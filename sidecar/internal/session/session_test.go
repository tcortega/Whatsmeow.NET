package session

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
	"go.mau.fi/whatsmeow/proto/waAdv"
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
	"github.com/tcortega/Whatsmeow.NET/sidecar/internal/outbox"
	"github.com/tcortega/Whatsmeow.NET/sidecar/internal/server"
	sidecarstore "github.com/tcortega/Whatsmeow.NET/sidecar/internal/store"
)

const token = "00112233445566778899aabbccddeeff00112233445566778899aabbccddeeff"

// stub stands in for whatsmeow behind the slice's interface. Like whatsmeow, it raises no event for Connect,
// Disconnect or Logout; a test raises events with emit.
type stub struct {
	device *store.Device

	mu          sync.Mutex
	handlers    []whatsmeow.EventHandlerWithSuccessStatus
	connectErr  error
	logoutErr   error
	connectCtxs []context.Context
	disconnects int
	logouts     int
	connected   bool
	// resetAt is when each ResetConnection came. Like whatsmeow's, a reset dispatches Disconnected and then the
	// login's Connected after it returned, on a goroutine of its own (client.go:612-631, 712-724), unless holdReset,
	// when it dispatches nothing.
	resetAt   []time.Time
	holdReset bool
	// connecting, when set, runs inside ConnectContext before it returns, without the stub's lock.
	connecting func(ctx context.Context)
	// disconnecting, when set, runs inside Disconnect, without the stub's lock.
	disconnecting func()
	// pairing is what the pairing calls see and did (pair_test.go).
	pairing pairStub
}

func (s *stub) AddEventHandlerWithSuccessStatus(handler whatsmeow.EventHandlerWithSuccessStatus) uint32 {
	s.mu.Lock()
	defer s.mu.Unlock()

	s.handlers = append(s.handlers, handler)

	return uint32(len(s.handlers)) //nolint:gosec // G115: a handful of handlers
}

func (s *stub) ConnectContext(ctx context.Context) error {
	s.mu.Lock()
	s.connectCtxs = append(s.connectCtxs, ctx)
	connecting, err := s.connecting, s.connectErr
	s.connected = err == nil
	s.mu.Unlock()

	if connecting != nil {
		connecting(ctx)
	}

	return err
}

func (s *stub) Disconnect() {
	s.mu.Lock()
	s.disconnects++
	s.connected = false
	disconnecting := s.disconnecting
	s.mu.Unlock()

	if disconnecting != nil {
		disconnecting()
	}
}

func (s *stub) ResetConnection() {
	s.mu.Lock()
	defer s.mu.Unlock()

	s.resetAt = append(s.resetAt, time.Now())
	if s.holdReset {
		return
	}

	go func() {
		s.emit(&events.Disconnected{})
		s.emit(&events.Connected{})
	}()
}

func (s *stub) IsConnected() bool {
	s.mu.Lock()
	defer s.mu.Unlock()

	return s.connected
}

func (s *stub) Logout(context.Context) error {
	s.mu.Lock()
	defer s.mu.Unlock()

	s.logouts++

	return s.logoutErr
}

func (s *stub) emit(evt any) {
	s.mu.Lock()
	handlers := s.handlers
	s.mu.Unlock()

	for _, h := range handlers {
		h(evt)
	}
}

func (s *stub) connects() int {
	s.mu.Lock()
	defer s.mu.Unlock()

	return len(s.connectCtxs)
}

// harness is the slice on a real store, served by the real server over an in-memory listener.
type harness struct {
	t      *testing.T
	path   string
	db     *sidecarstore.Store
	svc    *Service
	client whatsmeowv1.SessionServiceClient
	ctx    context.Context //nolint:containedctx // the test's call context
	stop   func()
	mu     sync.Mutex
	stubs  map[string]*stub
}

func start(t *testing.T) *harness {
	t.Helper()

	return startOn(t, filepath.Join(t.TempDir(), "whatsmeow-net.db"))
}

func startOn(t *testing.T, path string) *harness {
	t.Helper()

	return startLogging(t, path, zerolog.Nop())
}

// startLogging starts the slice with log as its logger.
func startLogging(t *testing.T, path string, log zerolog.Logger) *harness {
	t.Helper()

	h := &harness{t: t, path: path, stubs: map[string]*stub{}}

	db, err := sidecarstore.Open(context.Background(), path, zerolog.Nop())
	if err != nil {
		t.Fatalf("open the store: %v", err)
	}

	h.db = db

	svc, err := New(context.Background(), db.DB, db.Writer, db.Devices, outbox.New(db.DB, db.Writer), func(id string, device *store.Device, _ func()) Client {
		s := &stub{device: device}

		h.mu.Lock()
		h.stubs[id] = s
		h.mu.Unlock()

		return s
	}, log)
	if err != nil {
		_ = db.Close()

		t.Fatalf("New: %v", err)
	}

	h.svc = svc

	srv := server.New(token, zerolog.Nop(), sidecarlog.NewRedactor())
	whatsmeowv1.RegisterSessionServiceServer(srv, svc)

	lis := bufconn.Listen(1 << 20)
	served := make(chan error, 1)

	go func() { served <- srv.Serve(lis) }()

	conn, err := grpc.NewClient("passthrough:///bufnet",
		grpc.WithContextDialer(func(ctx context.Context, _ string) (net.Conn, error) { return lis.DialContext(ctx) }),
		grpc.WithTransportCredentials(insecure.NewCredentials()))
	if err != nil {
		t.Fatalf("dial: %v", err)
	}

	stopped := false
	stop := func() {
		if stopped {
			return
		}

		stopped = true

		_ = conn.Close()
		srv.Shutdown()
		srv.Stop()

		if err := <-served; err != nil {
			t.Errorf("serve: %v", err)
		}

		svc.Close()

		if err := db.Close(); err != nil {
			t.Errorf("close the store: %v", err)
		}
	}
	t.Cleanup(stop)
	h.stop = stop

	// A bound for a broken slice, never a wait: every wait below ends on a condition.
	ctx, cancel := context.WithTimeout(context.Background(), 30*time.Second)
	t.Cleanup(cancel)

	h.ctx = metadata.AppendToOutgoingContext(ctx, "authorization", "Bearer "+token)
	h.client = whatsmeowv1.NewSessionServiceClient(conn)

	return h
}

