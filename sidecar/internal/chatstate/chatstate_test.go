package chatstate

import (
	"bytes"
	"context"
	"errors"
	"fmt"
	"net"
	"path/filepath"
	"strings"
	"sync"
	"testing"
	"time"

	"github.com/rs/zerolog"
	"go.mau.fi/whatsmeow"
	"go.mau.fi/whatsmeow/appstate"
	"go.mau.fi/whatsmeow/proto/waAdv"
	"go.mau.fi/whatsmeow/proto/waSyncAction"
	"go.mau.fi/whatsmeow/store"
	"go.mau.fi/whatsmeow/types"
	"go.mau.fi/whatsmeow/types/events"
	"google.golang.org/genproto/googleapis/rpc/errdetails"
	"google.golang.org/grpc"
	"google.golang.org/grpc/codes"
	"google.golang.org/grpc/credentials/insecure"
	"google.golang.org/grpc/metadata"
	"google.golang.org/grpc/status"
	"google.golang.org/grpc/test/bufconn"
	"google.golang.org/protobuf/proto"
	"google.golang.org/protobuf/types/known/timestamppb"

	whatsmeowv1 "github.com/tcortega/Whatsmeow.NET/sidecar/gen/whatsmeow/v1"
	sidecarlog "github.com/tcortega/Whatsmeow.NET/sidecar/internal/log"
	"github.com/tcortega/Whatsmeow.NET/sidecar/internal/server"
	"github.com/tcortega/Whatsmeow.NET/sidecar/internal/session"
	sidecarstore "github.com/tcortega/Whatsmeow.NET/sidecar/internal/store"
)

const token = "00112233445566778899aabbccddeeff00112233445566778899aabbccddeeff"

// The chats of the tests; their users are what no status or log line may carry.
const (
	direct = "5511987654321@s.whatsapp.net"
	group  = "120363000000000001@g.us"
	member = "5511912345678@s.whatsapp.net"
)

// stub stands in for whatsmeow behind the slice's interface; the session methods it never needs stay nil.
type stub struct {
	session.Client

	mu sync.Mutex
	// sends are the patches sent; errs are what each send returns in turn, nil once they run out.
	sends []appstate.PatchInfo
	errs  []error
	// onSend runs inside each send, as whatsmeow dispatches AppStateSyncError inside a rejected one (appstate.go:176).
	onSend func()
	// decide, when set and it says so, gives a send's error in place of errs; it may wait.
	decide func(appstate.PatchInfo) (error, bool)
	// syncs gets each full sync's collection as it starts; syncErr is what each returns; block holds each until closed.
	syncs   chan appstate.WAPatchName
	syncErr error
	block   chan struct{}
}

func (s *stub) SendAppState(_ context.Context, patch appstate.PatchInfo) error {
	s.mu.Lock()
	s.sends = append(s.sends, patch)
	decide := s.decide
	s.mu.Unlock()

	if decide != nil {
		if err, ok := decide(patch); ok {
			return err
		}
	}

	s.mu.Lock()

	var err error
	if len(s.errs) > 0 {
		err, s.errs = s.errs[0], s.errs[1:]
	}

	onSend := s.onSend
	s.mu.Unlock()

	if onSend != nil {
		onSend()
	}

	return err
}

func (s *stub) FetchAppState(ctx context.Context, name appstate.WAPatchName, fullSync, _ bool) error {
	if !fullSync {
		return errors.New("only full syncs are expected")
	}

	s.syncs <- name

	if s.block != nil {
		select {
		case <-s.block:
		case <-ctx.Done():
			return fmt.Errorf("blocked: %w", ctx.Err())
		}
	}

	s.mu.Lock()
	defer s.mu.Unlock()

	return s.syncErr
}

func (s *stub) sent() []appstate.PatchInfo {
	s.mu.Lock()
	defer s.mu.Unlock()

	return append([]appstate.PatchInfo(nil), s.sends...)
}

// sessions stands in for the Sessions slice: one session, "s1", online with the stub unless offline is set, until
// the test closes online.
type sessions struct {
	client  session.Client
	device  *store.Device
	offline error
	online  chan struct{}
}

func (s *sessions) Online(id string) (session.Client, <-chan struct{}, error) {
	if id != "s1" {
		return nil, nil, fmt.Errorf("session %q: %w", id, server.ErrSessionNotFound)
	}

	if s.offline != nil {
		return nil, nil, s.offline
	}

	return s.client, s.online, nil
}

func (s *sessions) Device(id string) (*store.Device, error) {
	if id != "s1" {
		return nil, fmt.Errorf("session %q: %w", id, server.ErrSessionNotFound)
	}

	return s.device, nil
}

type harness struct {
	t        *testing.T
	stub     *stub
	sessions *sessions
	svc      *Service
	client   whatsmeowv1.ChatStateServiceClient
	ctx      context.Context //nolint:containedctx // the test's call context
	// returned gets the error of each SetChatState the server returned, in order.
	returned chan error
	// clock is the recoveries' now.
	clockMu sync.Mutex
	clock   time.Time
}

func start(t *testing.T) *harness {
	t.Helper()

	return startLogging(t, zerolog.Nop(), true)
}

