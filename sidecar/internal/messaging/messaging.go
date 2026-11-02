// Package messaging is the Messaging slice: text and raw sends, read receipts, and the receipts WhatsApp sends,
// which it maps for the Events slice to record (message.proto, ARCHITECTURE §7.5, ADR 0025).
//
// A send is recorded before it is handed to whatsmeow, so a repeat of its client message id is answered from the
// record and never sends twice; one whose outcome is unknown is never sent again (ARCHITECTURE §13 invariant 10).
package messaging

import (
	"context"
	"database/sql"
	"errors"
	"fmt"
	"regexp"
	"sync"
	"time"

	"github.com/rs/zerolog"
	"go.mau.fi/whatsmeow"
	"go.mau.fi/whatsmeow/proto/waE2E"
	"go.mau.fi/whatsmeow/types"
	"google.golang.org/protobuf/proto"
	"google.golang.org/protobuf/types/known/timestamppb"

	whatsmeowv1 "github.com/tcortega/Whatsmeow.NET/sidecar/gen/whatsmeow/v1"
	"github.com/tcortega/Whatsmeow.NET/sidecar/internal/server"
	"github.com/tcortega/Whatsmeow.NET/sidecar/internal/session"
)

// whatsApp is what the slice needs of a session's whatsmeow client. *whatsmeow.Client and the fake WhatsApp's client
// both have it (ADR 0019); the session slice hands out the client (session.(*Service).Online).
type whatsApp interface {
	SendMessage(ctx context.Context, to types.JID, message *waE2E.Message, extra ...whatsmeow.SendRequestExtra) (whatsmeow.SendResponse, error)
	MarkRead(ctx context.Context, ids []types.MessageID, timestamp time.Time, chat, sender types.JID, receiptTypeExtra ...types.ReceiptType) error
}

var _ whatsApp = (*whatsmeow.Client)(nil)

// Sessions finds the client of a session that is online: session.(*Service).Online.
type Sessions interface {
	Online(id string) (session.Client, <-chan struct{}, error)
}

// The pacing of a session's sends: a burst of up to burst, then one every pace (ADR 0025). Tuning it is Phase 2.
const (
	pace  = time.Second
	burst = 5
)

// backoff is how long a send waits before each new try after WhatsApp rate limited it.
func backoff() []time.Duration { return []time.Duration{time.Second, 2 * time.Second, 4 * time.Second} }

// clientMessageID is the only id format accepted: the one whatsmeow generates (send.go:41, 47-61). Whether WhatsApp
// accepts others is unknown, so the rule is strict until it is measured (ADR 0025).
var clientMessageID = regexp.MustCompile(`^3EB0[0-9A-F]{18}$`)

// Service implements whatsmeowv1.MessageServiceServer.
type Service struct {
	whatsmeowv1.UnimplementedMessageServiceServer

	db       *sql.DB
	sessions Sessions
	log      zerolog.Logger
	// lifetime ends with Close, which fails every send still waiting.
	lifetime context.Context //nolint:containedctx // the slice's lifetime, not a request's
	end      context.CancelFunc

	pace    time.Duration
	burst   int
	backoff []time.Duration
	// onQueue and onWait, when set, run when a send has to wait behind another and when a repeat waits for the same
	// send in flight; tests wait on them.
	onQueue, onWait func()

	mu      sync.Mutex
	flights map[flightKey]*flight
	queues  map[string]*queue
	workers sync.WaitGroup
}

// New returns the slice on the database's send records and the sessions' clients.
func New(db *sql.DB, sessions Sessions, log zerolog.Logger) *Service {
	lifetime, end := context.WithCancel(context.Background())

	return &Service{
		db: db, sessions: sessions, log: log, lifetime: lifetime, end: end,
		pace: pace, burst: burst, backoff: backoff(),
		flights: map[flightKey]*flight{}, queues: map[string]*queue{},
	}
}

// Close fails the sends still queued with NOT_CONNECTED, as nothing of them was sent, and waits for the queues.
func (svc *Service) Close() {
	svc.end()
	svc.workers.Wait()
}

// SendText sends a text message (message.proto).
func (svc *Service) SendText(ctx context.Context, req *whatsmeowv1.SendTextRequest) (*whatsmeowv1.SendTextResponse, error) {
	if req.GetText() == "" {
		return nil, invalid("text", "the text is empty")
	}

	sent, err := svc.send(ctx, req.GetSessionId(), req.GetChat(), req.GetClientMessageId(), &waE2E.Message{Conversation: proto.String(req.GetText())})
	if err != nil {
		return nil, err
	}

	return &whatsmeowv1.SendTextResponse{Chat: sent.chat, MessageTime: timestamppb.New(sent.at)}, nil
}

