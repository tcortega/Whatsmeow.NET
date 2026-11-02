package contacts

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
	waBinary "go.mau.fi/whatsmeow/binary"
	"go.mau.fi/whatsmeow/proto/waAdv"
	"go.mau.fi/whatsmeow/proto/waVnameCert"
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

	whatsmeowv1 "github.com/tcortega/Whatsmeow.NET/sidecar/gen/whatsmeow/v1"
	sidecarlog "github.com/tcortega/Whatsmeow.NET/sidecar/internal/log"
	"github.com/tcortega/Whatsmeow.NET/sidecar/internal/server"
	"github.com/tcortega/Whatsmeow.NET/sidecar/internal/session"
	sidecarstore "github.com/tcortega/Whatsmeow.NET/sidecar/internal/store"
)

const token = "00112233445566778899aabbccddeeff00112233445566778899aabbccddeeff"

// stub stands in for whatsmeow behind the slice's interface; the session methods it never needs stay nil.
type stub struct {
	session.Client

	mu       sync.Mutex
	err      error
	phones   []string
	jids     []types.JID
	onWA     []types.IsOnWhatsAppResponse
	users    map[types.JID]types.UserInfo
	picture  *types.ProfilePictureInfo
	blocked  *types.Blocklist
	updated  []string
	lookups  int
	pictured types.JID
}

func (s *stub) IsOnWhatsApp(_ context.Context, phones []string) ([]types.IsOnWhatsAppResponse, error) {
	s.mu.Lock()
	defer s.mu.Unlock()

	s.lookups++
	s.phones = phones

	return s.onWA, s.err
}

func (s *stub) GetUserInfo(_ context.Context, jids []types.JID) (map[types.JID]types.UserInfo, error) {
	s.mu.Lock()
	defer s.mu.Unlock()

	s.lookups++
	s.jids = jids

	return s.users, s.err
}

func (s *stub) GetProfilePictureInfo(_ context.Context, jid types.JID, _ *whatsmeow.GetProfilePictureParams) (*types.ProfilePictureInfo, error) {
	s.mu.Lock()
	defer s.mu.Unlock()

	s.lookups++
	s.pictured = jid

	return s.picture, s.err
}

func (s *stub) GetBlocklist(context.Context, string) (*types.Blocklist, error) {
	s.mu.Lock()
	defer s.mu.Unlock()

	s.lookups++

	return s.blocked, s.err
}

func (s *stub) UpdateBlocklist(_ context.Context, jid types.JID, action events.BlocklistChangeAction, _ string) (*types.Blocklist, error) {
	s.mu.Lock()
	defer s.mu.Unlock()

	s.lookups++
	s.updated = append(s.updated, string(action)+" "+jid.String())

	return s.blocked, s.err
}

// sessions stands in for the Sessions slice: one session, "s1", online with the stub unless offline is set.
type sessions struct {
	client  session.Client
	device  *store.Device
	offline error
}

func (s *sessions) Online(id string) (session.Client, <-chan struct{}, error) {
	if id != "s1" {
		return nil, nil, fmt.Errorf("session %q: %w", id, server.ErrSessionNotFound)
	}

	if s.offline != nil {
		return nil, nil, s.offline
	}

	return s.client, make(chan struct{}), nil
}

func (s *sessions) Device(id string) (*store.Device, error) {
	if id != "s1" {
		return nil, fmt.Errorf("session %q: %w", id, server.ErrSessionNotFound)
	}

	if s.device == nil {
		return nil, fmt.Errorf("session %q: %w", id, server.ErrNotLoggedIn)
	}

	return s.device, nil
}

type harness struct {
	t        *testing.T
	stub     *stub
	sessions *sessions
	device   *store.Device
	client   whatsmeowv1.ContactServiceClient
	ctx      context.Context //nolint:containedctx // the test's call context
}

func start(t *testing.T) *harness {
	t.Helper()

	return startLogging(t, zerolog.Nop())
}

// startLogging is start with the server logging to log.
func startLogging(t *testing.T, log zerolog.Logger) *harness {
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

	h := &harness{t: t, stub: &stub{}, device: device}
	h.sessions = &sessions{client: h.stub, device: device}

	srv := server.New(token, log, sidecarlog.NewRedactor())
	whatsmeowv1.RegisterContactServiceServer(srv, New(h.sessions))

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

		if err := db.Close(); err != nil {
			t.Errorf("close the store: %v", err)
		}
	})

	ctx, cancel := context.WithTimeout(context.Background(), 30*time.Second)
	t.Cleanup(cancel)

	h.ctx = metadata.AppendToOutgoingContext(ctx, "authorization", "Bearer "+token)
	h.client = whatsmeowv1.NewContactServiceClient(conn)

	return h
}