// startLogging is start with the slice and the server logging to log, and the device with an app state key or not.
func startLogging(t *testing.T, log zerolog.Logger, withKey bool) *harness {
	t.Helper()

	db, err := sidecarstore.Open(context.Background(), filepath.Join(t.TempDir(), "whatsmeow-net.db"), zerolog.Nop())
	if err != nil {
		t.Fatalf("open the store: %v", err)
	}

	device := db.Devices.NewDevice()
	id := types.NewJID("5511000000000", types.DefaultUserServer)
	id.Device = 1
	device.ID = &id
	device.Account = &waAdv.ADVSignedDeviceIdentity{
		Details: []byte{}, AccountSignature: make([]byte, 64), AccountSignatureKey: make([]byte, 32), DeviceSignature: make([]byte, 64),
	}

	if err := device.Save(context.Background()); err != nil {
		t.Fatalf("save the device: %v", err)
	}

	if withKey {
		key := store.AppStateSyncKey{Data: make([]byte, 32), Fingerprint: []byte{}, Timestamp: 1}
		if err := device.AppStateKeys.PutAppStateSyncKey(context.Background(), []byte{1}, key); err != nil {
			t.Fatalf("store the app state key: %v", err)
		}
	}

	h := &harness{t: t, stub: &stub{syncs: make(chan appstate.WAPatchName, 16)}, returned: make(chan error, 16), clock: time.Unix(1_700_000_000, 0)}
	h.sessions = &sessions{client: h.stub, device: device, online: make(chan struct{})}
	h.svc = New(log)
	h.svc.now = h.now
	h.svc.Use(h.sessions)

	srv := server.New(token, log, sidecarlog.NewRedactor())
	whatsmeowv1.RegisterChatStateServiceServer(srv, &observed{Service: h.svc, returned: h.returned})

	lis := bufconn.Listen(1 << 20)
	served := make(chan error, 1)

	go func() { served <- srv.Serve(lis) }()

	conn, err := grpc.NewClient("passthrough:///bufnet",
		grpc.WithContextDialer(func(ctx context.Context, _ string) (net.Conn, error) { return lis.DialContext(ctx) }),
		grpc.WithTransportCredentials(insecure.NewCredentials()))
	if err != nil {
		t.Fatalf("dial: %v", err)
	}

	t.Cleanup(func() {
		_ = conn.Close()
		srv.Shutdown()
		srv.Stop()

		if err := <-served; err != nil {
			t.Errorf("serve: %v", err)
		}

		h.svc.Close()

		if err := db.Close(); err != nil {
			t.Errorf("close the store: %v", err)
		}
	})

	// A bound for a broken slice, never a wait: every wait below ends on a condition.
	ctx, cancel := context.WithTimeout(context.Background(), 30*time.Second)
	t.Cleanup(cancel)

	h.ctx = metadata.AppendToOutgoingContext(ctx, "authorization", "Bearer "+token)
	h.client = whatsmeowv1.NewChatStateServiceClient(conn)

	return h
}

// observed is the slice, telling the test when each call returned on the server.
type observed struct {
	*Service

	returned chan error
}

func (o *observed) SetChatState(ctx context.Context, req *whatsmeowv1.SetChatStateRequest) (*whatsmeowv1.SetChatStateResponse, error) {
	resp, err := o.Service.SetChatState(ctx, req)

	select {
	case o.returned <- err:
	default:
	}

	return resp, err
}

func (h *harness) now() time.Time {
	h.clockMu.Lock()
	defer h.clockMu.Unlock()

	return h.clock
}

func (h *harness) advance(d time.Duration) {
	h.clockMu.Lock()
	defer h.clockMu.Unlock()

	h.clock = h.clock.Add(d)
}

// set sends one change of the direct chat.
func (h *harness) set(req *whatsmeowv1.SetChatStateRequest) error {
	if req.SessionId == "" {
		req.SessionId = "s1"
	}

	if req.Chat == "" {
		req.Chat = direct
	}

	_, err := h.client.SetChatState(h.ctx, req)

	return err //nolint:wrapcheck // the test reads the status
}

// wantSync waits for the next full sync, which must be of the collection.
func (h *harness) wantSync(name appstate.WAPatchName) {
	h.t.Helper()

	select {
	case got := <-h.stub.syncs:
		if got != name {
			h.t.Fatalf("full sync of %q, want %q", got, name)
		}
	case <-h.ctx.Done():
		h.t.Fatalf("no full sync of %q", name)
	}
}

// waitIdle waits until the collection's full sync, of the count that started, ended; call it after wantSync.
func (h *harness) waitIdle(name appstate.WAPatchName, count uint64) {
	h.t.Helper()

	rec := h.svc.recovery("s1")

	rec.mu.Lock()
	col := rec.collections[name]
	if col == nil || col.started != count {
		rec.mu.Unlock()
		h.t.Fatalf("%q has no full sync number %d", name, count)
	}

	running := col.running
	rec.mu.Unlock()

	if running == nil {
		return
	}

	select {
	case <-running.done:
	case <-h.ctx.Done():
		h.t.Fatalf("the full sync of %q did not end", name)
	}
}

// wantNoSync fails if a full sync started.
func (h *harness) wantNoSync() {
	h.t.Helper()

	select {
	case got := <-h.stub.syncs:
		h.t.Fatalf("unexpected full sync of %q", got)
	default:
	}
}

// wantReason fails unless err is a status of code with one ErrorInfo of reason and metadata.
func wantReason(t *testing.T, err error, code codes.Code, reason string, metadata map[string]string) {
	t.Helper()

	st, _ := status.FromError(err)
	if st.Code() != code || len(st.Details()) != 1 {
		t.Fatalf("error = %v, want %v with one ErrorInfo", err, code)
	}

	info, _ := st.Details()[0].(*errdetails.ErrorInfo)
	if info.GetReason() != reason || fmt.Sprint(info.GetMetadata()) != fmt.Sprint(metadata) {
		t.Fatalf("ErrorInfo = %v, want %s with %v", info, reason, metadata)
	}
}

// heldBack is what SendAppState returns when WhatsApp took the write and the sync after it met whatsmeow's backoff
// after a 429 (appstate.go:48-50, 636-638).
func heldBack(name appstate.WAPatchName) error {
	return fmt.Errorf("failed to fetch app state after sending update: %w",
		fmt.Errorf("%w: not fetching %s until %s", whatsmeow.ErrAppStateRateLimited, name, "2026-11-06T15:00:00Z"))
}

// rejected is what whatsmeow's SendAppState returns when WhatsApp answered 409 and the patches it sent with it do not
// verify against the session's copy (appstate.go:612-623, appstate/decode.go:254), with the chat's JID in its text as
// a node of WhatsApp's could carry one.
func rejected(name appstate.WAPatchName) error {
	mainErr := fmt.Errorf("%w (%s): %s", whatsmeow.ErrAppStateUpdate, name, `<error code="409" text="conflict" jid="`+direct+`"/>`)
	decodeErr := fmt.Errorf("failed to decode app state %s patches: %w", name, fmt.Errorf("failed to verify patch v7: %w", appstate.ErrMismatchingLTHash))

	return fmt.Errorf("%w (also, applying patches in the response failed: %w)", mainErr, decodeErr)
}

// value is a change's value without its message range, which carries the time whatsmeow sends it at.
func value(patch appstate.MutationInfo) *waSyncAction.SyncActionValue {
	v := proto.CloneOf(patch.Value)
	if a := v.GetArchiveChatAction(); a != nil {
		a.MessageRange = nil
	}

	if r := v.GetMarkChatAsReadAction(); r != nil {
		r.MessageRange = nil
	}

	return v
}