// setState puts the session in state, as if it got there before.
func (h *harness) setState(id string, state *whatsmeowv1.SessionState) {
	h.t.Helper()

	h.svc.mu.Lock()
	s := h.svc.sessions[id]
	h.svc.mu.Unlock()

	s.mu.Lock()
	s.state = state
	s.mu.Unlock()
}

func (h *harness) stub(id string) *stub {
	h.mu.Lock()
	defer h.mu.Unlock()

	return h.stubs[id]
}

func (h *harness) create(id string) *whatsmeowv1.Session {
	h.t.Helper()

	resp, err := h.client.CreateSession(h.ctx, &whatsmeowv1.CreateSessionRequest{SessionId: id})
	if err != nil {
		h.t.Fatalf("CreateSession(%q): %v", id, err)
	}

	return resp.GetSession()
}

// link does what whatsmeow does when the phone links the device (pair.go:222-240, 157).
func (h *harness) link(id, jid string) {
	h.t.Helper()

	s := h.stub(id)

	parsed, err := types.ParseJID(jid)
	if err != nil {
		h.t.Fatalf("jid: %v", err)
	}

	s.device.ID = &parsed
	s.device.Account = &waAdv.ADVSignedDeviceIdentity{
		Details: []byte{}, AccountSignature: make([]byte, 64), AccountSignatureKey: make([]byte, 32), DeviceSignature: make([]byte, 64),
	}

	if err := s.device.Save(context.Background()); err != nil {
		h.t.Fatalf("save the device: %v", err)
	}

	s.emit(&events.PairSuccess{ID: parsed})
}

// connected creates a linked session and connects it, as WhatsApp accepts the login.
func (h *harness) connected(id string) {
	h.t.Helper()

	h.create(id)
	h.link(id, id+":1@s.whatsapp.net")
	h.connect(id)
	h.stub(id).emit(&events.Connected{})
}

func (h *harness) connect(id string) {
	h.t.Helper()

	if _, err := h.client.Connect(h.ctx, &whatsmeowv1.ConnectRequest{SessionId: id}); err != nil {
		h.t.Fatalf("Connect(%q): %v", id, err)
	}
}

func (h *harness) state(id string) *whatsmeowv1.SessionState {
	h.t.Helper()

	resp, err := h.client.ListSessions(h.ctx, &whatsmeowv1.ListSessionsRequest{})
	if err != nil {
		h.t.Fatalf("ListSessions: %v", err)
	}

	for _, s := range resp.GetSessions() {
		if s.GetId() == id {
			return s.GetState()
		}
	}

	h.t.Fatalf("no session %q", id)

	return nil
}

