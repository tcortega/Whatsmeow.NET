package presence

import (
	"context"
	"errors"
	"fmt"
	"net"
	"slices"
	"sync"
	"testing"
	"time"

	"github.com/rs/zerolog"
	"go.mau.fi/whatsmeow"
	"go.mau.fi/whatsmeow/socket"
	"go.mau.fi/whatsmeow/types"
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
)

const token = "00112233445566778899aabbccddeeff00112233445566778899aabbccddeeff"

// stub stands in for whatsmeow behind both the session slice's interface and this slice's.
type stub struct {
	session.Client

	mu    sync.Mutex
	calls []string
	err   error
}

func (s *stub) record(call string) error {
	s.mu.Lock()
	defer s.mu.Unlock()

	s.calls = append(s.calls, call)

	return s.err
}

func (s *stub) SendPresence(_ context.Context, state types.Presence) error {
	return s.record("presence " + string(state))
}

func (s *stub) SubscribePresence(_ context.Context, jid types.JID) error {
	return s.record("subscribe " + jid.String())
}

func (s *stub) SendChatPresence(_ context.Context, jid types.JID, state types.ChatPresence, media types.ChatPresenceMedia) error {
	return s.record(fmt.Sprintf("chat %s %s %q", jid, state, media))
}

// onlyASessionClient has none of this slice's methods.
type onlyASessionClient struct{ session.Client }

// sessions answers Online with a client or an error per session.
type sessions map[string]struct {
	client session.Client
	err    error
}

func (s sessions) Online(id string) (session.Client, <-chan struct{}, error) {
	online, ok := s[id]
	if !ok {
		return nil, nil, fmt.Errorf("session %q: %w", id, server.ErrSessionNotFound)
	}

	if online.err != nil {
		return nil, nil, online.err
	}

	return online.client, make(chan struct{}), nil
}

func serve(t *testing.T, s sessions) (whatsmeowv1.PresenceServiceClient, context.Context) {
	t.Helper()

	srv := server.New(token, zerolog.Nop(), sidecarlog.NewRedactor())
	whatsmeowv1.RegisterPresenceServiceServer(srv, New(s))

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
	})

	// A bound for a broken slice, never a wait.
	ctx, cancel := context.WithTimeout(context.Background(), 30*time.Second)
	t.Cleanup(cancel)

	return whatsmeowv1.NewPresenceServiceClient(conn), metadata.AppendToOutgoingContext(ctx, "authorization", "Bearer "+token)
}

// call makes the call req is the request of.
func call(ctx context.Context, presence whatsmeowv1.PresenceServiceClient, req proto.Message) error {
	var err error

	switch r := req.(type) {
	case *whatsmeowv1.SendPresenceRequest:
		_, err = presence.SendPresence(ctx, r)
	case *whatsmeowv1.SubscribePresenceRequest:
		_, err = presence.SubscribePresence(ctx, r)
	case *whatsmeowv1.SendChatPresenceRequest:
		_, err = presence.SendChatPresence(ctx, r)
	default:
		err = fmt.Errorf("no call takes a %T", req)
	}

	return err
}

func wantReason(t *testing.T, what string, err error, code codes.Code, reason string) {
	t.Helper()

	st, _ := status.FromError(err)
	if st.Code() != code || len(st.Details()) != 1 {
		t.Errorf("%s: error %v, want %v with one ErrorInfo", what, err, code)

		return
	}

	if info, _ := st.Details()[0].(*errdetails.ErrorInfo); info.GetReason() != reason {
		t.Errorf("%s: reason %q, want %q", what, info.GetReason(), reason)
	}
}

func TestEachCallSendsWhatWhatsmeowIsAskedFor(t *testing.T) {
	t.Parallel()

	client := &stub{}
	presence, ctx := serve(t, sessions{"s1": {client: client}})

	calls := []proto.Message{
		&whatsmeowv1.SendPresenceRequest{SessionId: "s1", State: whatsmeowv1.PresenceState_PRESENCE_STATE_AVAILABLE},
		&whatsmeowv1.SendPresenceRequest{SessionId: "s1", State: whatsmeowv1.PresenceState_PRESENCE_STATE_UNAVAILABLE},
		&whatsmeowv1.SubscribePresenceRequest{SessionId: "s1", User: "111@lid"},
		&whatsmeowv1.SendChatPresenceRequest{SessionId: "s1", Chat: "111@s.whatsapp.net", State: whatsmeowv1.ChatPresenceState_CHAT_PRESENCE_STATE_TYPING},
		&whatsmeowv1.SendChatPresenceRequest{SessionId: "s1", Chat: "111@s.whatsapp.net", State: whatsmeowv1.ChatPresenceState_CHAT_PRESENCE_STATE_RECORDING},
		&whatsmeowv1.SendChatPresenceRequest{SessionId: "s1", Chat: "120363000000000000@g.us", State: whatsmeowv1.ChatPresenceState_CHAT_PRESENCE_STATE_PAUSED},
	}

	for i, req := range calls {
		if err := call(ctx, presence, req); err != nil {
			t.Fatalf("call %d: %v", i, err)
		}
	}

	want := []string{
		"presence available",
		"presence unavailable",
		"subscribe 111@lid",
		`chat 111@s.whatsapp.net composing ""`,
		`chat 111@s.whatsapp.net composing "audio"`,
		`chat 120363000000000000@g.us paused ""`,
	}
	if !slices.Equal(client.calls, want) {
		t.Fatalf("whatsmeow was asked for %q, want %q", client.calls, want)
	}
}