func TestEachChangeSendsWhatsmeowsPatch(t *testing.T) {
	t.Parallel()

	until := time.Date(2027, 1, 2, 3, 4, 5, 678_000_000, time.UTC)
	end := until.UnixMilli()
	forever := int64(-1)

	type want struct {
		index []string
		value *waSyncAction.SyncActionValue
	}

	for _, tc := range []struct {
		name       string
		req        *whatsmeowv1.SetChatStateRequest
		collection appstate.WAPatchName
		want       []want
	}{
		{
			"mute until", &whatsmeowv1.SetChatStateRequest{Change: &whatsmeowv1.SetChatStateRequest_Mute{Mute: &whatsmeowv1.ChatMute{Muted: true, Until: timestamppb.New(until)}}}, appstate.WAPatchRegularHigh,
			[]want{{[]string{"mute", direct}, &waSyncAction.SyncActionValue{MuteAction: &waSyncAction.MuteAction{Muted: proto.Bool(true), MuteEndTimestamp: &end}}}},
		},
		{
			"mute forever", &whatsmeowv1.SetChatStateRequest{Change: &whatsmeowv1.SetChatStateRequest_Mute{Mute: &whatsmeowv1.ChatMute{Muted: true}}}, appstate.WAPatchRegularHigh,
			[]want{{[]string{"mute", direct}, &waSyncAction.SyncActionValue{MuteAction: &waSyncAction.MuteAction{Muted: proto.Bool(true), MuteEndTimestamp: &forever}}}},
		},
		{
			"unmute", &whatsmeowv1.SetChatStateRequest{Change: &whatsmeowv1.SetChatStateRequest_Mute{Mute: &whatsmeowv1.ChatMute{}}}, appstate.WAPatchRegularHigh,
			[]want{{[]string{"mute", direct}, &waSyncAction.SyncActionValue{MuteAction: &waSyncAction.MuteAction{Muted: proto.Bool(false)}}}},
		},
		{
			"pin", &whatsmeowv1.SetChatStateRequest{Change: &whatsmeowv1.SetChatStateRequest_Pin{Pin: &whatsmeowv1.ChatPin{Pinned: true}}}, appstate.WAPatchRegularLow,
			[]want{{[]string{"pin_v1", direct}, &waSyncAction.SyncActionValue{PinAction: &waSyncAction.PinAction{Pinned: proto.Bool(true)}}}},
		},
		{
			"unpin", &whatsmeowv1.SetChatStateRequest{Change: &whatsmeowv1.SetChatStateRequest_Pin{Pin: &whatsmeowv1.ChatPin{}}}, appstate.WAPatchRegularLow,
			[]want{{[]string{"pin_v1", direct}, &waSyncAction.SyncActionValue{PinAction: &waSyncAction.PinAction{Pinned: proto.Bool(false)}}}},
		},
		// Archiving unpins too (appstate/encode.go:111-113).
		{
			"archive", &whatsmeowv1.SetChatStateRequest{Change: &whatsmeowv1.SetChatStateRequest_Archive{Archive: &whatsmeowv1.ChatArchive{Archived: true}}}, appstate.WAPatchRegularLow,
			[]want{
				{[]string{"archive", direct}, &waSyncAction.SyncActionValue{ArchiveChatAction: &waSyncAction.ArchiveChatAction{Archived: proto.Bool(true)}}},
				{[]string{"pin_v1", direct}, &waSyncAction.SyncActionValue{PinAction: &waSyncAction.PinAction{Pinned: proto.Bool(false)}}},
			},
		},
		{
			"unarchive", &whatsmeowv1.SetChatStateRequest{Change: &whatsmeowv1.SetChatStateRequest_Archive{Archive: &whatsmeowv1.ChatArchive{}}}, appstate.WAPatchRegularLow,
			[]want{{[]string{"archive", direct}, &waSyncAction.SyncActionValue{ArchiveChatAction: &waSyncAction.ArchiveChatAction{Archived: proto.Bool(false)}}}},
		},
		{
			"read", &whatsmeowv1.SetChatStateRequest{Change: &whatsmeowv1.SetChatStateRequest_Read{Read: &whatsmeowv1.ChatRead{Read: true}}}, appstate.WAPatchRegularLow,
			[]want{{[]string{"markChatAsRead", direct}, &waSyncAction.SyncActionValue{MarkChatAsReadAction: &waSyncAction.MarkChatAsReadAction{Read: proto.Bool(true)}}}},
		},
		{
			"unread", &whatsmeowv1.SetChatStateRequest{Change: &whatsmeowv1.SetChatStateRequest_Read{Read: &whatsmeowv1.ChatRead{}}}, appstate.WAPatchRegularLow,
			[]want{{[]string{"markChatAsRead", direct}, &waSyncAction.SyncActionValue{MarkChatAsReadAction: &waSyncAction.MarkChatAsReadAction{Read: proto.Bool(false)}}}},
		},
		// The chat's device is dropped.
		{
			"star in a direct chat", &whatsmeowv1.SetChatStateRequest{Chat: "5511987654321:3@s.whatsapp.net", Change: &whatsmeowv1.SetChatStateRequest_Star{Star: &whatsmeowv1.MessageStar{MessageId: "M1", Starred: true}}}, appstate.WAPatchRegularHigh,
			[]want{{[]string{"star", direct, "M1", "0", "0"}, &waSyncAction.SyncActionValue{StarAction: &waSyncAction.StarAction{Starred: proto.Bool(true)}}}},
		},
		{
			"unstar the account's own message", &whatsmeowv1.SetChatStateRequest{Change: &whatsmeowv1.SetChatStateRequest_Star{Star: &whatsmeowv1.MessageStar{MessageId: "M2", IsFromMe: true, Sender: member}}}, appstate.WAPatchRegularHigh,
			[]want{{[]string{"star", direct, "M2", "1", "0"}, &waSyncAction.SyncActionValue{StarAction: &waSyncAction.StarAction{Starred: proto.Bool(false)}}}},
		},
		// A group message from someone else names its sender, without the device.
		{
			"star in a group", &whatsmeowv1.SetChatStateRequest{Chat: group, Change: &whatsmeowv1.SetChatStateRequest_Star{Star: &whatsmeowv1.MessageStar{MessageId: "M3", Sender: "5511912345678:2@s.whatsapp.net", Starred: true}}}, appstate.WAPatchRegularHigh,
			[]want{{[]string{"star", group, "M3", "0", member}, &waSyncAction.SyncActionValue{StarAction: &waSyncAction.StarAction{Starred: proto.Bool(true)}}}},
		},
		{
			"star the account's own message in a group", &whatsmeowv1.SetChatStateRequest{Chat: group, Change: &whatsmeowv1.SetChatStateRequest_Star{Star: &whatsmeowv1.MessageStar{MessageId: "M4", IsFromMe: true, Starred: true}}}, appstate.WAPatchRegularHigh,
			[]want{{[]string{"star", group, "M4", "1", "0"}, &waSyncAction.SyncActionValue{StarAction: &waSyncAction.StarAction{Starred: proto.Bool(true)}}}},
		},
	} {
		t.Run(tc.name, func(t *testing.T) {
			t.Parallel()

			h := start(t)
			if err := h.set(tc.req); err != nil {
				t.Fatalf("SetChatState: %v", err)
			}

			sent := h.stub.sent()
			if len(sent) != 1 || sent[0].Type != tc.collection || len(sent[0].Mutations) != len(tc.want) {
				t.Fatalf("sent %+v, want one patch of %s with %d mutations", sent, tc.collection, len(tc.want))
			}

			for i, w := range tc.want {
				got := sent[0].Mutations[i]
				if strings.Join(got.Index, " ") != strings.Join(w.index, " ") || !proto.Equal(value(got), w.value) {
					t.Fatalf("mutation %d = %v %v, want %v %v", i, got.Index, value(got), w.index, w.value)
				}
			}
		})
	}
}