func (h *harness) wantState(id string, want *whatsmeowv1.SessionState) {
	h.t.Helper()

	if got := h.state(id); !proto.Equal(got, want) {
		h.t.Fatalf("session %q state = %v, want %v", id, got, want)
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
	if info.GetReason() != reason || len(info.GetMetadata()) != len(metadata) {
		t.Fatalf("ErrorInfo = %v, want %s with %v", info, reason, metadata)
	}

	for k, v := range metadata {
		if info.GetMetadata()[k] != v {
			t.Fatalf("ErrorInfo = %v, want %s with %v", info, reason, metadata)
		}
	}
}

func TestCreateSessionReturnsTheExistingSession(t *testing.T) {
	t.Parallel()

	h := start(t)
	first := h.create("s1")
	h.link("s1", "1555:1@s.whatsapp.net")

	again := h.create("s1")

	want := &whatsmeowv1.Session{Id: "s1", State: disconnected(), Jid: "1555:1@s.whatsapp.net"}
	if !proto.Equal(again, want) || !proto.Equal(first, &whatsmeowv1.Session{Id: "s1", State: created()}) {
		t.Fatalf("CreateSession = %v then %v, want Created then %v", first, again, want)
	}
}

func TestAMalformedSessionIDIsInvalidArgument(t *testing.T) {
	t.Parallel()

	h := start(t)

	for _, id := range []string{"", "-a", ".a", "a b", "a/b", "é", strings.Repeat("a", 65)} {
		_, err := h.client.CreateSession(h.ctx, &whatsmeowv1.CreateSessionRequest{SessionId: id})
		wantReason(t, err, codes.InvalidArgument, "INVALID_ARGUMENT", map[string]string{"field": "session_id"})

		_, err = h.client.Connect(h.ctx, &whatsmeowv1.ConnectRequest{SessionId: id})
		wantReason(t, err, codes.InvalidArgument, "INVALID_ARGUMENT", map[string]string{"field": "session_id"})
	}

	h.create(strings.Repeat("a", 64))
	h.create("0a.B_-")
}

func TestEveryCallFailsWithSessionNotFoundForAnUnknownID(t *testing.T) {
	t.Parallel()

	h := start(t)
	req := "nobody"

	calls := map[string]func() error{
		"Connect": func() error {
			_, err := h.client.Connect(h.ctx, &whatsmeowv1.ConnectRequest{SessionId: req})

			return fmt.Errorf("Connect: %w", err)
		},
		"Disconnect": func() error {
			_, err := h.client.Disconnect(h.ctx, &whatsmeowv1.DisconnectRequest{SessionId: req})

			return fmt.Errorf("Disconnect: %w", err)
		},
		"Logout": func() error {
			_, err := h.client.Logout(h.ctx, &whatsmeowv1.LogoutRequest{SessionId: req})

			return fmt.Errorf("Logout: %w", err)
		},
		"Delete": func() error {
			_, err := h.client.DeleteSession(h.ctx, &whatsmeowv1.DeleteSessionRequest{SessionId: req})

			return fmt.Errorf("DeleteSession: %w", err)
		},
		"Watch": func() error {
			stream, err := h.client.WatchSessionState(h.ctx, &whatsmeowv1.WatchSessionStateRequest{SessionId: req})
			if err != nil {
				return fmt.Errorf("WatchSessionState: %w", err)
			}

			_, err = stream.Recv()

			return fmt.Errorf("Recv: %w", err)
		},
	}

	for name, call := range calls {
		t.Run(name, func(t *testing.T) {
			wantReason(t, call(), codes.NotFound, "SESSION_NOT_FOUND", nil)
		})
	}
}

func TestListSessionsOrdersByID(t *testing.T) {
	t.Parallel()

	h := start(t)
	for _, id := range []string{"b", "a", "B", "c"} {
		h.create(id)
	}

	resp, err := h.client.ListSessions(h.ctx, &whatsmeowv1.ListSessionsRequest{})
	if err != nil {
		t.Fatalf("ListSessions: %v", err)
	}

	var ids []string
	for _, s := range resp.GetSessions() {
		ids = append(ids, s.GetId())
	}

	if strings.Join(ids, ",") != "B,a,b,c" {
		t.Fatalf("ids = %v, want B,a,b,c", ids)
	}
}

func TestDeleteSessionDisconnectsDeletesTheDeviceAndEndsWatches(t *testing.T) {
	t.Parallel()

	h := start(t)
	h.connected("s1")

	stream, err := h.client.WatchSessionState(h.ctx, &whatsmeowv1.WatchSessionStateRequest{SessionId: "s1"})
	if err != nil {
		t.Fatalf("WatchSessionState: %v", err)
	}

	if _, err := stream.Recv(); err != nil {
		t.Fatalf("the current state: %v", err)
	}

	if _, err := h.client.DeleteSession(h.ctx, &whatsmeowv1.DeleteSessionRequest{SessionId: "s1"}); err != nil {
		t.Fatalf("DeleteSession: %v", err)
	}

	_, err = stream.Recv()
	wantReason(t, err, codes.NotFound, "SESSION_NOT_FOUND", nil)

	var devices, sessions int
	if err := h.db.DB.QueryRowContext(h.ctx, "SELECT (SELECT count(*) FROM whatsmeow_device), (SELECT count(*) FROM session)").Scan(&devices, &sessions); err != nil {
		t.Fatalf("count: %v", err)
	}

	if s := h.stub("s1"); s.disconnects != 1 || devices != 0 || sessions != 0 {
		t.Fatalf("disconnects %d, device rows %d, session rows %d; want 1, 0, 0", s.disconnects, devices, sessions)
	}

	if got := h.create("s1"); !proto.Equal(got, &whatsmeowv1.Session{Id: "s1", State: created()}) {
		t.Fatalf("CreateSession after delete = %v, want a new Created session", got)
	}
}

// A call that found the session just before DeleteSession took it waits for the delete, then must not act on the
// deleted session: it records nothing, and a second delete does not close what the first one closed.
func TestACallThatFoundTheSessionBeforeItWasDeletedIsSessionNotFound(t *testing.T) {
	t.Parallel()

	calls := []struct {
		name string
		call func(svc *Service, ctx context.Context, s *session) error
	}{
		{"Connect", func(svc *Service, ctx context.Context, s *session) error {
			_, err := svc.connect(ctx, s)
			return err
		}},
		{"Disconnect", func(svc *Service, ctx context.Context, s *session) error {
			_, err := svc.disconnect(ctx, s)
			return err
		}},
		{"Logout", func(svc *Service, ctx context.Context, s *session) error {
			_, err := svc.logout(ctx, s)
			return err
		}},
		{"DeleteSession", func(svc *Service, ctx context.Context, s *session) error {
			_, err := svc.deleteSession(ctx, s)
			return err
		}},
	}

	for _, c := range calls {
		t.Run(c.name, func(t *testing.T) {
			t.Parallel()

			h := start(t)
			h.connected("s1")

			// What the call found before it waited for s.ops.
			s, err := h.svc.get("s1")
			if err != nil {
				t.Fatalf("get: %v", err)
			}

			if _, err := h.client.DeleteSession(h.ctx, &whatsmeowv1.DeleteSessionRequest{SessionId: "s1"}); err != nil {
				t.Fatalf("DeleteSession: %v", err)
			}

			if err := c.call(h.svc, h.ctx, s); !errors.Is(err, server.ErrSessionNotFound) {
				t.Fatalf("%s on the deleted session = %v, want SESSION_NOT_FOUND", c.name, err)
			}

			if d := h.stub("s1").disconnects; d != 1 {
				t.Fatalf("disconnects = %d, want only the delete's", d)
			}
		})
	}
}

func TestEverySignalMapsToExactlyOneState(t *testing.T) {
	t.Parallel()

	now := time.Date(2026, 11, 1, 10, 0, 0, 500, time.UTC)
	jid := types.NewADJID("1555", 0, 1)
	banEnd := timestamppb.New(time.Date(2026, 11, 1, 11, 0, 0, 0, time.UTC))

	cases := []struct {
		name string
		from *whatsmeowv1.SessionState
		evt  any
		want *whatsmeowv1.SessionState // nil: no change
	}{
		{"Connected after Connecting", connecting(), &events.Connected{}, connected()},
		{"Connected after Reconnecting", reconnecting(), &events.Connected{}, connected()},
		{"a late Connected after Disconnect", disconnected(), &events.Connected{}, nil},
		{"OfflineSyncCompleted when Connected", connected(), &events.OfflineSyncCompleted{Count: 3}, caughtUp()},
		{"OfflineSyncCompleted while Connecting", connecting(), &events.OfflineSyncCompleted{}, nil},
		{"Disconnected when Connected", connected(), &events.Disconnected{}, reconnecting()},
		{"Disconnected when CaughtUp", caughtUp(), &events.Disconnected{}, reconnecting()},
		{"Disconnected while Connecting", connecting(), &events.Disconnected{}, reconnecting()},
		{"Disconnected while Reconnecting", reconnecting(), &events.Disconnected{}, nil},
		{"a late Disconnected after Disconnect", disconnected(), &events.Disconnected{}, nil},
		{"PairSuccess when Created", created(), &events.PairSuccess{ID: jid}, disconnected()},
		{"PairSuccess when LoggedOut", loggedOut(401), &events.PairSuccess{ID: jid}, disconnected()},
		{"PairSuccess when Connected", connected(), &events.PairSuccess{ID: jid}, nil},
		{"LoggedOut on connect", connecting(), &events.LoggedOut{OnConnect: true, Reason: events.ConnectFailureMainDeviceGone}, loggedOut(403)},
		{"LoggedOut by stream error", caughtUp(), &events.LoggedOut{Reason: events.ConnectFailureLoggedOut}, loggedOut(401)},
		{"StreamReplaced", connected(), &events.StreamReplaced{}, replaced()},
		{"ClientOutdated", connecting(), &events.ClientOutdated{}, outdated()},
		{"CATRefreshError", connected(), &events.CATRefreshError{Error: errors.New("refresh failed")}, failed(0, "refresh failed")},
		{"TemporaryBan with an end", connecting(), &events.TemporaryBan{Code: events.TempBanBlockedByUsers, Expire: time.Hour}, tempBanned(102, banEnd)},
		{"TemporaryBan without an end", connecting(), &events.TemporaryBan{Code: events.TempBanSentToTooManyPeople}, tempBanned(101, nil)},
		{"ConnectFailure", connecting(), &events.ConnectFailure{Reason: events.ConnectFailureClientUnknown, Message: "unknown"}, failed(418, "unknown")},
		{"StreamReplaced after LoggedOut", loggedOut(401), &events.StreamReplaced{}, nil},
		{"KeepAliveTimeout", connected(), &events.KeepAliveTimeout{ErrorCount: 1}, nil},
		{"StreamError", connected(), &events.StreamError{Code: "500"}, nil},
		{"a message", connected(), &events.Message{}, nil},
	}

	for _, c := range cases {
		got, ok := next(c.from, c.evt, now)
		if c.want == nil {
			if ok {
				t.Errorf("%s: moved to %v, want no change", c.name, got)
			}

			continue
		}

		if !ok || !proto.Equal(got, c.want) {
			t.Errorf("%s: = %v, %v; want %v", c.name, got, ok, c.want)
		}
	}
}

// whatsmeow dispatches Connected on a goroutine of its own after two IQs (connectionevents.go:187-204), and
// OfflineSyncCompleted from the offline ib on the node queue (connectionevents.go:87-90), so the offline sync can come
// first.
func TestAnOfflineSyncBeforeConnectedMakesTheSessionCaughtUpOnceConnected(t *testing.T) {
	t.Parallel()

	h := start(t)
	h.create("s1")
	h.link("s1", "1555:1@s.whatsapp.net")
	h.connect("s1")

	h.stub("s1").emit(&events.OfflineSyncCompleted{})
	h.wantState("s1", connecting())

	h.stub("s1").emit(&events.Connected{})
	h.wantRecorded("s1", disconnected(), connecting(), connected(), caughtUp())
}

func TestAnOfflineSyncBeforeTheReconnectsConnectedMakesTheSessionCaughtUpOnceConnected(t *testing.T) {
	t.Parallel()

	h := start(t)
	h.connected("s1")
	h.stub("s1").emit(&events.Disconnected{})

	h.stub("s1").emit(&events.OfflineSyncCompleted{})
	h.wantState("s1", reconnecting())

	h.stub("s1").emit(&events.Connected{})
	h.wantRecorded("s1", disconnected(), connecting(), connected(), reconnecting(), connected(), caughtUp())
}

func TestAnEarlyOfflineSyncIsForgottenWhenItsLoginDrops(t *testing.T) {
	t.Parallel()

	h := start(t)
	h.create("s1")
	h.link("s1", "1555:1@s.whatsapp.net")
	h.connect("s1")
	h.stub("s1").emit(&events.OfflineSyncCompleted{})

	// The reconnect's login is another one, with an offline sync of its own.
	h.stub("s1").emit(&events.Disconnected{})
	h.stub("s1").emit(&events.Connected{})

	h.wantRecorded("s1", disconnected(), connecting(), reconnecting(), connected())
}

func TestAnEarlyOfflineSyncIsForgottenWhenTheSessionIsDisconnected(t *testing.T) {
	t.Parallel()

	h := start(t)
	h.create("s1")
	h.link("s1", "1555:1@s.whatsapp.net")
	h.connect("s1")
	h.stub("s1").emit(&events.OfflineSyncCompleted{})

	if _, err := h.client.Disconnect(h.ctx, &whatsmeowv1.DisconnectRequest{SessionId: "s1"}); err != nil {
		t.Fatalf("Disconnect: %v", err)
	}

	h.connect("s1")
	h.stub("s1").emit(&events.Connected{})

	h.wantRecorded("s1", disconnected(), connecting(), disconnected(), connecting(), connected())
}

func TestAnEarlyOfflineSyncIsLoggedWithTheSessionIDOnly(t *testing.T) {
	t.Parallel()

	var log bytes.Buffer

	h := startLogging(t, filepath.Join(t.TempDir(), "whatsmeow-net.db"), zerolog.New(&log))
	h.create("s1")
	h.link("s1", "1555:1@s.whatsapp.net")
	h.connect("s1")

	h.stub("s1").emit(&events.OfflineSyncCompleted{})

	// The live run looks for this line to record which order WhatsApp used (#83).
	want := `{"level":"info","session_id":"s1","state":"Connecting","message":"the offline sync completed before Connected; the session is CaughtUp once Connected"}` + "\n"
	if got := log.String(); got != want {
		t.Fatalf("log = %q, want %q", got, want)
	}
}

func TestAnOfflineSyncOutsideALoginKeepsNothingAndLogsNothing(t *testing.T) {
	t.Parallel()

	var log bytes.Buffer

	h := startLogging(t, filepath.Join(t.TempDir(), "whatsmeow-net.db"), zerolog.New(&log))

	h.create("idle")
	h.link("idle", "1555:1@s.whatsapp.net")
	h.stub("idle").emit(&events.OfflineSyncCompleted{})

	h.connected("caughtup")
	h.stub("caughtup").emit(&events.OfflineSyncCompleted{})
	h.stub("caughtup").emit(&events.OfflineSyncCompleted{})

	// Before the link there is no account, and no login to keep it for.
	h.create("pairing")
	h.pair("pairing", "")
	h.pairing("pairing", nil).emit(&events.OfflineSyncCompleted{})

	if strings.Contains(log.String(), "offline sync") {
		t.Fatalf("an offline sync outside a login was kept:\n%s", log.String())
	}

	h.wantState("idle", disconnected())
	h.wantState("caughtup", caughtUp())
	h.wantState("pairing", pairing())
}

// container records every write or delete of the device.
type container struct {
	mu    sync.Mutex
	calls []string
}

func (c *container) PutDevice(context.Context, *store.Device) error {
	c.mu.Lock()
	defer c.mu.Unlock()

	c.calls = append(c.calls, "PutDevice")

	return nil
}

func (c *container) DeleteDevice(context.Context, *store.Device) error {
	c.mu.Lock()
	defer c.mu.Unlock()

	c.calls = append(c.calls, "DeleteDevice")

	return nil
}

func TestAServerLoggedOutNeverTouchesTheDevice(t *testing.T) {
	t.Parallel()

	h := start(t)
	h.connected("s1")

	s := h.stub("s1")
	recording := &container{}
	s.device.Container = recording
	s.device.SetAllStores(&store.NoopStore{Error: errors.New("the LoggedOut handler read the device")}) //nolint:err113 // a test sentinel

	s.emit(&events.LoggedOut{OnConnect: false, Reason: events.ConnectFailureLoggedOut})

	h.wantState("s1", loggedOut(401))

	if len(recording.calls) != 0 || s.device.Deleted {
		t.Fatalf("the handler touched the device: %v, deleted %v", recording.calls, s.device.Deleted)
	}

	if got := h.create("s1"); got.GetJid() != "" {
		t.Fatalf("a logged-out session keeps the jid %q", got.GetJid())
	}
}

func TestDisconnectRecordsDisconnected(t *testing.T) {
	t.Parallel()

	h := start(t)
	h.connected("s1")

	if _, err := h.client.Disconnect(h.ctx, &whatsmeowv1.DisconnectRequest{SessionId: "s1"}); err != nil {
		t.Fatalf("Disconnect: %v", err)
	}

	h.wantState("s1", disconnected())

	// Not connected: it does nothing.
	if _, err := h.client.Disconnect(h.ctx, &whatsmeowv1.DisconnectRequest{SessionId: "s1"}); err != nil {
		t.Fatalf("Disconnect again: %v", err)
	}

	if d := h.stub("s1").disconnects; d != 1 {
		t.Fatalf("disconnects = %d, want 1", d)
	}
}

func TestLogoutRecordsLoggedOutWithCodeZero(t *testing.T) {
	t.Parallel()

	h := start(t)
	h.connected("s1")

	if _, err := h.client.Logout(h.ctx, &whatsmeowv1.LogoutRequest{SessionId: "s1"}); err != nil {
		t.Fatalf("Logout: %v", err)
	}

	if got := h.create("s1"); !proto.Equal(got, &whatsmeowv1.Session{Id: "s1", State: loggedOut(0)}) || h.stub("s1").logouts != 1 {
		t.Fatalf("after Logout: %v, %d logouts; want LoggedOut{0} and 1", got, h.stub("s1").logouts)
	}
}

func TestLogoutErrors(t *testing.T) {
	t.Parallel()

	cases := []struct {
		name     string
		err      error
		code     codes.Code
		reason   string
		metadata map[string]string
	}{
		{"timed out", whatsmeow.ErrIQTimedOut, codes.Unknown, "OUTCOME_UNKNOWN", nil},
		{"disconnected while waiting", whatsmeow.ErrIQDisconnected, codes.Unknown, "OUTCOME_UNKNOWN", nil},
		{"WhatsApp refused", &whatsmeow.IQError{Code: 500, Text: "internal-server-error"}, codes.FailedPrecondition, "WHATSAPP_ERROR", map[string]string{"code": "500"}},
		{"not connected", whatsmeow.ErrNotConnected, codes.Unavailable, "NOT_CONNECTED", nil},
		{"anything else", errors.New("error deleting data from store: disk full"), codes.Internal, "INTERNAL", nil},
	}

	for _, c := range cases {
		t.Run(c.name, func(t *testing.T) {
			t.Parallel()

			h := start(t)
			h.connected("s1")
			h.stub("s1").logoutErr = c.err

			_, err := h.client.Logout(h.ctx, &whatsmeowv1.LogoutRequest{SessionId: "s1"})
			wantReason(t, err, c.code, c.reason, c.metadata)
			h.wantState("s1", connected())
		})
	}
}

func TestLogoutPreconditions(t *testing.T) {
	t.Parallel()

	h := start(t)
	h.create("new")
	h.create("linked")
	h.link("linked", "1555:1@s.whatsapp.net")
	h.connected("out")
	h.stub("out").emit(&events.LoggedOut{Reason: events.ConnectFailureLoggedOut})

	for id, reason := range map[string]string{"new": "NOT_LOGGED_IN", "linked": "NOT_CONNECTED", "out": "LOGGED_OUT"} {
		_, err := h.client.Logout(h.ctx, &whatsmeowv1.LogoutRequest{SessionId: id})

		code := codes.FailedPrecondition
		if reason == "NOT_CONNECTED" {
			code = codes.Unavailable
		}

		wantReason(t, err, code, reason, nil)
	}
}

func TestConnectFromEachState(t *testing.T) {
	t.Parallel()

	future := time.Now().Add(time.Hour).Truncate(time.Second)

	cases := []struct {
		name     string
		setup    func(h *harness)
		reason   string // "" when it connects
		metadata map[string]string
		connects int
	}{
		{"never paired", func(*harness) {}, "NOT_LOGGED_IN", nil, 0},
		{"logged out", func(h *harness) { h.stub("s1").emit(&events.LoggedOut{Reason: events.ConnectFailureLoggedOut}) }, "LOGGED_OUT", nil, 1},
		{"connected", func(*harness) {}, "", nil, 1},
		{"reconnecting", func(h *harness) { h.stub("s1").emit(&events.Disconnected{}) }, "", nil, 1},
		{"disconnected", func(h *harness) { _, _ = h.svc.Disconnect(h.ctx, &whatsmeowv1.DisconnectRequest{SessionId: "s1"}) }, "", nil, 2},
		{"banned until later", func(h *harness) {
			h.stub("s1").emit(&events.TemporaryBan{Code: 101, Expire: time.Until(future) + time.Second})
		}, "TEMPORARILY_BANNED", nil, 1},
		{"banned without an end", func(h *harness) { h.stub("s1").emit(&events.TemporaryBan{Code: 101}) }, "", nil, 2},
		{"replaced", func(h *harness) { h.stub("s1").emit(&events.StreamReplaced{}) }, "", nil, 2},
		{"outdated", func(h *harness) { h.stub("s1").emit(&events.ClientOutdated{}) }, "", nil, 2},
		{"error", func(h *harness) { h.stub("s1").emit(&events.ConnectFailure{Reason: 418}) }, "", nil, 2},
	}

	for _, c := range cases {
		t.Run(c.name, func(t *testing.T) {
			t.Parallel()

			h := start(t)
			if c.name == "never paired" {
				h.create("s1")
			} else {
				h.connected("s1")
			}

			c.setup(h)

			_, err := h.client.Connect(h.ctx, &whatsmeowv1.ConnectRequest{SessionId: "s1"})

			switch c.reason {
			case "":
				if err != nil {
					t.Fatalf("Connect: %v", err)
				}
			case "TEMPORARILY_BANNED":
				end := h.state("s1").GetTempBanned().GetExpireTime().AsTime().UTC().Format(time.RFC3339)
				wantReason(t, err, codes.FailedPrecondition, c.reason, map[string]string{"expireTime": end})
			default:
				wantReason(t, err, codes.FailedPrecondition, c.reason, nil)
			}

			if got := h.stub("s1").connects(); got != c.connects {
				t.Fatalf("connects = %d, want %d", got, c.connects)
			}
		})
	}
}

func TestConnectIsConnectingUntilWhatsAppAcceptsTheLogin(t *testing.T) {
	t.Parallel()

	h := start(t)
	h.create("s1")
	h.link("s1", "1555:1@s.whatsapp.net")
	h.connect("s1")
	h.wantState("s1", connecting())

	h.stub("s1").emit(&events.Connected{})
	h.wantState("s1", connected())
}

func TestAFailedConnectRestoresThePreviousState(t *testing.T) {
	t.Parallel()

	h := start(t)
	h.connected("s1")
	h.stub("s1").emit(&events.StreamReplaced{})
	h.stub("s1").connectErr = errors.New("websocket: bad handshake")

	_, err := h.client.Connect(h.ctx, &whatsmeowv1.ConnectRequest{SessionId: "s1"})
	wantReason(t, err, codes.Unavailable, "CONNECTION_FAILED", nil)
	h.wantState("s1", replaced())
}

func TestConnectUsesTheSidecarsLifetimeNotTheCall(t *testing.T) {
	t.Parallel()

	h := start(t)
	h.connected("s1")

	ctx := h.stub("s1").connectCtxs[0]
	if ctx.Err() != nil {
		t.Fatalf("the connection's context ended with the call: %v", ctx.Err())
	}

	h.stop()

	if ctx.Err() == nil {
		t.Fatal("the connection's context outlived the sidecar")
	}
}

func TestWatchSendsTheCurrentStateFirstThenEachChange(t *testing.T) {
	t.Parallel()

	h := start(t)
	h.create("s1")
	h.link("s1", "1555:1@s.whatsapp.net")

	stream, err := h.client.WatchSessionState(h.ctx, &whatsmeowv1.WatchSessionStateRequest{SessionId: "s1"})
	if err != nil {
		t.Fatalf("WatchSessionState: %v", err)
	}

	recv := func(want *whatsmeowv1.SessionState) {
		t.Helper()

		resp, err := stream.Recv()
		if err != nil || !proto.Equal(resp.GetState(), want) {
			t.Fatalf("Recv = %v, %v; want %v", resp.GetState(), err, want)
		}
	}

	recv(disconnected())
	h.connect("s1")
	recv(connecting())
	h.stub("s1").emit(&events.Connected{})
	recv(connected())
	h.stub("s1").emit(&events.OfflineSyncCompleted{})
	recv(caughtUp())
}

// blockingStream is a WatchSessionState stream whose Send waits until the test lets it through.
type blockingStream struct {
	grpc.ServerStreamingServer[whatsmeowv1.WatchSessionStateResponse]

	ctx     context.Context //nolint:containedctx // the stream's context, as grpc.ServerStream requires
	entered chan *whatsmeowv1.SessionState
	release chan struct{}
}

func (s *blockingStream) Context() context.Context { return s.ctx }

func (s *blockingStream) Send(resp *whatsmeowv1.WatchSessionStateResponse) error {
	s.entered <- resp.GetState()
	<-s.release

	return nil
}

func TestASlowWatcherGetsTheLatestState(t *testing.T) {
	t.Parallel()

	h := start(t)
	h.create("s1")
	h.link("s1", "1555:1@s.whatsapp.net")

	ctx, cancel := context.WithCancel(context.Background())
	stream := &blockingStream{ctx: ctx, entered: make(chan *whatsmeowv1.SessionState), release: make(chan struct{})}
	watched := make(chan error, 1)

	go func() {
		watched <- h.svc.WatchSessionState(&whatsmeowv1.WatchSessionStateRequest{SessionId: "s1"}, stream)
	}()

	if got := <-stream.entered; !proto.Equal(got, disconnected()) {
		t.Fatalf("first = %v, want Disconnected", got)
	}

	// The watcher is stuck sending the first state while three more happen.
	h.connect("s1")
	h.stub("s1").emit(&events.Connected{})
	h.stub("s1").emit(&events.OfflineSyncCompleted{})
	stream.release <- struct{}{}

	if got := <-stream.entered; !proto.Equal(got, caughtUp()) {
		t.Fatalf("after falling behind = %v, want the latest, CaughtUp", got)
	}

	// Connecting and Connected were dropped: the next one is the next change.
	h.stub("s1").emit(&events.Disconnected{})
	stream.release <- struct{}{}

	if got := <-stream.entered; !proto.Equal(got, reconnecting()) {
		t.Fatalf("next = %v, want Reconnecting", got)
	}

	cancel()
	stream.release <- struct{}{}

	if err := <-watched; !errors.Is(err, context.Canceled) {
		t.Fatalf("WatchSessionState = %v, want it to end with the cancel", err)
	}
}

func TestARestartKeepsTheStoppingStatesAndNeverConnects(t *testing.T) {
	t.Parallel()

	h := start(t)
	end := time.Now().Add(time.Hour)

	h.create("created")
	h.create("linked")
	h.link("linked", "1:1@s.whatsapp.net")
	h.connected("connected")
	h.connected("caughtup")
	h.stub("caughtup").emit(&events.OfflineSyncCompleted{})
	h.connected("reconnecting")
	h.stub("reconnecting").emit(&events.Disconnected{})
	h.connected("loggedout")
	h.stub("loggedout").emit(&events.LoggedOut{OnConnect: true, Reason: events.ConnectFailureUnknownLogout})
	h.connected("replaced")
	h.stub("replaced").emit(&events.StreamReplaced{})
	h.connected("outdated")
	h.stub("outdated").emit(&events.ClientOutdated{})
	h.connected("banned")
	h.stub("banned").emit(&events.TemporaryBan{Code: 101, Expire: time.Until(end)})
	h.connected("error")
	h.stub("error").emit(&events.ConnectFailure{Reason: 418, Message: "unknown"})

	banned := h.state("banned")

	h.stop()

	restarted := startOn(t, h.path)

	want := map[string]*whatsmeowv1.SessionState{
		"created": created(), "linked": disconnected(), "connected": disconnected(), "caughtup": disconnected(),
		"reconnecting": disconnected(), "loggedout": loggedOut(406), "replaced": replaced(), "outdated": outdated(),
		"banned": banned, "error": failed(418, "unknown"),
	}

	for id, state := range want {
		restarted.wantState(id, state)

		if n := restarted.stub(id).connects(); n != 0 {
			t.Errorf("session %q connected %d times on load", id, n)
		}
	}

	if got := restarted.create("replaced").GetJid(); got != "replaced:1@s.whatsapp.net" {
		t.Errorf("a Replaced session lost its device: jid %q", got)
	}
}

func TestARestartWithTheDeviceGoneLoadsLoggedOut(t *testing.T) {
	t.Parallel()

	h := start(t)
	h.create("s1")
	h.link("s1", "1555:1@s.whatsapp.net")

	if _, err := h.db.DB.ExecContext(h.ctx, "DELETE FROM whatsmeow_device"); err != nil {
		t.Fatalf("delete the device: %v", err)
	}

	h.stop()

	restarted := startOn(t, h.path)
	restarted.wantState("s1", loggedOut(0))
	restarted.stop()

	// Recorded, so the next start finds it as it is.
	startOn(t, h.path).wantState("s1", loggedOut(0))
}

func TestTheWhatsmeowClientIsConfiguredAsArchitecture72(t *testing.T) {
	t.Parallel()

	cli := NewWhatsmeowClient(zerolog.Nop())("s1", (&sqlstoreless{}).device(), func() {}).(*whatsmeow.Client) //nolint:forcetypeassert // the release factory's type

	if !cli.SynchronousAck || !cli.EnableDecryptedEventBuffer || !cli.ManualHistorySyncDownload || !cli.UseRetryMessageStore ||
		!cli.AutomaticMessageRerequestFromPhone || !cli.InitialAutoReconnect || !cli.EnableAutoReconnect || cli.AutoReconnectHook != nil {
		t.Fatalf("the client is not configured as ARCHITECTURE §7.2 says: %+v", cli)
	}
}

func TestTheWhatsmeowClientLogsNoURLQuery(t *testing.T) {
	t.Parallel()

	var buf bytes.Buffer

	cli := NewWhatsmeowClient(zerolog.New(&buf))("s1", (&sqlstoreless{}).device(), func() {}).(*whatsmeow.Client) //nolint:forcetypeassert // the release factory's type

	// As whatsmeow logs a failed media download (download-to-file.go:116).
	cli.Log.Warnf("Failed to download media: %s, trying with next host...", `Get "https://mmg.whatsapp.net/v/t62/1.enc?oh=SECRET": EOF`)

	if got := buf.String(); strings.Contains(got, "SECRET") || !strings.Contains(got, "mmg.whatsapp.net/v/t62/1.enc") {
		t.Fatalf("whatsmeow logged %s", got)
	}
}

func TestTheWhatsmeowClientEmitsAppStateEventsOnFullSync(t *testing.T) {
	t.Parallel()

	cli := NewWhatsmeowClient(zerolog.Nop())("s1", (&sqlstoreless{}).device(), func() {}).(*whatsmeow.Client) //nolint:forcetypeassert // the release factory's type

	// Without it a full sync dispatches no chat state event (appstate.go:70-72): the state after pairing and after a
	// recovery would never arrive (ADR 0031).
	if !cli.EmitAppStateEventsOnFullSync {
		t.Fatal("the client does not emit app state events on a full sync")
	}
}

// sqlstoreless builds a device without a database, for the factory test.
type sqlstoreless struct{}

func (sqlstoreless) device() *store.Device { return &store.Device{} }

func TestDeleteSessionDisconnectsBeforeItDeletesTheDevice(t *testing.T) {
	t.Parallel()

	h := start(t)
	h.connected("s1")

	// The device rows when Delete disconnects: still there, so the connection closed before they went.
	rowsAtDisconnect := -1
	s := h.stub("s1")
	s.disconnecting = func() {
		if err := h.db.DB.QueryRowContext(context.Background(), "SELECT count(*) FROM whatsmeow_device").Scan(&rowsAtDisconnect); err != nil {
			t.Errorf("count: %v", err)
		}
	}

	if _, err := h.client.DeleteSession(h.ctx, &whatsmeowv1.DeleteSessionRequest{SessionId: "s1"}); err != nil {
		t.Fatalf("DeleteSession: %v", err)
	}

	var rows int
	if err := h.db.DB.QueryRowContext(h.ctx, "SELECT count(*) FROM whatsmeow_device").Scan(&rows); err != nil {
		t.Fatalf("count: %v", err)
	}

	if rowsAtDisconnect != 1 || rows != 0 {
		t.Fatalf("device rows %d when it disconnected and %d after; want 1, then 0", rowsAtDisconnect, rows)
	}
}

func TestAConnectingWriteThatFailsLeavesThePreviousState(t *testing.T) {
	t.Parallel()

	h := start(t)
	h.create("s1")
	h.link("s1", "1555:1@s.whatsapp.net")

	if _, err := h.db.DB.ExecContext(h.ctx, "CREATE TRIGGER fail BEFORE UPDATE ON session BEGIN SELECT RAISE(ABORT, 'disk full'); END"); err != nil {
		t.Fatalf("trigger: %v", err)
	}

	_, err := h.client.Connect(h.ctx, &whatsmeowv1.ConnectRequest{SessionId: "s1"})
	wantReason(t, err, codes.Internal, "INTERNAL", nil)
	h.wantState("s1", disconnected())

	if _, err := h.db.DB.ExecContext(h.ctx, "DROP TRIGGER fail"); err != nil {
		t.Fatalf("drop the trigger: %v", err)
	}

	h.connect("s1")

	if n := h.stub("s1").connects(); n != 1 {
		t.Fatalf("connects = %d, want 1: the next Connect connects", n)
	}
}

func TestAFailedConnectWithACancelledCallKeepsTheStoppingStateAcrossARestart(t *testing.T) {
	t.Parallel()

	h := start(t)
	h.connected("s1")
	h.stub("s1").emit(&events.StreamReplaced{})

	ctx, cancel := context.WithCancel(context.Background())
	s := h.stub("s1")
	s.connectErr = errors.New("websocket: bad handshake")
	s.connecting = func(context.Context) { cancel() }

	if _, err := h.svc.Connect(ctx, &whatsmeowv1.ConnectRequest{SessionId: "s1"}); err == nil {
		t.Fatal("Connect succeeded, want CONNECTION_FAILED")
	}

	h.wantState("s1", replaced())
	h.stop()

	startOn(t, h.path).wantState("s1", replaced())
}

func TestLogoutWhoseStoreDeleteFailedAfterTheUnlinkRecordsDisconnected(t *testing.T) {
	t.Parallel()

	h := start(t)
	h.connected("s1")

	// whatsmeow disconnects once WhatsApp unlinked the device, then deleting the store fails (client.go:771-776).
	s := h.stub("s1")
	s.logoutErr = errors.New("error deleting data from store: disk full")
	s.connecting = nil
	s.mu.Lock()
	s.connected = false
	s.mu.Unlock()

	_, err := h.client.Logout(h.ctx, &whatsmeowv1.LogoutRequest{SessionId: "s1"})
	wantReason(t, err, codes.Internal, "INTERNAL", nil)
	h.wantState("s1", disconnected())
}

func TestConnectAfterABanEndedConnects(t *testing.T) {
	t.Parallel()

	h := start(t)
	h.connected("s1")
	h.setState("s1", tempBanned(101, timestamppb.New(time.Now().Add(-time.Minute))))

	h.connect("s1")

	if n := h.stub("s1").connects(); n != 2 {
		t.Fatalf("connects = %d, want 2: the ban has ended", n)
	}
}

func TestCloseDoesNotWaitForAConnectInFlight(t *testing.T) {
	t.Parallel()

	h := start(t)
	h.create("s1")
	h.link("s1", "1555:1@s.whatsapp.net")

	entered := make(chan struct{})
	s := h.stub("s1")
	s.connecting = func(ctx context.Context) {
		close(entered)
		// A dial that ends only with its context, as whatsmeow's does (client.go:589).
		<-ctx.Done()
	}

	go func() { _, _ = h.svc.Connect(context.Background(), &whatsmeowv1.ConnectRequest{SessionId: "s1"}) }()

	<-entered

	closed := make(chan struct{})

	go func() {
		h.svc.Close()
		close(closed)
	}()

	select {
	case <-closed:
	case <-h.ctx.Done():
		t.Fatal("Close waited for the connect in flight")
	}
}