func TestACallFailsWithItsReasonAndAsksWhatsmeowForNothingWhenRefused(t *testing.T) {
	t.Parallel()

	client := &stub{}
	presence, ctx := serve(t, sessions{
		"s1":       {client: client},
		"offline":  {err: fmt.Errorf("session %q: %w", "offline", server.ErrNotConnected)},
		"banned":   {err: fmt.Errorf("session %q: %w", "banned", server.ErrTemporarilyBanned)},
		"unpaired": {err: fmt.Errorf("session %q: %w", "unpaired", server.ErrNotLoggedIn)},
		"other":    {client: onlyASessionClient{}},
	})

	available := whatsmeowv1.PresenceState_PRESENCE_STATE_AVAILABLE
	typing := whatsmeowv1.ChatPresenceState_CHAT_PRESENCE_STATE_TYPING

	cases := []struct {
		what   string
		req    proto.Message
		code   codes.Code
		reason string
	}{
		{"unspecified presence", &whatsmeowv1.SendPresenceRequest{SessionId: "s1"}, codes.InvalidArgument, "INVALID_ARGUMENT"},
		{"unspecified chat state", &whatsmeowv1.SendChatPresenceRequest{SessionId: "s1", Chat: "111@lid"}, codes.InvalidArgument, "INVALID_ARGUMENT"},
		{"no chat", &whatsmeowv1.SendChatPresenceRequest{SessionId: "s1", State: typing}, codes.InvalidArgument, "INVALID_ARGUMENT"},
		{"a chat without a server", &whatsmeowv1.SendChatPresenceRequest{SessionId: "s1", Chat: "111@", State: typing}, codes.InvalidArgument, "INVALID_ARGUMENT"},
		{"a chat without a user", &whatsmeowv1.SendChatPresenceRequest{SessionId: "s1", Chat: "111", State: typing}, codes.InvalidArgument, "INVALID_ARGUMENT"},
		{"a malformed user", &whatsmeowv1.SubscribePresenceRequest{SessionId: "s1", User: "1.2.3@lid"}, codes.InvalidArgument, "INVALID_ARGUMENT"},
		{"unknown session", &whatsmeowv1.SendPresenceRequest{SessionId: "nope", State: available}, codes.NotFound, "SESSION_NOT_FOUND"},
		{"not connected", &whatsmeowv1.SubscribePresenceRequest{SessionId: "offline", User: "111@lid"}, codes.Unavailable, "NOT_CONNECTED"},
		{"banned", &whatsmeowv1.SendChatPresenceRequest{SessionId: "banned", Chat: "111@lid", State: typing}, codes.FailedPrecondition, "TEMPORARILY_BANNED"},
		{"never paired", &whatsmeowv1.SendPresenceRequest{SessionId: "unpaired", State: available}, codes.FailedPrecondition, "NOT_LOGGED_IN"},
		{"a client without presence", &whatsmeowv1.SendPresenceRequest{SessionId: "other", State: available}, codes.Internal, "INTERNAL"},
	}

	for _, c := range cases {
		wantReason(t, c.what, call(ctx, presence, c.req), c.code, c.reason)
	}

	if len(client.calls) != 0 {
		t.Fatalf("whatsmeow was asked for %q after refused calls", client.calls)
	}
}

func TestWhatsmeowsErrorsBecomeTheirReasons(t *testing.T) {
	t.Parallel()

	cases := []struct {
		err    error
		code   codes.Code
		reason string
	}{
		{whatsmeow.ErrNotConnected, codes.Unavailable, "NOT_CONNECTED"},
		{fmt.Errorf("send: %w", socket.ErrSocketClosed), codes.Unavailable, "NOT_CONNECTED"},
		{whatsmeow.ErrNoPushName, codes.FailedPrecondition, "NO_PUSH_NAME"},
		{fmt.Errorf("failed to get privacy token: %w", whatsmeow.ErrNotLoggedIn), codes.FailedPrecondition, "NOT_LOGGED_IN"},
		{errors.New("websocket: write failed"), codes.Internal, "INTERNAL"},
	}

	for _, c := range cases {
		client := &stub{err: c.err}
		presence, ctx := serve(t, sessions{"s1": {client: client}})

		err := call(ctx, presence, &whatsmeowv1.SendPresenceRequest{SessionId: "s1", State: whatsmeowv1.PresenceState_PRESENCE_STATE_AVAILABLE})
		wantReason(t, c.err.Error(), err, c.code, c.reason)
	}
}