func TestAnInvalidRequestIsInvalidArgumentAndSendsNothing(t *testing.T) {
	t.Parallel()

	star := func(s *whatsmeowv1.MessageStar) *whatsmeowv1.SetChatStateRequest_Star {
		return &whatsmeowv1.SetChatStateRequest_Star{Star: s}
	}
	pin := &whatsmeowv1.SetChatStateRequest_Pin{Pin: &whatsmeowv1.ChatPin{Pinned: true}}

	for _, tc := range []struct {
		name, field string
		req         *whatsmeowv1.SetChatStateRequest
	}{
		{"a chat that does not parse", "chat", &whatsmeowv1.SetChatStateRequest{Chat: "not a jid@", Change: pin}},
		{"a chat that is neither a user nor a group", "chat", &whatsmeowv1.SetChatStateRequest{Chat: "120363000000000002@newsletter", Change: pin}},
		{"no change", "change", &whatsmeowv1.SetChatStateRequest{}},
		// At or before the Unix epoch is no end whatsmeow can send: 0 or less reads as forever (appstate.go:280-284).
		{"a mute that ends at the epoch", "until", &whatsmeowv1.SetChatStateRequest{Change: &whatsmeowv1.SetChatStateRequest_Mute{Mute: &whatsmeowv1.ChatMute{Muted: true, Until: timestamppb.New(time.Unix(0, 0))}}}},
		{"a mute that ends in year 1", "until", &whatsmeowv1.SetChatStateRequest{Change: &whatsmeowv1.SetChatStateRequest_Mute{Mute: &whatsmeowv1.ChatMute{Muted: true, Until: timestamppb.New(time.Time{})}}}},
		{"a star without a message id", "message_id", &whatsmeowv1.SetChatStateRequest{Change: star(&whatsmeowv1.MessageStar{Starred: true})}},
		{"a group star from someone else without a sender", "sender", &whatsmeowv1.SetChatStateRequest{Chat: group, Change: star(&whatsmeowv1.MessageStar{MessageId: "M1"})}},
		{"a group star from a sender that is not a user", "sender", &whatsmeowv1.SetChatStateRequest{Chat: group, Change: star(&whatsmeowv1.MessageStar{MessageId: "M1", Sender: group})}},
	} {
		t.Run(tc.name, func(t *testing.T) {
			t.Parallel()

			h := start(t)
			wantReason(t, h.set(tc.req), codes.InvalidArgument, "INVALID_ARGUMENT", map[string]string{"field": tc.field})

			if sent := h.stub.sent(); len(sent) != 0 {
				t.Fatalf("sent %v", sent)
			}
		})
	}
}

func TestASessionNotOnlineFailsAtOnceWithItsReason(t *testing.T) {
	t.Parallel()

	h := start(t)
	h.sessions.offline = fmt.Errorf("session %q: %w", "s1", server.ErrNotConnected)

	wantReason(t, h.set(&whatsmeowv1.SetChatStateRequest{Change: &whatsmeowv1.SetChatStateRequest_Pin{Pin: &whatsmeowv1.ChatPin{}}}),
		codes.Unavailable, "NOT_CONNECTED", map[string]string{})

	if sent := h.stub.sent(); len(sent) != 0 {
		t.Fatalf("sent %v", sent)
	}
}

func TestBeforeThePhoneSharedTheAppStateKeyAWriteIsNotConnected(t *testing.T) {
	t.Parallel()

	h := startLogging(t, zerolog.Nop(), false)

	wantReason(t, h.set(&whatsmeowv1.SetChatStateRequest{Change: &whatsmeowv1.SetChatStateRequest_Pin{Pin: &whatsmeowv1.ChatPin{}}}),
		codes.Unavailable, "NOT_CONNECTED", map[string]string{})

	if sent := h.stub.sent(); len(sent) != 0 {
		t.Fatalf("sent %v", sent)
	}
}

func TestAWriteRejectedForACopyOutOfStepIsSentOnceMoreAfterAFullSync(t *testing.T) {
	t.Parallel()

	h := start(t)
	h.stub.errs = []error{rejected(appstate.WAPatchRegularLow)}

	if err := h.set(&whatsmeowv1.SetChatStateRequest{Change: &whatsmeowv1.SetChatStateRequest_Pin{Pin: &whatsmeowv1.ChatPin{Pinned: true}}}); err != nil {
		t.Fatalf("SetChatState: %v", err)
	}

	h.wantSync(appstate.WAPatchRegularLow)
	h.wantNoSync()

	if sent := h.stub.sent(); len(sent) != 2 || !proto.Equal(sent[0].Mutations[0].Value, sent[1].Mutations[0].Value) {
		t.Fatalf("sent %v, want the same patch twice", sent)
	}
}