// SendMessage sends a caller-built message unchanged (message.proto).
func (svc *Service) SendMessage(ctx context.Context, req *whatsmeowv1.SendMessageRequest) (*whatsmeowv1.SendMessageResponse, error) {
	message := req.GetMessage()
	if message == nil {
		return nil, invalid("message", "no message")
	}

	// The lenient codec let it through (ARCHITECTURE §6); whatsmeow's proto.Marshal would refuse it (send.go:1261),
	// and refusing it here sends nothing for sure.
	if err := proto.CheckInitialized(message); err != nil {
		return nil, invalid("message", "%v", err)
	}

	sent, err := svc.send(ctx, req.GetSessionId(), req.GetChat(), req.GetClientMessageId(), message)
	if err != nil {
		return nil, err
	}

	return &whatsmeowv1.SendMessageResponse{Chat: sent.chat, MessageTime: timestamppb.New(sent.at)}, nil
}

// MarkRead sends one read receipt, timestamped now, for messages of one chat (receipt.go:197-237). It is not queued:
// a receipt is not a message, and sending it again changes nothing.
func (svc *Service) MarkRead(ctx context.Context, req *whatsmeowv1.MarkReadRequest) (*whatsmeowv1.MarkReadResponse, error) {
	if err := validateSession(req.GetSessionId()); err != nil {
		return nil, err
	}

	chat, err := recipient("chat", req.GetChat())
	if err != nil {
		return nil, err
	}

	if len(req.GetMessageIds()) == 0 {
		return nil, invalid("message_ids", "no message ids")
	}

	var sender types.JID

	switch {
	case req.GetSender() != "":
		if sender, err = user("sender", req.GetSender()); err != nil {
			return nil, err
		}
	case chat.Server == types.GroupServer:
		// whatsmeow marks the sender's messages; without one a group receipt names nobody (receipt.go:223-225).
		return nil, invalid("sender", "a group chat needs the messages' sender")
	}

	client, err := svc.client(req.GetSessionId())
	if err != nil {
		return nil, err
	}

	if err := client.MarkRead(ctx, req.GetMessageIds(), time.Now(), chat, sender); err != nil {
		if errors.Is(err, whatsmeow.ErrNotConnected) {
			return nil, fmt.Errorf("mark read in session %q: %w: %w", req.GetSessionId(), server.ErrNotConnected, whatsmeow.ErrNotConnected)
		}

		// The cause only, as for a send (classify).
		return nil, fmt.Errorf("mark read in session %q: %w", req.GetSessionId(), cause(err))
	}

	return &whatsmeowv1.MarkReadResponse{}, nil
}

// client is the whatsmeow client of a session that is online.
func (svc *Service) client(sessionID string) (whatsApp, error) {
	client, _, err := svc.sessions.Online(sessionID)
	if err != nil {
		return nil, fmt.Errorf("session %q: %w", sessionID, err)
	}

	wa, ok := client.(whatsApp)
	if !ok {
		return nil, fmt.Errorf("session %q: its client %T cannot send", sessionID, client)
	}

	return wa, nil
}

func validateSession(id string) error {
	if err := server.CheckID("session_id", id); err != nil {
		return fmt.Errorf("session id: %w", err)
	}

	return nil
}

// recipient is a chat to send to, as message.proto says: a user without a device (by phone number or LID), or a
// group. whatsmeow refuses a device (send.go:203-206); newsletters and broadcast lists are not in v1.
func recipient(field, chat string) (types.JID, error) {
	to, err := types.ParseJID(chat)
	if chat == "" || err != nil || to.User == "" {
		return types.JID{}, invalid(field, "%q is not a JID", chat)
	}

	if to.Device != 0 {
		return types.JID{}, invalid(field, "%q names a device; send to the user", chat)
	}

	switch to.Server {
	case types.DefaultUserServer, types.HiddenUserServer, types.GroupServer:
		return to, nil
	default:
		return types.JID{}, invalid(field, "%q is not a user or a group", chat)
	}
}

// user is a user's JID, by phone number or LID, with or without a device.
func user(field, value string) (types.JID, error) {
	j, err := types.ParseJID(value)
	if err != nil || j.User == "" || (j.Server != types.DefaultUserServer && j.Server != types.HiddenUserServer) {
		return types.JID{}, invalid(field, "%q is not a user's JID", value)
	}

	return j, nil
}

// invalid is INVALID_ARGUMENT for the request field field.
func invalid(field, format string, args ...any) error {
	return fmt.Errorf("invalid request: %w", server.InvalidArgument(field, format, args...))
}