func jid(s string) types.JID {
	j, err := types.ParseJID(s)
	if err != nil {
		panic(err)
	}

	return j
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

func wantProto(t *testing.T, got, want proto.Message) {
	t.Helper()

	if !proto.Equal(got, want) {
		t.Fatalf("got %v, want %v", got, want)
	}
}

func TestIsOnWhatsAppAnswersEachNumberInRequestOrder(t *testing.T) {
	t.Parallel()

	h := start(t)
	// WhatsApp answers in its own order, and with the LID as the JID when there is one (user.go:194-217).
	h.stub.onWA = []types.IsOnWhatsAppResponse{
		{Query: "+5511900000002", JID: jid("5511900000002@s.whatsapp.net"), IsIn: true},
		{Query: "+5511900000001", JID: jid("111@lid"), PhoneNumber: jid("5511900000001@s.whatsapp.net"), IsIn: true},
		{Query: "+5511900000003", JID: jid("5511900000003@s.whatsapp.net"), IsIn: false},
	}

	resp, err := h.client.IsOnWhatsApp(h.ctx, &whatsmeowv1.IsOnWhatsAppRequest{
		SessionId: "s1", PhoneNumbers: []string{"5511900000001", "+5511900000002", "+5511900000003", "5511900000004"},
	})
	if err != nil {
		t.Fatalf("IsOnWhatsApp: %v", err)
	}

	// whatsmeow wants the + (user.go:174-175).
	if fmt.Sprint(h.stub.phones) != "[+5511900000001 +5511900000002 +5511900000003 +5511900000004]" {
		t.Fatalf("asked WhatsApp for %v", h.stub.phones)
	}

	wantProto(t, resp, &whatsmeowv1.IsOnWhatsAppResponse{Results: []*whatsmeowv1.PhoneNumberCheck{
		{PhoneNumber: "5511900000001", OnWhatsapp: true, Jid: "5511900000001@s.whatsapp.net", Lid: "111@lid"},
		{PhoneNumber: "+5511900000002", OnWhatsapp: true, Jid: "5511900000002@s.whatsapp.net"},
		{PhoneNumber: "+5511900000003"},
		{PhoneNumber: "5511900000004"},
	}})
}

func TestAMalformedPhoneNumberIsInvalidArgumentAndAsksNothing(t *testing.T) {
	t.Parallel()

	for _, phone := range []string{"", "+", "55 11 9", "+55-11", "++5511", "5511a", "5511+"} {
		h := start(t)

		_, err := h.client.IsOnWhatsApp(h.ctx, &whatsmeowv1.IsOnWhatsAppRequest{SessionId: "s1", PhoneNumbers: []string{"5511900000001", phone}})
		wantReason(t, err, codes.InvalidArgument, "INVALID_ARGUMENT", map[string]string{"field": "phone_numbers"})

		if h.stub.lookups != 0 {
			t.Fatalf("%q: asked WhatsApp anyway", phone)
		}
	}
}

func TestAnEmptyListReturnsAnEmptyListWithoutAskingWhatsApp(t *testing.T) {
	t.Parallel()

	h := start(t)
	// Even offline: nothing is asked.
	h.sessions.offline = server.ErrNotConnected

	numbers, err := h.client.IsOnWhatsApp(h.ctx, &whatsmeowv1.IsOnWhatsAppRequest{SessionId: "s1"})
	if err != nil || len(numbers.GetResults()) != 0 {
		t.Fatalf("IsOnWhatsApp = %v, %v; want empty", numbers, err)
	}

	users, err := h.client.GetUserInfo(h.ctx, &whatsmeowv1.GetUserInfoRequest{SessionId: "s1"})
	if err != nil || len(users.GetUsers()) != 0 {
		t.Fatalf("GetUserInfo = %v, %v; want empty", users, err)
	}

	resolved, err := h.client.ResolveJids(h.ctx, &whatsmeowv1.ResolveJidsRequest{SessionId: "s1"})
	if err != nil || len(resolved.GetResults()) != 0 {
		t.Fatalf("ResolveJids = %v, %v; want empty", resolved, err)
	}

	if h.stub.lookups != 0 {
		t.Fatal("asked WhatsApp for an empty list")
	}
}

func TestGetUserInfoGivesAboutBusinessNameAndPictureOrderedByJID(t *testing.T) {
	t.Parallel()

	h := start(t)
	h.stub.users = map[types.JID]types.UserInfo{
		jid("5511900000002@s.whatsapp.net"): {Status: "busy", PictureID: "P2"},
		jid("5511900000001@s.whatsapp.net"): {
			Status: "hi", PictureID: "P1", LID: jid("111@lid"),
			VerifiedName: &types.VerifiedName{Details: &waVnameCert.VerifiedNameCertificate_Details{VerifiedName: proto.String("Ana's Shop")}},
		},
	}

	resp, err := h.client.GetUserInfo(h.ctx, &whatsmeowv1.GetUserInfoRequest{
		SessionId: "s1", Jids: []string{"5511900000002@s.whatsapp.net", "5511900000001@s.whatsapp.net"},
	})
	if err != nil {
		t.Fatalf("GetUserInfo: %v", err)
	}

	wantProto(t, resp, &whatsmeowv1.GetUserInfoResponse{Users: []*whatsmeowv1.UserInfo{
		{Jid: "5511900000001@s.whatsapp.net", About: "hi", VerifiedBusinessName: "Ana's Shop", PictureId: "P1"},
		{Jid: "5511900000002@s.whatsapp.net", About: "busy", PictureId: "P2"},
	}})
}

func TestAJIDThatIsNotAUserIsInvalidArgument(t *testing.T) {
	t.Parallel()

	h := start(t)

	for _, bad := range []string{"120363000000000000@g.us", "status@broadcast", "a@b@c"} {
		_, err := h.client.GetUserInfo(h.ctx, &whatsmeowv1.GetUserInfoRequest{SessionId: "s1", Jids: []string{bad}})
		wantReason(t, err, codes.InvalidArgument, "INVALID_ARGUMENT", map[string]string{"field": "jids"})

		_, err = h.client.ResolveJids(h.ctx, &whatsmeowv1.ResolveJidsRequest{SessionId: "s1", Jids: []string{bad}})
		wantReason(t, err, codes.InvalidArgument, "INVALID_ARGUMENT", map[string]string{"field": "jids"})

		_, err = h.client.UpdateBlocklist(h.ctx, &whatsmeowv1.UpdateBlocklistRequest{SessionId: "s1", Jid: bad, Action: whatsmeowv1.BlocklistAction_BLOCKLIST_ACTION_BLOCK})
		wantReason(t, err, codes.InvalidArgument, "INVALID_ARGUMENT", map[string]string{"field": "jid"})
	}

	_, err := h.client.GetProfilePicture(h.ctx, &whatsmeowv1.GetProfilePictureRequest{SessionId: "s1", Jid: "status@broadcast"})
	wantReason(t, err, codes.InvalidArgument, "INVALID_ARGUMENT", map[string]string{"field": "jid"})

	_, err = h.client.UpdateBlocklist(h.ctx, &whatsmeowv1.UpdateBlocklistRequest{SessionId: "s1", Jid: "111@lid"})
	wantReason(t, err, codes.InvalidArgument, "INVALID_ARGUMENT", map[string]string{"field": "action"})

	if h.stub.lookups != 0 {
		t.Fatal("asked WhatsApp anyway")
	}
}

func TestGetProfilePictureSaysSetNoneOrHidden(t *testing.T) {
	t.Parallel()

	for _, tc := range []struct {
		name    string
		jid     string
		picture *types.ProfilePictureInfo
		err     error
		want    *whatsmeowv1.GetProfilePictureResponse
	}{
		{
			"set", "5511900000001@s.whatsapp.net", &types.ProfilePictureInfo{ID: "P1", URL: "https://pps.example/p1", Type: "image"}, nil,
			&whatsmeowv1.GetProfilePictureResponse{Result: &whatsmeowv1.GetProfilePictureResponse_Picture_{
				Picture: &whatsmeowv1.GetProfilePictureResponse_Picture{Url: "https://pps.example/p1", Id: "P1"},
			}},
		},
		{
			"a group's", "120363000000000000@g.us", &types.ProfilePictureInfo{ID: "G1", URL: "https://pps.example/g1"}, nil,
			&whatsmeowv1.GetProfilePictureResponse{Result: &whatsmeowv1.GetProfilePictureResponse_Picture_{
				Picture: &whatsmeowv1.GetProfilePictureResponse_Picture{Url: "https://pps.example/g1", Id: "G1"},
			}},
		},
		{
			"none", "111@lid", nil, fmt.Errorf("%w: %w", whatsmeow.ErrProfilePictureNotSet, whatsmeow.ErrIQNotFound),
			&whatsmeowv1.GetProfilePictureResponse{Result: &whatsmeowv1.GetProfilePictureResponse_None_{None: &whatsmeowv1.GetProfilePictureResponse_None{}}},
		},
		{
			"none, as a nil answer", "111@lid", nil, nil,
			&whatsmeowv1.GetProfilePictureResponse{Result: &whatsmeowv1.GetProfilePictureResponse_None_{None: &whatsmeowv1.GetProfilePictureResponse_None{}}},
		},
		{
			"hidden", "111@lid", nil, fmt.Errorf("%w: %w", whatsmeow.ErrProfilePictureUnauthorized, whatsmeow.ErrIQNotAuthorized),
			&whatsmeowv1.GetProfilePictureResponse{Result: &whatsmeowv1.GetProfilePictureResponse_Hidden_{Hidden: &whatsmeowv1.GetProfilePictureResponse_Hidden{}}},
		},
	} {
		t.Run(tc.name, func(t *testing.T) {
			t.Parallel()

			h := start(t)
			h.stub.picture, h.stub.err = tc.picture, tc.err

			resp, err := h.client.GetProfilePicture(h.ctx, &whatsmeowv1.GetProfilePictureRequest{SessionId: "s1", Jid: tc.jid})
			if err != nil {
				t.Fatalf("GetProfilePicture: %v", err)
			}

			wantProto(t, resp, tc.want)

			if h.stub.pictured.String() != tc.jid {
				t.Fatalf("asked for %v, want %s", h.stub.pictured, tc.jid)
			}
		})
	}
}

func TestResolveJidsGivesEachItsOtherFormFromTheStoreOrUnknown(t *testing.T) {
	t.Parallel()

	h := start(t)
	// The store learns pairs as whatsmeow does from a message with both forms (message.go:51-55).
	if err := h.device.LIDs.PutLIDMapping(h.ctx, jid("111@lid"), jid("5511900000001@s.whatsapp.net")); err != nil {
		t.Fatalf("store the pair: %v", err)
	}

	// Offline: it reads the store only.
	h.sessions.offline = server.ErrNotConnected

	resp, err := h.client.ResolveJids(h.ctx, &whatsmeowv1.ResolveJidsRequest{
		SessionId: "s1", Jids: []string{"111@lid", "5511900000001@s.whatsapp.net", "111:3@lid", "999@lid", "5511900000009@s.whatsapp.net"},
	})
	if err != nil {
		t.Fatalf("ResolveJids: %v", err)
	}

	wantProto(t, resp, &whatsmeowv1.ResolveJidsResponse{Results: []*whatsmeowv1.ResolvedJid{
		{Jid: "111@lid", JidAlt: "5511900000001@s.whatsapp.net"},
		{Jid: "5511900000001@s.whatsapp.net", JidAlt: "111@lid"},
		{Jid: "111:3@lid", JidAlt: "5511900000001:3@s.whatsapp.net"},
		{Jid: "999@lid"},
		{Jid: "5511900000009@s.whatsapp.net"},
	}})

	if h.stub.lookups != 0 {
		t.Fatal("asked WhatsApp")
	}
}

func TestListContactsGivesTheSyncedNamesOrderedByJID(t *testing.T) {
	t.Parallel()

	h := start(t)
	contacts := h.device.Contacts

	if err := contacts.PutContactName(h.ctx, jid("5511900000002@s.whatsapp.net"), "Bea", "Bea Lima"); err != nil {
		t.Fatalf("store a saved name: %v", err)
	}

	for _, name := range []struct{ jid, push, business string }{
		{"5511900000001@s.whatsapp.net", "Ana", "Ana's Shop"},
		{"111@lid", "Ana", ""},
	} {
		if _, _, err := contacts.PutPushName(h.ctx, jid(name.jid), name.push); err != nil {
			t.Fatalf("store a push name: %v", err)
		}

		if name.business == "" {
			continue
		}

		if _, _, err := contacts.PutBusinessName(h.ctx, jid(name.jid), name.business); err != nil {
			t.Fatalf("store a business name: %v", err)
		}
	}

	h.sessions.offline = server.ErrNotConnected

	resp, err := h.client.ListContacts(h.ctx, &whatsmeowv1.ListContactsRequest{SessionId: "s1"})
	if err != nil {
		t.Fatalf("ListContacts: %v", err)
	}

	wantProto(t, resp, &whatsmeowv1.ListContactsResponse{Contacts: []*whatsmeowv1.Contact{
		{Jid: "111@lid", PushName: "Ana"},
		{Jid: "5511900000001@s.whatsapp.net", PushName: "Ana", BusinessName: "Ana's Shop"},
		{Jid: "5511900000002@s.whatsapp.net", SavedName: "Bea Lima"},
	}})
}

func TestTheStoreReadsFailForASessionWithoutADevice(t *testing.T) {
	t.Parallel()

	h := start(t)
	h.sessions.device = nil

	_, err := h.client.ListContacts(h.ctx, &whatsmeowv1.ListContactsRequest{SessionId: "s1"})
	wantReason(t, err, codes.FailedPrecondition, "NOT_LOGGED_IN", nil)

	_, err = h.client.ResolveJids(h.ctx, &whatsmeowv1.ResolveJidsRequest{SessionId: "s1", Jids: []string{"111@lid"}})
	wantReason(t, err, codes.FailedPrecondition, "NOT_LOGGED_IN", nil)

	_, err = h.client.ListContacts(h.ctx, &whatsmeowv1.ListContactsRequest{SessionId: "nobody"})
	wantReason(t, err, codes.NotFound, "SESSION_NOT_FOUND", nil)
}

func TestBlocklistReadsAndUpdates(t *testing.T) {
	t.Parallel()

	h := start(t)
	h.stub.blocked = &types.Blocklist{Items: []types.BlocklistItem{
		{LID: jid("111@lid"), PN: jid("5511900000001@s.whatsapp.net"), Active: true},
		{LID: jid("222@lid")},
	}}

	resp, err := h.client.GetBlocklist(h.ctx, &whatsmeowv1.GetBlocklistRequest{SessionId: "s1"})
	if err != nil {
		t.Fatalf("GetBlocklist: %v", err)
	}

	wantProto(t, resp, &whatsmeowv1.GetBlocklistResponse{Users: []*whatsmeowv1.BlockedUser{
		{Jid: "111@lid", JidAlt: "5511900000001@s.whatsapp.net"},
		{Jid: "222@lid"},
	}})

	for _, req := range []*whatsmeowv1.UpdateBlocklistRequest{
		{SessionId: "s1", Jid: "5511900000003@s.whatsapp.net", Action: whatsmeowv1.BlocklistAction_BLOCKLIST_ACTION_BLOCK},
		{SessionId: "s1", Jid: "333@lid", Action: whatsmeowv1.BlocklistAction_BLOCKLIST_ACTION_UNBLOCK},
	} {
		if _, err := h.client.UpdateBlocklist(h.ctx, req); err != nil {
			t.Fatalf("UpdateBlocklist: %v", err)
		}
	}

	if fmt.Sprint(h.stub.updated) != "[block 5511900000003@s.whatsapp.net unblock 333@lid]" {
		t.Fatalf("sent %v", h.stub.updated)
	}
}

func TestWhatsAppsErrorsBecomeTheirReasons(t *testing.T) {
	t.Parallel()

	// whatsmeow puts the JID in its errors (user.go:1034, 1039, 1043, 1052) and in an IQ error's node (errors.go:216-226).
	const pn = "5511900000001@s.whatsapp.net"

	node := &waBinary.Node{Tag: "iq", Attrs: waBinary.Attrs{"from": jid(pn)}}
	disconnected := &whatsmeow.DisconnectedError{Action: "info query", Node: node}

	for _, tc := range []struct {
		name     string
		err      error
		update   bool
		code     codes.Code
		reason   string
		metadata map[string]string
	}{
		{"rate limited", fmt.Errorf("failed to send usync query: %w", whatsmeow.ErrIQRateOverLimit), false, codes.ResourceExhausted, "RATE_LIMITED", nil},
		{"rate limited inside the blocklist's user lookup", fmt.Errorf("failed to get user info for %s to fill LID cache: failed to send usync query: %w", pn, &whatsmeow.IQError{Code: 429, Text: "rate-overlimit", RawNode: node}), true, codes.ResourceExhausted, "RATE_LIMITED", nil},
		{"another IQ error", &whatsmeow.IQError{Code: 500, Text: "internal-server-error", RawNode: node}, false, codes.FailedPrecondition, "WHATSAPP_ERROR", map[string]string{"code": "500"}},
		{"an IQ error without a code, which prints its node", &whatsmeow.IQError{RawNode: node}, false, codes.FailedPrecondition, "WHATSAPP_ERROR", map[string]string{"code": "0"}},
		{"not connected", whatsmeow.ErrNotConnected, false, codes.Unavailable, "NOT_CONNECTED", nil},
		{"not connected, an update that was never sent", fmt.Errorf("failed to get user info for %s to fill LID cache: failed to send usync query: %w", pn, whatsmeow.ErrNotConnected), true, codes.Unavailable, "NOT_CONNECTED", nil},
		{"disconnected while waiting", disconnected, false, codes.Unavailable, "NOT_CONNECTED", nil},
		{"an update disconnected while waiting", disconnected, true, codes.Unknown, "OUTCOME_UNKNOWN", nil},
		{"an update that timed out", whatsmeow.ErrIQTimedOut, true, codes.Unknown, "OUTCOME_UNKNOWN", nil},
		{"a read that timed out", fmt.Errorf("failed to send usync query: %w", whatsmeow.ErrIQTimedOut), false, codes.Unavailable, "WHATSAPP_TIMED_OUT", nil},
		{"a number without a LID", fmt.Errorf("%w: no LID found for %s from server", whatsmeow.ErrRecipientNotOnWhatsApp, pn), true, codes.NotFound, "RECIPIENT_NOT_ON_WHATSAPP", nil},
		{"a store failure", fmt.Errorf("failed to get LID for PN %s: %w", pn, errors.New("database is locked")), true, codes.Internal, "INTERNAL", nil},
		{"cancelled", fmt.Errorf("failed to get user info for %s to fill LID cache: failed to send usync query: %w", pn, context.Canceled), true, codes.Canceled, "", nil},
	} {
		t.Run(tc.name, func(t *testing.T) {
			t.Parallel()

			h := start(t)
			h.stub.err = tc.err

			var err error
			if tc.update {
				_, err = h.client.UpdateBlocklist(h.ctx, &whatsmeowv1.UpdateBlocklistRequest{
					SessionId: "s1", Jid: "5511900000001@s.whatsapp.net", Action: whatsmeowv1.BlocklistAction_BLOCKLIST_ACTION_BLOCK,
				})
			} else {
				_, err = h.client.GetBlocklist(h.ctx, &whatsmeowv1.GetBlocklistRequest{SessionId: "s1"})
			}

			if tc.reason == "" {
				if status.Code(err) != tc.code {
					t.Fatalf("error = %v, want %v", err, tc.code)
				}
			} else {
				wantReason(t, err, tc.code, tc.reason, tc.metadata)
			}

			// The message reaches the application: none of whatsmeow's text around the error, with its JID, goes along.
			if msg := status.Convert(err).Message(); strings.Contains(msg, "900000001") {
				t.Fatalf("the message %q carries the JID", msg)
			}
		})
	}
}

func TestEveryWhatsAppCallNeedsTheSessionOnline(t *testing.T) {
	t.Parallel()

	h := start(t)
	h.sessions.offline = fmt.Errorf("session %q: %w", "s1", server.WithMetadata(server.ErrTemporarilyBanned, "expireTime", "2099-01-02T03:04:05Z"))
	banned := map[string]string{"expireTime": "2099-01-02T03:04:05Z"}

	_, err := h.client.IsOnWhatsApp(h.ctx, &whatsmeowv1.IsOnWhatsAppRequest{SessionId: "s1", PhoneNumbers: []string{"5511900000001"}})
	wantReason(t, err, codes.FailedPrecondition, "TEMPORARILY_BANNED", banned)

	_, err = h.client.GetUserInfo(h.ctx, &whatsmeowv1.GetUserInfoRequest{SessionId: "s1", Jids: []string{"111@lid"}})
	wantReason(t, err, codes.FailedPrecondition, "TEMPORARILY_BANNED", banned)

	_, err = h.client.GetProfilePicture(h.ctx, &whatsmeowv1.GetProfilePictureRequest{SessionId: "s1", Jid: "111@lid"})
	wantReason(t, err, codes.FailedPrecondition, "TEMPORARILY_BANNED", banned)

	_, err = h.client.GetBlocklist(h.ctx, &whatsmeowv1.GetBlocklistRequest{SessionId: "s1"})
	wantReason(t, err, codes.FailedPrecondition, "TEMPORARILY_BANNED", banned)

	_, err = h.client.UpdateBlocklist(h.ctx, &whatsmeowv1.UpdateBlocklistRequest{SessionId: "s1", Jid: "111@lid", Action: whatsmeowv1.BlocklistAction_BLOCKLIST_ACTION_UNBLOCK})
	wantReason(t, err, codes.FailedPrecondition, "TEMPORARILY_BANNED", banned)

	if h.stub.lookups != 0 {
		t.Fatal("asked WhatsApp for a session that is not online")
	}
}

// bare has the session methods only, like a client without contact queries.
type bare struct{ session.Client }

func TestAClientWithoutContactQueriesIsInternal(t *testing.T) {
	t.Parallel()

	h := start(t)
	h.sessions.client = bare{}

	_, err := h.client.GetBlocklist(h.ctx, &whatsmeowv1.GetBlocklistRequest{SessionId: "s1"})
	wantReason(t, err, codes.Internal, "INTERNAL", nil)
}

func TestEachContactChangeMapsToOneContactEventAndOthersToNone(t *testing.T) {
	t.Parallel()

	contact := func(jid, alt string, change any) *whatsmeowv1.StreamEventsResponse {
		event := &whatsmeowv1.ContactEvent{Jid: jid, JidAlt: alt}

		switch c := change.(type) {
		case *whatsmeowv1.ContactEvent_PushName_:
			event.Change = c
		case *whatsmeowv1.ContactEvent_BusinessName_:
			event.Change = c
		case *whatsmeowv1.ContactEvent_Picture_:
			event.Change = c
		case *whatsmeowv1.ContactEvent_About_:
			event.Change = c
		}

		return &whatsmeowv1.StreamEventsResponse{Payload: &whatsmeowv1.StreamEventsResponse_Contact{Contact: event}}
	}

	for _, tc := range []struct {
		name string
		evt  any
		want *whatsmeowv1.StreamEventsResponse
	}{
		{
			"push name", &events.PushName{JID: jid("111:2@lid"), JIDAlt: jid("5511900000001:2@s.whatsapp.net"), OldPushName: "A", NewPushName: "Ana"},
			contact("111@lid", "5511900000001@s.whatsapp.net", &whatsmeowv1.ContactEvent_PushName_{PushName: &whatsmeowv1.ContactEvent_PushName{Name: "Ana"}}),
		},
		{
			"push name without an alt", &events.PushName{JID: jid("111@lid"), NewPushName: "Ana"},
			contact("111@lid", "", &whatsmeowv1.ContactEvent_PushName_{PushName: &whatsmeowv1.ContactEvent_PushName{Name: "Ana"}}),
		},
		{
			"business name", &events.BusinessName{JID: jid("5511900000001@s.whatsapp.net"), NewBusinessName: "Ana's Shop"},
			contact("5511900000001@s.whatsapp.net", "", &whatsmeowv1.ContactEvent_BusinessName_{BusinessName: &whatsmeowv1.ContactEvent_BusinessName{Name: "Ana's Shop"}}),
		},
		{
			"picture set", &events.Picture{JID: jid("111@lid"), PictureID: "P1"},
			contact("111@lid", "", &whatsmeowv1.ContactEvent_Picture_{Picture: &whatsmeowv1.ContactEvent_Picture{Id: "P1"}}),
		},
		{
			"a group's picture removed", &events.Picture{JID: jid("120363000000000000@g.us"), Remove: true, PictureID: "ignored"},
			contact("120363000000000000@g.us", "", &whatsmeowv1.ContactEvent_Picture_{Picture: &whatsmeowv1.ContactEvent_Picture{}}),
		},
		{
			"about", &events.UserAbout{JID: jid("111@lid"), Status: "busy"},
			contact("111@lid", "", &whatsmeowv1.ContactEvent_About_{About: &whatsmeowv1.ContactEvent_About{Text: "busy"}}),
		},
		{"a saved name is not recorded", &events.Contact{JID: jid("111@lid")}, nil},
		{"a blocklist change is not recorded", &events.Blocklist{}, nil},
		{"a message is the Events slice's", &events.Message{}, nil},
	} {
		key, got, ok := Event("s1", tc.evt)
		if ok != (tc.want != nil) || key != "" || !proto.Equal(got, tc.want) {
			t.Errorf("%s: Event = %q, %v, %v; want %v", tc.name, key, got, ok, tc.want)
		}
	}
}

func TestIsOnWhatsAppGivesTheNumbersJIDWhenWhatsAppSentOnlyTheLID(t *testing.T) {
	t.Parallel()

	h := start(t)
	// A user answered by LID without a pn_jid: whatsmeow leaves PhoneNumber empty (user.go:194-204).
	h.stub.onWA = []types.IsOnWhatsAppResponse{{Query: "+5511900000001", JID: jid("111@lid"), IsIn: true}}

	resp, err := h.client.IsOnWhatsApp(h.ctx, &whatsmeowv1.IsOnWhatsAppRequest{SessionId: "s1", PhoneNumbers: []string{"+5511900000001"}})
	if err != nil {
		t.Fatalf("IsOnWhatsApp: %v", err)
	}

	wantProto(t, resp, &whatsmeowv1.IsOnWhatsAppResponse{Results: []*whatsmeowv1.PhoneNumberCheck{
		{PhoneNumber: "+5511900000001", OnWhatsapp: true, Jid: "5511900000001@s.whatsapp.net", Lid: "111@lid"},
	}})
}

func TestUpdateBlocklistSendsTheUserWithoutItsDevice(t *testing.T) {
	t.Parallel()

	h := start(t)

	for _, req := range []*whatsmeowv1.UpdateBlocklistRequest{
		{SessionId: "s1", Jid: "5511900000001:2@s.whatsapp.net", Action: whatsmeowv1.BlocklistAction_BLOCKLIST_ACTION_BLOCK},
		{SessionId: "s1", Jid: "111:3@lid", Action: whatsmeowv1.BlocklistAction_BLOCKLIST_ACTION_UNBLOCK},
	} {
		if _, err := h.client.UpdateBlocklist(h.ctx, req); err != nil {
			t.Fatalf("UpdateBlocklist: %v", err)
		}
	}

	// whatsmeow sends the JID as given, and its LID lookup keeps the device (user.go:1027-1066, lidmap.go:100, 113).
	if fmt.Sprint(h.stub.updated) != "[block 5511900000001@s.whatsapp.net unblock 111@lid]" {
		t.Fatalf("sent %v", h.stub.updated)
	}
}

func TestTheInfoLogCarriesNoJID(t *testing.T) {
	t.Parallel()

	var logged bytes.Buffer

	h := startLogging(t, zerolog.New(&logged).Level(zerolog.InfoLevel))
	h.stub.onWA = []types.IsOnWhatsAppResponse{{Query: "+5511900000001", JID: jid("111@lid"), PhoneNumber: jid("5511900000001@s.whatsapp.net"), IsIn: true}}
	h.stub.users = map[types.JID]types.UserInfo{jid("5511900000001@s.whatsapp.net"): {Status: "hi"}}
	h.stub.picture = &types.ProfilePictureInfo{ID: "P1", URL: "https://pps.example/p1"}
	h.stub.blocked = &types.Blocklist{Items: []types.BlocklistItem{{LID: jid("111@lid"), PN: jid("5511900000001@s.whatsapp.net")}}}

	calls := func() {
		_, _ = h.client.IsOnWhatsApp(h.ctx, &whatsmeowv1.IsOnWhatsAppRequest{SessionId: "s1", PhoneNumbers: []string{"+5511900000001"}})
		_, _ = h.client.GetUserInfo(h.ctx, &whatsmeowv1.GetUserInfoRequest{SessionId: "s1", Jids: []string{"5511900000001@s.whatsapp.net"}})
		_, _ = h.client.GetProfilePicture(h.ctx, &whatsmeowv1.GetProfilePictureRequest{SessionId: "s1", Jid: "5511900000001@s.whatsapp.net"})
		_, _ = h.client.ResolveJids(h.ctx, &whatsmeowv1.ResolveJidsRequest{SessionId: "s1", Jids: []string{"5511900000001@s.whatsapp.net"}})
		_, _ = h.client.ListContacts(h.ctx, &whatsmeowv1.ListContactsRequest{SessionId: "s1"})
		_, _ = h.client.GetBlocklist(h.ctx, &whatsmeowv1.GetBlocklistRequest{SessionId: "s1"})
		_, _ = h.client.UpdateBlocklist(h.ctx, &whatsmeowv1.UpdateBlocklistRequest{
			SessionId: "s1", Jid: "5511900000001@s.whatsapp.net", Action: whatsmeowv1.BlocklistAction_BLOCKLIST_ACTION_BLOCK,
		})
	}

	calls()

	// And each failure whatsmeow returns with a JID in it (user.go:1034, 1039, 1043, 1052; errors.go:216-226).
	node := &waBinary.Node{Tag: "iq", Attrs: waBinary.Attrs{"from": jid("5511900000001@s.whatsapp.net")}}
	for _, err := range []error{
		fmt.Errorf("failed to get LID for PN %s: %w", "5511900000001@s.whatsapp.net", errors.New("database is locked")),
		fmt.Errorf("%w: no LID found for %s from server", whatsmeow.ErrRecipientNotOnWhatsApp, "5511900000001@s.whatsapp.net"),
		&whatsmeow.IQError{RawNode: node},
		fmt.Errorf("failed to resolve PN for %s: %w", "111@lid", errors.New("database is locked")),
	} {
		h.stub.err = err
		calls()
	}

	if strings.Contains(logged.String(), "900000001") || strings.Contains(logged.String(), "111@lid") {
		t.Fatalf("the info log carries a JID:\n%s", logged.String())
	}
}