func TestAWriteRejectedAgainAfterTheRecoveryIsAConflictAndIsNotSentAThirdTime(t *testing.T) {
	t.Parallel()

	h := start(t)
	h.stub.errs = []error{rejected(appstate.WAPatchRegularHigh), rejected(appstate.WAPatchRegularHigh)}

	wantReason(t, h.set(&whatsmeowv1.SetChatStateRequest{Change: &whatsmeowv1.SetChatStateRequest_Mute{Mute: &whatsmeowv1.ChatMute{Muted: true}}}),
		codes.FailedPrecondition, "WHATSAPP_ERROR", map[string]string{"code": "409"})

	h.wantSync(appstate.WAPatchRegularHigh)
	h.wantNoSync()

	if sent := h.stub.sent(); len(sent) != 2 {
		t.Fatalf("sent %d patches, want 2", len(sent))
	}
}

func TestAWriteWhoseRecoveryFailsIsAConflict(t *testing.T) {
	t.Parallel()

	h := start(t)
	h.stub.errs = []error{rejected(appstate.WAPatchRegularLow)}
	h.stub.syncErr = fmt.Errorf("failed to decode app state regular_low patches: failed to verify snapshot: %w", appstate.ErrMismatchingLTHash)

	wantReason(t, h.set(&whatsmeowv1.SetChatStateRequest{Change: &whatsmeowv1.SetChatStateRequest_Pin{Pin: &whatsmeowv1.ChatPin{Pinned: true}}}),
		codes.FailedPrecondition, "WHATSAPP_ERROR", map[string]string{"code": "409"})

	h.wantSync(appstate.WAPatchRegularLow)

	if sent := h.stub.sent(); len(sent) != 1 {
		t.Fatalf("sent %d patches, want 1", len(sent))
	}
}

func TestARejectedWriteWhoseRecoveryWhatsAppHoldsBackIsRateLimited(t *testing.T) {
	t.Parallel()

	h := start(t)
	h.stub.errs = []error{rejected(appstate.WAPatchRegularLow)}
	h.stub.syncErr = fmt.Errorf("%w: not fetching %s until %s", whatsmeow.ErrAppStateRateLimited, appstate.WAPatchRegularLow, "2026-11-06T15:00:00Z")

	err := h.set(&whatsmeowv1.SetChatStateRequest{Change: &whatsmeowv1.SetChatStateRequest_Pin{Pin: &whatsmeowv1.ChatPin{Pinned: true}}})
	wantReason(t, err, codes.ResourceExhausted, "RATE_LIMITED", map[string]string{})

	for _, wrapped := range []string{"not fetching", "2026-11-06T15:00:00Z", "5511987654321"} {
		if strings.Contains(status.Convert(err).Message(), wrapped) {
			t.Fatalf("the status carries %q: %v", wrapped, err)
		}
	}

	h.wantSync(appstate.WAPatchRegularLow)
	h.wantNoSync()

	if sent := h.stub.sent(); len(sent) != 1 {
		t.Fatalf("sent %d patches, want 1: the recovery did not run, so the write is not sent again", len(sent))
	}
}

func TestAWriteTakenWhoseSyncFoundTheCopyOutOfStepIsDoneAfterAFullSync(t *testing.T) {
	t.Parallel()

	h := start(t)
	// WhatsApp took it; the sync after it failed (appstate.go:636-638).
	h.stub.errs = []error{fmt.Errorf("failed to fetch app state after sending update: %w", fmt.Errorf("failed to verify patch v8: %w", appstate.ErrMismatchingPatchMAC))}

	if err := h.set(&whatsmeowv1.SetChatStateRequest{Change: &whatsmeowv1.SetChatStateRequest_Read{Read: &whatsmeowv1.ChatRead{Read: true}}}); err != nil {
		t.Fatalf("SetChatState: %v", err)
	}

	h.wantSync(appstate.WAPatchRegularLow)

	if sent := h.stub.sent(); len(sent) != 1 {
		t.Fatalf("sent %d patches, want 1: it was taken", len(sent))
	}
}

func TestAWriteTakenWhoseSyncWhatsAppHoldsBackIsDoneWithoutAnotherSendOrSync(t *testing.T) {
	t.Parallel()

	for _, tc := range []struct {
		name   string
		closed bool
		errs   []error
		sends  int
		syncs  int
	}{
		{"on the first send", false, []error{heldBack(appstate.WAPatchRegularLow)}, 1, 0},
		{"on the send after a recovery", false, []error{rejected(appstate.WAPatchRegularLow), heldBack(appstate.WAPatchRegularLow)}, 2, 1},
		// A write while the slice closes has no recovery to join.
		{"once the slice closed", true, []error{heldBack(appstate.WAPatchRegularLow)}, 1, 0},
	} {
		t.Run(tc.name, func(t *testing.T) {
			t.Parallel()

			var logs syncBuffer

			h := startLogging(t, zerolog.New(&logs), true)
			h.stub.errs = tc.errs

			if tc.closed {
				h.svc.Close()
			}

			if err := h.set(&whatsmeowv1.SetChatStateRequest{Change: &whatsmeowv1.SetChatStateRequest_Pin{Pin: &whatsmeowv1.ChatPin{Pinned: true}}}); err != nil {
				t.Fatalf("SetChatState: %v", err)
			}

			for range tc.syncs {
				h.wantSync(appstate.WAPatchRegularLow)
			}

			h.wantNoSync()

			if sent := h.stub.sent(); len(sent) != tc.sends {
				t.Fatalf("sent %d patches, want %d: it was taken", len(sent), tc.sends)
			}

			logs.waitFor(h.ctx, t, `"error":"app state fetch is rate limited by the server","message":"a chat state write was taken, but WhatsApp holds back the sync after it"`, 1)

			for _, wrapped := range []string{"not fetching", "2026-11-06T15:00:00Z"} {
				if strings.Contains(logs.String(), wrapped) {
					t.Fatalf("a log line carries %q: %s", wrapped, logs.String())
				}
			}
		})
	}
}

func TestAWriteWaitsForAFullSyncThatStartedAfterItWasSentAndAFailedSyncForNoneWithinTheCooldownOfAFailedOne(t *testing.T) {
	t.Parallel()

	at := time.Unix(1_700_000_000, 0)
	rec := &recovery{collections: map[appstate.WAPatchName]*collection{}, pending: map[appstate.WAPatchName]uint64{}}
	name := appstate.WAPatchRegularLow

	// A failed sync starts one; a write sent before it started joins it while it runs.
	worker, start := rec.join(name, 0, at, false)
	if !start {
		t.Fatal("the first failed sync started no full sync")
	}

	if joined, start := rec.join(name, 0, at, true); start || joined != worker {
		t.Fatal("a write did not join the running full sync")
	}

	if _, start := rec.join(name, 0, at, false); start {
		t.Fatal("a failed sync started a second full sync while one runs")
	}

	rec.mu.Lock()
	worker.err = appstate.ErrMismatchingLTHash
	rec.collections[name].running, rec.collections[name].finished = nil, worker
	close(worker.done)
	rec.mu.Unlock()

	// Once it ended, a write sent before it started takes its result; one sent after it starts its own, cooldown or
	// not.
	if got, start := rec.join(name, 0, at, true); start || got != worker {
		t.Fatal("a write sent before the full sync did not take its result")
	}

	write, start := rec.join(name, rec.seen(name), at, true)
	if !start || write == worker {
		t.Fatal("a write sent after the full sync started none of its own")
	}

	rec.mu.Lock()
	write.err = appstate.ErrMismatchingLTHash
	rec.collections[name].running, rec.collections[name].finished = nil, write
	close(write.done)
	rec.mu.Unlock()

	// A failed sync noted before the last full sync started starts none: that one answered it.
	if _, start := rec.join(name, 0, at.Add(2*cooldown), false); start {
		t.Fatal("a failed sync from before the last full sync started another")
	}

	// One noted after it starts none within the cooldown of a full sync that failed, and one after it.
	if _, start := rec.join(name, rec.seen(name), at.Add(cooldown-time.Nanosecond), false); start {
		t.Fatal("a failed sync within the cooldown of a failed full sync started a full sync")
	}

	again, start := rec.join(name, rec.seen(name), at.Add(cooldown), false)
	if !start {
		t.Fatal("a failed sync after the cooldown started no full sync")
	}

	// After a full sync that succeeded, the next failed sync starts one at once.
	rec.mu.Lock()
	rec.collections[name].running, rec.collections[name].finished = nil, again
	close(again.done)
	rec.mu.Unlock()

	if _, start := rec.join(name, rec.seen(name), at.Add(cooldown), false); !start {
		t.Fatal("a failed sync right after a full sync that succeeded started none")
	}
}

func TestAWriteAndTheFailedSyncItDispatchedRecoverWithOneFullSync(t *testing.T) {
	t.Parallel()

	h := start(t)
	h.stub.errs = []error{rejected(appstate.WAPatchRegularLow)}
	// whatsmeow dispatches the decode failure inside the rejected send, the first (appstate.go:622, 176), which wakes
	// the worker.
	var once sync.Once
	h.stub.onSend = func() {
		once.Do(func() {
			h.svc.Handler("s1")(&events.AppStateSyncError{Name: appstate.WAPatchRegularLow, Error: appstate.ErrMismatchingLTHash})
		})
	}

	if err := h.set(&whatsmeowv1.SetChatStateRequest{Change: &whatsmeowv1.SetChatStateRequest_Pin{Pin: &whatsmeowv1.ChatPin{Pinned: true}}}); err != nil {
		t.Fatalf("SetChatState: %v", err)
	}

	h.wantSync(appstate.WAPatchRegularLow)

	// The worker's turn has passed once a later signal of another collection is synced: the first was synced once.
	h.svc.Handler("s1")(&events.AppStateSyncError{Name: appstate.WAPatchCriticalBlock})
	h.wantSync(appstate.WAPatchCriticalBlock)
	h.wantNoSync()
}

func TestWhatsmeowsErrorsBecomeTheirReasonsWithoutAJIDAndAreNotRecovered(t *testing.T) {
	t.Parallel()

	for _, tc := range []struct {
		name     string
		err      error
		code     codes.Code
		reason   string
		metadata map[string]string
	}{
		{"no answer in time", fmt.Errorf("sending %s: %w", direct, whatsmeow.ErrIQTimedOut), codes.Unknown, "OUTCOME_UNKNOWN", map[string]string{}},
		{"a disconnect while waiting", &whatsmeow.DisconnectedError{Action: "info query"}, codes.Unknown, "OUTCOME_UNKNOWN", map[string]string{}},
		{"not connected", whatsmeow.ErrNotConnected, codes.Unavailable, "NOT_CONNECTED", map[string]string{}},
		{"rate limited", &whatsmeow.IQError{Code: 429, Text: "rate-overlimit"}, codes.ResourceExhausted, "RATE_LIMITED", map[string]string{}},
		// Taken, and the sync after it got the 429 that starts a backoff: wrapped like the write's own (appstate.go:84-89,
		// 636-638).
		{"rate limited on the sync after a taken write", fmt.Errorf("failed to fetch app state after sending update: %w", fmt.Errorf("failed to fetch app state %s patches: %w", appstate.WAPatchRegularLow, &whatsmeow.IQError{Code: 429, Text: "rate-overlimit"})), codes.ResourceExhausted, "RATE_LIMITED", map[string]string{}},
		{"an IQ error", &whatsmeow.IQError{Code: 500, Text: "internal-server-error"}, codes.FailedPrecondition, "WHATSAPP_ERROR", map[string]string{"code": "500"}},
		// A conflict whatsmeow's own retry did not get past (appstate.go:616, 631), with WhatsApp's node in its text.
		{"a rejected write", fmt.Errorf("%w (regular_low): <error code=\"409\" jid=\"%s\"/>", whatsmeow.ErrAppStateUpdate, direct), codes.FailedPrecondition, "WHATSAPP_ERROR", map[string]string{}},
		// A missing key is whatsmeow's to ask the phone for; a full sync does not fix it (appstate.go:173-175).
		{"a missing key", fmt.Errorf("decode %s: %w", direct, appstate.ErrKeyNotFound), codes.Internal, "INTERNAL", map[string]string{}},
	} {
		t.Run(tc.name, func(t *testing.T) {
			t.Parallel()

			h := start(t)
			h.stub.errs = []error{tc.err}

			err := h.set(&whatsmeowv1.SetChatStateRequest{Change: &whatsmeowv1.SetChatStateRequest_Pin{Pin: &whatsmeowv1.ChatPin{Pinned: true}}})
			wantReason(t, err, tc.code, tc.reason, tc.metadata)

			if strings.Contains(status.Convert(err).Message(), "5511987654321") {
				t.Fatalf("the status carries the chat: %v", err)
			}

			h.wantNoSync()

			if sent := h.stub.sent(); len(sent) != 1 {
				t.Fatalf("sent %d patches, want 1", len(sent))
			}
		})
	}
}

func TestNoLogLineCarriesAJID(t *testing.T) {
	t.Parallel()

	var logs syncBuffer

	h := startLogging(t, zerolog.New(&logs), true)
	h.stub.errs = []error{rejected(appstate.WAPatchRegularLow), fmt.Errorf("%s: %w", direct, appstate.ErrKeyNotFound)}
	h.stub.syncErr = fmt.Errorf("verify the snapshot of %s: %w", direct, appstate.ErrMismatchingLTHash)

	for range 2 {
		_ = h.set(&whatsmeowv1.SetChatStateRequest{Change: &whatsmeowv1.SetChatStateRequest_Pin{Pin: &whatsmeowv1.ChatPin{Pinned: true}}})
	}

	h.svc.Handler("s1")(&events.AppStateSyncError{Name: appstate.WAPatchRegularHigh})
	h.wantSync(appstate.WAPatchRegularLow)
	h.wantSync(appstate.WAPatchRegularHigh)

	// Both full syncs failed and said so: the write's, then the worker's.
	logs.waitFor(h.ctx, t, "a full sync of an app state collection failed", 2)

	if strings.Contains(logs.String(), "5511987654321") {
		t.Fatalf("a log line carries the chat: %s", logs.String())
	}
}

func TestAFailedSyncIsRecoveredByTheSessionsWorkerOnceWithinTheCooldownOfAFailedOne(t *testing.T) {
	t.Parallel()

	h := start(t)
	// Each full sync fails, and dispatches another error (appstate.go:85-86, 176).
	h.stub.syncErr = appstate.ErrMismatchingLTHash
	handle := h.svc.Handler("s1")

	if !handle(&events.AppStateSyncError{Name: appstate.WAPatchRegularHigh, Error: appstate.ErrMismatchingLTHash}) {
		t.Fatal("the handler failed")
	}

	h.wantSync(appstate.WAPatchRegularHigh)
	h.waitIdle(appstate.WAPatchRegularHigh, 1)

	// The full sync's own failure, and more within the cooldown, start none. The worker takes the signals in order,
	// one collection at a time, so the next sync is the other collection's.
	handle(&events.AppStateSyncError{Name: appstate.WAPatchRegularHigh, FullSync: true})
	h.advance(cooldown - time.Second)
	handle(&events.AppStateSyncError{Name: appstate.WAPatchRegularHigh})
	handle(&events.AppStateSyncError{Name: appstate.WAPatchRegularLow})
	h.wantSync(appstate.WAPatchRegularLow)
	h.waitIdle(appstate.WAPatchRegularLow, 1)
	h.wantNoSync()

	// Once it passed, the next failure is recovered.
	h.advance(time.Second)
	handle(&events.AppStateSyncError{Name: appstate.WAPatchRegularHigh})
	h.wantSync(appstate.WAPatchRegularHigh)
}

func TestARecoveryWhoseFullSyncWhatsAppHoldsBackFailedAndWaitsForTheCooldown(t *testing.T) {
	t.Parallel()

	var logs syncBuffer

	h := startLogging(t, zerolog.New(&logs), true)
	// whatsmeow returns the backoff from the full sync itself, before it deletes the copy's version (appstate.go:48-50).
	h.stub.syncErr = fmt.Errorf("%w: not fetching %s until %s", whatsmeow.ErrAppStateRateLimited, appstate.WAPatchRegularHigh, "2026-11-06T15:00:00Z")
	handle := h.svc.Handler("s1")

	handle(&events.AppStateSyncError{Name: appstate.WAPatchRegularHigh})
	h.wantSync(appstate.WAPatchRegularHigh)
	h.waitIdle(appstate.WAPatchRegularHigh, 1)

	logs.waitFor(h.ctx, t, `"error":"app state fetch is rate limited by the server","message":"a full sync of an app state collection failed"`, 1)

	for _, wrapped := range []string{"not fetching", "2026-11-06T15:00:00Z"} {
		if strings.Contains(logs.String(), wrapped) {
			t.Fatalf("a log line carries %q: %s", wrapped, logs.String())
		}
	}

	// It failed, so another failure within the cooldown starts none.
	h.advance(cooldown - time.Second)
	handle(&events.AppStateSyncError{Name: appstate.WAPatchRegularHigh})
	handle(&events.AppStateSyncError{Name: appstate.WAPatchRegularLow})
	h.wantSync(appstate.WAPatchRegularLow)
	h.waitIdle(appstate.WAPatchRegularLow, 1)
	h.wantNoSync()
}

func TestAFailedSyncAfterASuccessfulRecoveryIsRecoveredWithinTheHour(t *testing.T) {
	t.Parallel()

	h := start(t)
	handle := h.svc.Handler("s1")

	handle(&events.AppStateSyncError{Name: appstate.WAPatchRegularHigh})
	h.wantSync(appstate.WAPatchRegularHigh)
	h.waitIdle(appstate.WAPatchRegularHigh, 1)

	// The copy got out of step again a minute after a recovery that succeeded: no cooldown holds it back.
	h.advance(time.Minute)
	handle(&events.AppStateSyncError{Name: appstate.WAPatchRegularHigh})
	h.wantSync(appstate.WAPatchRegularHigh)
}

func TestAJoinedWriteStillRecoversWhenTheWriteThatStartedTheFullSyncIsCancelled(t *testing.T) {
	t.Parallel()

	h := start(t)
	h.stub.block = make(chan struct{})
	archiveSent, archiveGo := make(chan struct{}), make(chan struct{})
	archives := 0
	// The archive is sent before the full sync starts, and answered once the pin started it and gave up.
	h.stub.decide = func(patch appstate.PatchInfo) (error, bool) {
		if patch.Mutations[0].Index[0] != appstate.IndexArchive {
			return nil, false
		}

		h.stub.mu.Lock()
		archives++
		first := archives == 1
		h.stub.mu.Unlock()

		if !first {
			return nil, true
		}

		close(archiveSent)
		<-archiveGo

		return rejected(appstate.WAPatchRegularLow), true
	}
	h.stub.errs = []error{rejected(appstate.WAPatchRegularLow)}

	archived := make(chan error, 1)

	go func() {
		archived <- h.set(&whatsmeowv1.SetChatStateRequest{Change: &whatsmeowv1.SetChatStateRequest_Archive{Archive: &whatsmeowv1.ChatArchive{Archived: true}}})
	}()

	<-archiveSent

	pinCtx, cancelPin := context.WithCancel(h.ctx)
	pinned := make(chan error, 1)

	go func() {
		_, err := h.client.SetChatState(pinCtx, &whatsmeowv1.SetChatStateRequest{SessionId: "s1", Chat: direct, Change: &whatsmeowv1.SetChatStateRequest_Pin{Pin: &whatsmeowv1.ChatPin{Pinned: true}}})
		pinned <- err
	}()

	h.wantSync(appstate.WAPatchRegularLow)
	cancelPin()

	if err := <-pinned; status.Code(err) != codes.Canceled {
		t.Fatalf("the cancelled pin = %v, want Canceled", err)
	}

	// The server gave the pin up too.
	if err := <-h.returned; !errors.Is(err, context.Canceled) {
		t.Fatalf("the server's pin = %v, want Canceled", err)
	}

	// The full sync goes on without the pin, and the archive, rejected meanwhile, takes its result.
	close(archiveGo)
	close(h.stub.block)

	if err := <-archived; err != nil {
		t.Fatalf("the archive = %v, want it recovered and sent again", err)
	}

	h.wantNoSync()
}

func TestAWritesFullSyncEndsWhenTheSessionLeavesOnline(t *testing.T) {
	t.Parallel()

	h := start(t)
	h.stub.block = make(chan struct{})
	h.stub.errs = []error{rejected(appstate.WAPatchRegularLow)}

	pinned := make(chan error, 1)

	go func() {
		pinned <- h.set(&whatsmeowv1.SetChatStateRequest{Change: &whatsmeowv1.SetChatStateRequest_Pin{Pin: &whatsmeowv1.ChatPin{Pinned: true}}})
	}()

	h.wantSync(appstate.WAPatchRegularLow)
	close(h.sessions.online)

	wantReason(t, <-pinned, codes.FailedPrecondition, "WHATSAPP_ERROR", map[string]string{"code": "409"})
	close(h.stub.block)
}

func TestARetryTakenWhoseSyncFoundTheCopyOutOfStepIsDoneAfterAnotherFullSync(t *testing.T) {
	t.Parallel()

	h := start(t)
	// Rejected, then taken on the retry with the sync after it failing (appstate.go:636-638).
	h.stub.errs = []error{
		rejected(appstate.WAPatchRegularLow),
		fmt.Errorf("failed to fetch app state after sending update: %w", fmt.Errorf("failed to verify patch v9: %w", appstate.ErrMismatchingLTHash)),
	}

	if err := h.set(&whatsmeowv1.SetChatStateRequest{Change: &whatsmeowv1.SetChatStateRequest_Pin{Pin: &whatsmeowv1.ChatPin{Pinned: true}}}); err != nil {
		t.Fatalf("SetChatState: %v", err)
	}

	h.wantSync(appstate.WAPatchRegularLow)
	h.wantSync(appstate.WAPatchRegularLow)

	if sent := h.stub.sent(); len(sent) != 2 {
		t.Fatalf("sent %d patches, want 2", len(sent))
	}
}

func TestTheHandlerNeverWaitsForARecovery(t *testing.T) {
	t.Parallel()

	h := start(t)
	h.stub.block = make(chan struct{})
	handle := h.svc.Handler("s1")

	handle(&events.AppStateSyncError{Name: appstate.WAPatchRegularHigh})
	h.wantSync(appstate.WAPatchRegularHigh)

	// The worker is held in its full sync; signals while it is are dropped or noted, never waited on.
	for _, name := range appstate.AllPatchNames {
		for range 100 {
			if !handle(&events.AppStateSyncError{Name: name}) {
				t.Fatal("the handler failed")
			}
		}
	}

	// The handler is no handler for any other event.
	if !handle(&events.Mute{}) {
		t.Fatal("the handler failed")
	}

	close(h.stub.block)
}

func TestTheWorkerEndsOnceTheSessionLeavesOnlineAndAtClose(t *testing.T) {
	t.Parallel()

	h := start(t)
	h.stub.block = make(chan struct{})
	h.svc.Handler("s1")(&events.AppStateSyncError{Name: appstate.WAPatchRegularHigh})
	h.wantSync(appstate.WAPatchRegularHigh)

	rec := h.svc.recovery("s1")

	// Leaving online (or a delete, which closes the same channel) ends the full sync and the worker.
	close(h.sessions.online)

	select {
	case <-rec.done:
	case <-h.ctx.Done():
		t.Fatal("the worker did not end")
	}

	h.svc.mu.Lock()
	left := len(h.svc.recoveries)
	h.svc.mu.Unlock()

	if left != 0 {
		t.Fatalf("%d recoveries left", left)
	}

	// A session that is not online starts a worker that ends at once.
	h.sessions.offline = server.ErrNotConnected
	h.svc.Handler("s1")(&events.AppStateSyncError{Name: appstate.WAPatchRegularHigh})

	h.svc.Close()
	h.wantNoSync()

	// After Close nothing starts.
	h.svc.Handler("s1")(&events.AppStateSyncError{Name: appstate.WAPatchRegularLow})
	h.wantNoSync()
}

// syncBuffer is a bytes.Buffer safe for the server's and the worker's goroutines; written closes and replaces its
// channel on each write, so a test can wait for a line.
type syncBuffer struct {
	mu      sync.Mutex
	buf     bytes.Buffer
	written chan struct{}
}

func (b *syncBuffer) Write(p []byte) (int, error) {
	b.mu.Lock()
	defer b.mu.Unlock()

	if b.written != nil {
		close(b.written)
	}

	b.written = make(chan struct{})

	return b.buf.Write(p) //nolint:wrapcheck // a buffer's
}

// waitFor waits until the buffer holds text count times.
func (b *syncBuffer) waitFor(ctx context.Context, t *testing.T, text string, count int) {
	t.Helper()

	for {
		b.mu.Lock()
		got := strings.Count(b.buf.String(), text)

		if b.written == nil {
			b.written = make(chan struct{})
		}

		written := b.written
		b.mu.Unlock()

		if got >= count {
			return
		}

		select {
		case <-written:
		case <-ctx.Done():
			t.Fatalf("%q was logged %d times, want %d: %s", text, got, count, b.String())
		}
	}
}

func (b *syncBuffer) String() string {
	b.mu.Lock()
	defer b.mu.Unlock()

	return b.buf.String()
}
