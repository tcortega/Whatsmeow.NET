package messaging

import (
	"bytes"
	"context"
	"database/sql"
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
	"go.mau.fi/whatsmeow/proto/waE2E"
	"go.mau.fi/whatsmeow/store"
	"go.mau.fi/whatsmeow/types"
	"google.golang.org/genproto/googleapis/rpc/errdetails"
	"google.golang.org/grpc"
	"google.golang.org/grpc/codes"
	"google.golang.org/grpc/credentials/insecure"
	"google.golang.org/grpc/mem"
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

// at is WhatsApp's timestamp of every send the stub accepts, whole seconds as WhatsApp sends them (send.go:467).
var at = time.Unix(1_700_000_200, 0) //nolint:gochecknoglobals // a test constant

func jid(s string) types.JID {
	j, err := types.ParseJID(s)
	if err != nil {
		panic(err)
	}

	return j
}

// id is the n-th valid client message id: whatsmeow's format (send.go:41, 60).
func id(n int) string { return fmt.Sprintf("3EB0%018X", n) }

// call is one SendMessage the stub got.
type call struct {
	to      types.JID
	id      string
	message *waE2E.Message
	at      time.Time
}

// read is one MarkRead the stub got.
type read struct {
	ids          []types.MessageID
	at           time.Time
	chat, sender types.JID
}

// stub stands in for whatsmeow behind the slice's interface. It embeds session.Client so it is one; the session
// methods are never called here.
type stub struct {
	session.Client

	mu    sync.Mutex
	calls []call
	reads []read
	// send answers each SendMessage; nil sends to the requested chat at at.
	send    func(ctx context.Context, c call) (whatsmeow.SendResponse, error)
	readErr error
	// got gets each call as it starts.
	got chan call
	// wa is a real whatsmeow client of self's device, never connected: the builders are whatsmeow's own.
	wa *whatsmeow.Client
	// groupErr is what GetGroupInfo answers when set; otherwise lidGroup is LID-addressed and every other group is not.
	groupErr error
	// groupInfos counts the GetGroupInfo calls, each a request to WhatsApp.
	groupInfos int
}

// The account the stub's device is: its device JID and LID, as WhatsApp assigns them.
const (
	self    = "5511900000001:3@s.whatsapp.net"
	selfLID = "900000001:3@lid"
)

func (s *stub) BuildReaction(chat, sender types.JID, id types.MessageID, reaction string) *waE2E.Message {
	return s.wa.BuildReaction(chat, sender, id, reaction)
}

func (s *stub) BuildRevoke(chat, sender types.JID, id types.MessageID) *waE2E.Message {
	return s.wa.BuildRevoke(chat, sender, id)
}

func (s *stub) BuildEdit(chat types.JID, id types.MessageID, newContent *waE2E.Message) *waE2E.Message {
	return s.wa.BuildEdit(chat, id, newContent)
}

func (s *stub) BuildPollCreation(name string, optionNames []string, selectableOptionCount int) *waE2E.Message {
	return s.wa.BuildPollCreation(name, optionNames, selectableOptionCount)
}

func (s *stub) BuildPollVote(ctx context.Context, pollInfo *types.MessageInfo, optionNames []string) (*waE2E.Message, error) {
	return s.wa.BuildPollVote(ctx, pollInfo, optionNames) //nolint:wrapcheck // whatsmeow's own
}

// GetGroupInfo answers as whatsmeow parses WhatsApp's answer, with only the group's addressing mode (group.go:742-811).
func (s *stub) GetGroupInfo(_ context.Context, jid types.JID) (*types.GroupInfo, error) {
	s.mu.Lock()
	s.groupInfos++
	s.mu.Unlock()

	if s.groupErr != nil {
		return nil, s.groupErr
	}

	info := &types.GroupInfo{JID: jid, AddressingMode: types.AddressingModePN}
	if jid.String() == lidGroup {
		info.AddressingMode = types.AddressingModeLID
	}

	return info, nil
}

func (s *stub) SendMessage(ctx context.Context, to types.JID, message *waE2E.Message, extra ...whatsmeow.SendRequestExtra) (whatsmeow.SendResponse, error) {
	c := call{to: to, id: extra[0].ID, message: message, at: time.Now()}

	s.mu.Lock()
	s.calls = append(s.calls, c)
	send := s.send
	s.mu.Unlock()

	s.got <- c

	if send == nil {
		return whatsmeow.SendResponse{ID: c.id, Chat: to, Timestamp: at}, nil
	}

	return send(ctx, c)
}

func (s *stub) MarkRead(_ context.Context, ids []types.MessageID, timestamp time.Time, chat, sender types.JID, _ ...types.ReceiptType) error {
	s.mu.Lock()
	defer s.mu.Unlock()

	s.reads = append(s.reads, read{ids: ids, at: timestamp, chat: chat, sender: sender})

	return s.readErr
}

func (s *stub) groupInfoCalls() int {
	s.mu.Lock()
	defer s.mu.Unlock()

	return s.groupInfos
}

func (s *stub) sent() []call {
	s.mu.Lock()
	defer s.mu.Unlock()

	return append([]call(nil), s.calls...)
}

func (s *stub) ids() []string {
	var ids []string
	for _, c := range s.sent() {
		ids = append(ids, c.id)
	}

	return ids
}

// sessions stands in for the session slice's Online: s1, online with the stub until leave, and s3, always online.
type sessions struct {
	mu     sync.Mutex
	client *stub
	left   chan struct{}
	why    error
}

func (s *sessions) Online(id string) (session.Client, <-chan struct{}, error) {
	s.mu.Lock()
	defer s.mu.Unlock()

	if id == "s3" {
		return s.client, make(chan struct{}), nil
	}

	if id != "s1" {
		return nil, nil, fmt.Errorf("session %q: %w", id, server.ErrSessionNotFound)
	}

	if s.why != nil {
		return nil, nil, s.why
	}

	return s.client, s.left, nil
}

// Device is the device of s1 and s3, the stub's, until s1 leaves; another session is not found.
func (s *sessions) Device(id string) (*store.Device, error) {
	if id != "s1" && id != "s3" {
		return nil, fmt.Errorf("session %q: %w", id, server.ErrSessionNotFound)
	}

	s.mu.Lock()
	defer s.mu.Unlock()

	if id == "s1" && s.why != nil {
		return nil, s.why
	}

	return s.client.wa.Store, nil
}

// leave makes s1 stop being online, for the reason why.
func (s *sessions) leave(why error) {
	s.mu.Lock()
	defer s.mu.Unlock()

	s.why = why
	close(s.left)
}

// clientCodec marshals partial messages, as the .NET client does: Google.Protobuf is lenient (ARCHITECTURE §6).
type clientCodec struct{}

func (clientCodec) Marshal(v any) (mem.BufferSlice, error) {
	b, err := proto.MarshalOptions{AllowPartial: true}.Marshal(v.(proto.Message))
	if err != nil {
		return nil, fmt.Errorf("marshal: %w", err)
	}

	return mem.BufferSlice{mem.SliceBuffer(b)}, nil
}

func (clientCodec) Unmarshal(data mem.BufferSlice, v any) error {
	if err := (proto.UnmarshalOptions{AllowPartial: true}).Unmarshal(data.Materialize(), v.(proto.Message)); err != nil {
		return fmt.Errorf("unmarshal: %w", err)
	}

	return nil
}

func (clientCodec) Name() string { return "proto" }

type harness struct {
	t        *testing.T
	db       *sql.DB
	svc      *Service
	stub     *stub
	sessions *sessions
	client   whatsmeowv1.MessageServiceClient
	ctx      context.Context //nolint:containedctx // the test's call context
	// queued and waiting get a value each time a send joins the queue, and each time a repeat waits for one in flight.
	queued, waiting chan struct{}
	// logs is what the slice and the server logged at info level and above.
	logs *logs
}

// logs is a log written by several goroutines.
type logs struct {
	mu  sync.Mutex
	buf bytes.Buffer
}

func (l *logs) Write(p []byte) (int, error) {
	l.mu.Lock()
	defer l.mu.Unlock()

	return l.buf.Write(p) //nolint:wrapcheck // a bytes.Buffer never fails
}

func (l *logs) String() string {
	l.mu.Lock()
	defer l.mu.Unlock()

	return l.buf.String()
}

func start(t *testing.T) *harness {
	t.Helper()

	db, err := sidecarstore.Open(context.Background(), filepath.Join(t.TempDir(), "whatsmeow-net.db"), zerolog.Nop())
	if err != nil {
		t.Fatalf("open the store: %v", err)
	}

	if _, err := db.DB.ExecContext(context.Background(), "INSERT INTO session (id) VALUES ('s1'), ('s3')"); err != nil {
		t.Fatalf("create the sessions: %v", err)
	}

	// A saved device of the store, so whatsmeow's message secrets (MsgSecrets) are real.
	device := db.Devices.NewDevice()
	device.ID, device.LID = new(jid(self)), jid(selfLID)
	device.Account = &waAdv.ADVSignedDeviceIdentity{
		Details: []byte{}, AccountSignature: make([]byte, 64), AccountSignatureKey: make([]byte, 32), DeviceSignature: make([]byte, 64),
	}

	if err := device.Save(context.Background()); err != nil {
		t.Fatalf("save the device: %v", err)
	}

	h := &harness{
		t: t, db: db.DB, stub: &stub{got: make(chan call, 64), wa: whatsmeow.NewClient(device, nil)},
		queued: make(chan struct{}, 64), waiting: make(chan struct{}, 64), logs: &logs{},
	}
	// The sidecar logs at info level and above (ARCHITECTURE §7.8).
	log := zerolog.New(h.logs).Level(zerolog.InfoLevel)
	h.sessions = &sessions{client: h.stub, left: make(chan struct{})}
	h.svc = New(db.DB, db.Writer, h.sessions, log, Pacing{Interval: DefaultInterval, Burst: DefaultBurst})
	// A burst no test reaches unless it sets one; a test that paces or backs off gives the slice a fake clock (fakeClock).
	h.svc.pace, h.svc.burst, h.svc.backoff = 40*time.Millisecond, 100, []time.Duration{time.Millisecond, 2 * time.Millisecond, 4 * time.Millisecond}
	h.svc.margin = time.Millisecond
	h.svc.onQueue = func() { h.queued <- struct{}{} }
	h.svc.onWait = func() { h.waiting <- struct{}{} }

	srv := server.New(token, log, sidecarlog.NewRedactor())
	whatsmeowv1.RegisterMessageServiceServer(srv, h.svc)

	lis := bufconn.Listen(1 << 20)
	served := make(chan error, 1)

	go func() { served <- srv.Serve(lis) }()

	conn, err := grpc.NewClient("passthrough:///bufnet",
		grpc.WithContextDialer(func(ctx context.Context, _ string) (net.Conn, error) { return lis.DialContext(ctx) }),
		grpc.WithTransportCredentials(insecure.NewCredentials()),
		grpc.WithDefaultCallOptions(grpc.ForceCodecV2(clientCodec{})))
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
	h.client = whatsmeowv1.NewMessageServiceClient(conn)

	return h
}

func (h *harness) sendText(ctx context.Context, n int, text string) (*whatsmeowv1.SendTextResponse, error) {
	return h.client.SendText(ctx, &whatsmeowv1.SendTextRequest{SessionId: "s1", Chat: "111@lid", ClientMessageId: id(n), Text: text}) //nolint:wrapcheck // the test reads the status
}

// async sends text n on its own goroutine; the result comes on the returned channel.
func (h *harness) async(ctx context.Context, n int) <-chan result {
	done := make(chan result, 1)

	go func() {
		resp, err := h.sendText(ctx, n, "hello")
		done <- result{resp, err}
	}()

	return done
}

type result struct {
	resp *whatsmeowv1.SendTextResponse
	err  error
}

// next waits for the stub's next SendMessage.
func (h *harness) next() call {
	h.t.Helper()

	select {
	case c := <-h.stub.got:
		return c
	case <-h.ctx.Done():
		h.t.Fatal("no send reached whatsmeow")

		return call{}
	}
}

func (h *harness) wait(signal chan struct{}, what string) {
	h.t.Helper()

	select {
	case <-signal:
	case <-h.ctx.Done():
		h.t.Fatalf("no %s", what)
	}
}

func (h *harness) outcome(done <-chan result) result {
	h.t.Helper()

	select {
	case r := <-done:
		return r
	case <-h.ctx.Done():
		h.t.Fatal("the send did not return")

		return result{}
	}
}

// hold makes each send block until release is closed or its context ends, as whatsmeow waits for WhatsApp's answer
// after writing the node (send.go:445-455).
func (h *harness) hold() (release chan struct{}) {
	release = make(chan struct{})
	h.stub.mu.Lock()
	h.stub.send = func(ctx context.Context, c call) (whatsmeow.SendResponse, error) {
		select {
		case <-release:
			return whatsmeow.SendResponse{ID: c.id, Chat: c.to, Timestamp: at}, nil
		case <-ctx.Done():
			return whatsmeow.SendResponse{}, ctx.Err()
		}
	}
	h.stub.mu.Unlock()

	return release
}

// script makes the stub answer the sends with errs in turn, then succeed.
func (h *harness) script(errs ...error) {
	h.stub.mu.Lock()
	defer h.stub.mu.Unlock()

	h.stub.send = func(_ context.Context, c call) (whatsmeow.SendResponse, error) {
		if len(errs) == 0 {
			return whatsmeow.SendResponse{ID: c.id, Chat: c.to, Timestamp: at}, nil
		}

		err := errs[0]
		errs = errs[1:]

		return whatsmeow.SendResponse{}, err
	}
}

// record is the send record of client message id n: whether there is one, and its columns.
func (h *harness) record(n int) (bool, sql.NullString, sql.NullInt64, sql.NullInt64) {
	h.t.Helper()

	var (
		chat          sql.NullString
		when, waError sql.NullInt64
	)

	err := h.db.QueryRowContext(h.ctx, "SELECT chat, message_time, whatsapp_error FROM sent_message WHERE session_id = 's1' AND client_message_id = ?", id(n)).
		Scan(&chat, &when, &waError)
	if errors.Is(err, sql.ErrNoRows) {
		return false, chat, when, waError
	}

	if err != nil {
		h.t.Fatalf("read the record: %v", err)
	}

	return true, chat, when, waError
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

func TestSendTextSendsAConversationAndReportsWhereItWent(t *testing.T) {
	t.Parallel()

	h := start(t)
	// whatsmeow sends a phone-number JID to its LID and reports that (send.go:341-365, 384-385).
	h.stub.send = func(_ context.Context, c call) (whatsmeow.SendResponse, error) {
		return whatsmeow.SendResponse{ID: c.id, Chat: jid("222@lid"), Timestamp: at}, nil
	}

	resp, err := h.client.SendText(h.ctx, &whatsmeowv1.SendTextRequest{SessionId: "s1", Chat: "5511999999999@s.whatsapp.net", ClientMessageId: id(1), Text: "hi"})
	if err != nil {
		t.Fatalf("SendText: %v", err)
	}

	if resp.GetChat() != "222@lid" || !resp.GetMessageTime().AsTime().Equal(at) {
		t.Fatalf("SendText = %v, want 222@lid at %v", resp, at)
	}

	sent := h.stub.sent()
	if len(sent) != 1 || sent[0].to != jid("5511999999999@s.whatsapp.net") || sent[0].id != id(1) ||
		!proto.Equal(sent[0].message, &waE2E.Message{Conversation: proto.String("hi")}) {
		t.Fatalf("whatsmeow got %v, want one conversation to the requested chat with the client's id", sent)
	}
}

func TestSendMessageSendsTheMessageAsGiven(t *testing.T) {
	t.Parallel()

	h := start(t)
	message := &waE2E.Message{ExtendedTextMessage: &waE2E.ExtendedTextMessage{Text: proto.String("hi"), MatchedText: proto.String("https://example.org")}}

	resp, err := h.client.SendMessage(h.ctx, &whatsmeowv1.SendMessageRequest{SessionId: "s1", Chat: "120363000000000000@g.us", ClientMessageId: id(1), Message: message})
	if err != nil || resp.GetChat() != "120363000000000000@g.us" || !resp.GetMessageTime().AsTime().Equal(at) {
		t.Fatalf("SendMessage = %v, %v; want the group at %v", resp, err, at)
	}

	if sent := h.stub.sent(); len(sent) != 1 || !proto.Equal(sent[0].message, message) {
		t.Fatalf("whatsmeow got %v, want the message as given", sent)
	}
}

func TestASendIsRecordedBeforeItIsHandedOff(t *testing.T) {
	t.Parallel()

	h := start(t)
	recordedBefore := false
	h.stub.send = func(_ context.Context, c call) (whatsmeow.SendResponse, error) {
		// Pending: recorded, with no answer yet.
		exists, chat, _, _ := h.record(1)
		recordedBefore = exists && !chat.Valid

		return whatsmeow.SendResponse{ID: c.id, Chat: c.to, Timestamp: at}, nil
	}

	if _, err := h.sendText(h.ctx, 1, "hi"); err != nil {
		t.Fatalf("SendText: %v", err)
	}

	if exists, chat, when, _ := h.record(1); !recordedBefore || !exists || chat.String != "111@lid" || when.Int64 != at.Unix() {
		t.Fatalf("recorded before the hand-off %v; after: %v %v %v; want pending first, then the answer", recordedBefore, exists, chat, when)
	}
}

func TestARepeatAfterSuccessReturnsTheRecordedResultAndSendsNothing(t *testing.T) {
	t.Parallel()

	h := start(t)

	first, err := h.sendText(h.ctx, 1, "hi")
	if err != nil {
		t.Fatalf("SendText: %v", err)
	}

	// A repeat's other fields are ignored (message.proto), and the raw call shares the ids.
	again, err := h.sendText(h.ctx, 1, "something else")
	if err != nil || !proto.Equal(again, first) {
		t.Fatalf("the repeat = %v, %v; want %v", again, err, first)
	}

	raw, err := h.client.SendMessage(h.ctx, &whatsmeowv1.SendMessageRequest{SessionId: "s1", Chat: "111@lid", ClientMessageId: id(1), Message: &waE2E.Message{}})
	if err != nil || raw.GetChat() != first.GetChat() || !proto.Equal(raw.GetMessageTime(), first.GetMessageTime()) {
		t.Fatalf("the raw repeat = %v, %v; want %v", raw, err, first)
	}

	if got := h.stub.ids(); len(got) != 1 {
		t.Fatalf("whatsmeow got %v, want one send", got)
	}
}

func TestARepeatInFlightWaitsForTheOutcome(t *testing.T) {
	t.Parallel()

	h := start(t)
	release := h.hold()

	first := h.async(h.ctx, 1)
	h.next()

	repeat := h.async(h.ctx, 1)
	h.wait(h.waiting, "repeat waiting")
	close(release)

	a, b := h.outcome(first), h.outcome(repeat)
	if a.err != nil || b.err != nil || !proto.Equal(a.resp, b.resp) {
		t.Fatalf("first %v, %v; repeat %v, %v; want the same success", a.resp, a.err, b.resp, b.err)
	}

	if got := h.stub.ids(); len(got) != 1 {
		t.Fatalf("whatsmeow got %v, want one send", got)
	}
}

func TestAnUnknownOutcomeIsNeverRetried(t *testing.T) {
	t.Parallel()

	h := start(t)

	// What SendMessage returns once the node may be written (send.go:438-463, request.go:185-216), and what cannot be
	// told apart from it: retryFrame's unwrapped ErrNotConnected after the write (request.go:207-209), a failed write.
	unknown := []error{
		whatsmeow.ErrMessageTimedOut,
		&whatsmeow.DisconnectedError{Action: "message send"},
		whatsmeow.ErrNotConnected,
		fmt.Errorf("failed to send message node: %w", errors.New("write: broken pipe")),
		whatsmeow.ErrIQTimedOut,
	}

	for n, err := range unknown {
		h.script(err)

		_, got := h.sendText(h.ctx, n, "hi")
		wantReason(t, got, codes.Unknown, "OUTCOME_UNKNOWN", nil)

		// The repeat is unknown too, and sends nothing (invariant 10).
		_, got = h.sendText(h.ctx, n, "hi")
		wantReason(t, got, codes.Unknown, "OUTCOME_UNKNOWN", nil)

		if sent := h.stub.ids(); len(sent) != n+1 {
			t.Fatalf("%v: whatsmeow got %v, want one send each", err, sent)
		}
	}
}

func TestACancelAfterTheHandOffIsAnUnknownOutcome(t *testing.T) {
	t.Parallel()

	h := start(t)
	h.hold()

	ctx, cancel := context.WithCancel(h.ctx)
	first := h.async(ctx, 1)
	h.next()
	cancel()

	if r := h.outcome(first); status.Code(r.err) != codes.Canceled {
		t.Fatalf("the cancelled send = %v, %v; want CANCELLED", r.resp, r.err)
	}

	_, err := h.sendText(h.ctx, 1, "hi")
	wantReason(t, err, codes.Unknown, "OUTCOME_UNKNOWN", nil)

	if got := h.stub.ids(); len(got) != 1 {
		t.Fatalf("whatsmeow got %v, want one send", got)
	}
}

// A context error in the cause that is not the caller's, one of whatsmeow's own contexts after the hand-off, is no
// cancel by the caller: it stays an unknown outcome, never CANCELLED or DEADLINE_EXCEEDED.
func TestAContextErrorTheCallerDidNotCauseIsAnUnknownOutcome(t *testing.T) {
	t.Parallel()

	h := start(t)

	for n, err := range []error{
		fmt.Errorf("failed to send message node: %w", context.DeadlineExceeded),
		fmt.Errorf("failed to send message node: %w", context.Canceled),
	} {
		h.script(err)

		_, got := h.sendText(h.ctx, n, "hi")
		wantReason(t, got, codes.Unknown, "OUTCOME_UNKNOWN", nil)
	}
}

// The caller's own cancel after the hand-off keeps its context error, so the call ends with its own status. Called on
// the slice itself: a gRPC client returns CANCELLED on its own as soon as its context ends.
func TestTheCallersOwnCancelAfterTheHandOffKeepsItsContextError(t *testing.T) {
	t.Parallel()

	h := start(t)
	h.hold()

	ctx, cancel := context.WithCancel(h.ctx)
	done := make(chan error, 1)

	go func() {
		_, err := h.svc.SendText(ctx, &whatsmeowv1.SendTextRequest{SessionId: "s1", Chat: "111@lid", ClientMessageId: id(1), Text: "hi"})
		done <- err
	}()

	h.next()
	cancel()

	select {
	case err := <-done:
		if !errors.Is(err, context.Canceled) || !errors.Is(err, server.ErrOutcomeUnknown) {
			t.Fatalf("the cancelled send = %v, want an unknown outcome with its context's end", err)
		}
	case <-h.ctx.Done():
		t.Fatal("the cancelled send did not return")
	}
}

func TestAFailureThatSentNothingLeavesNoRecordSoARepeatSends(t *testing.T) {
	t.Parallel()

	h := start(t)

	// Each fails before the node is written (send.go:207-211, 341-365; the IQs in sendDM 875-918, sendGroup 814-840;
	// client.go:983-985).
	cases := []struct {
		err      error
		code     codes.Code
		reason   string
		metadata map[string]string
	}{
		{fmt.Errorf("%w: no LID found for 5511999999999@s.whatsapp.net from server", whatsmeow.ErrRecipientNotOnWhatsApp), codes.NotFound, "RECIPIENT_NOT_ON_WHATSAPP", nil},
		{fmt.Errorf("failed to get user info: %w", &whatsmeow.IQError{Code: 500, Text: "internal-server-error"}), codes.FailedPrecondition, "WHATSAPP_ERROR", map[string]string{"code": "500"}},
		// The device lost its ID: the session was logged out after it was found online.
		{whatsmeow.ErrNotLoggedIn, codes.FailedPrecondition, "LOGGED_OUT", nil},
		// No socket before the write (client.go:983-985), as sendDM wraps it (send.go:921).
		{fmt.Errorf("failed to send message node: %w", whatsmeow.ErrNotConnected), codes.Unavailable, "NOT_CONNECTED", nil},
	}

	for n, c := range cases {
		h.script(c.err)

		_, err := h.sendText(h.ctx, n, "hi")
		wantReason(t, err, c.code, c.reason, c.metadata)

		if exists, _, _, _ := h.record(n); exists {
			t.Fatalf("%v: a record is left", c.err)
		}

		if _, err := h.sendText(h.ctx, n, "hi"); err != nil {
			t.Fatalf("%v: the repeat failed: %v", c.err, err)
		}

		if sent := h.stub.ids(); len(sent) != 2*(n+1) {
			t.Fatalf("%v: whatsmeow got %v, want the repeat sent", c.err, sent)
		}
	}
}

// whatsmeow wraps the recipient's JID into its errors (send.go:346, 353, 356), and an IQ error prints its nodes
// (errors.go:217-227): other people's personal data, which neither a status message, which reaches the application,
// nor the sidecar's log may carry (ARCHITECTURE §7.8).
func TestNoErrorOrLogCarriesTheRecipientsJID(t *testing.T) {
	t.Parallel()

	h := start(t)
	pn := jid("5511999999999@s.whatsapp.net")
	node := &waBinary.Node{Tag: "error", Attrs: waBinary.Attrs{"jid": pn}}

	// whatsmeow's own texts at the pin.
	for n, err := range []error{
		fmt.Errorf("%w: no LID found for %s from server", whatsmeow.ErrRecipientNotOnWhatsApp, pn),
		fmt.Errorf("failed to get user info for %s to fill LID cache: %w", pn, &whatsmeow.IQError{Code: 500, Text: "internal-server-error", ErrorNode: node}),
		fmt.Errorf("failed to get user info for %s to fill LID cache: %w", pn, &whatsmeow.IQError{ErrorNode: node}),
		fmt.Errorf("failed to get user info for %s to fill LID cache: %w", pn, whatsmeow.ErrIQTimedOut),
		fmt.Errorf("failed to get LID for PN %s: %w", pn, errors.New("sqlite: database is locked")),
		fmt.Errorf("failed to get user info for %s to fill LID cache: %w", pn, &whatsmeow.IQError{Code: 429, Text: "rate-overlimit", ErrorNode: node}),
	} {
		h.script(err, err, err, err)

		_, got := h.sendText(h.ctx, n, "hi")
		if got == nil || strings.Contains(status.Convert(got).Message(), "@") || strings.Contains(status.Convert(got).Message(), "5511999999999") {
			t.Fatalf("for %v the call returned %v, want an error without the JID", err, got)
		}
	}

	if logged := h.logs.String(); strings.Contains(logged, "@") || strings.Contains(logged, "5511999999999") {
		t.Fatalf("the log has the JID:\n%s", logged)
	}
}

func TestAServerErrorIsRecordedAndRepeated(t *testing.T) {
	t.Parallel()

	h := start(t)
	// WhatsApp answered the node with an error code (send.go:468-470); whether it dropped the message is unknown, so it
	// is never sent again with this id (ADR 0025).
	h.script(fmt.Errorf("%w %d", whatsmeow.ErrServerReturnedError, 479))

	for range 2 {
		_, err := h.sendText(h.ctx, 1, "hi")
		wantReason(t, err, codes.FailedPrecondition, "WHATSAPP_ERROR", map[string]string{"code": "479"})
	}

	if got := h.stub.ids(); len(got) != 1 {
		t.Fatalf("whatsmeow got %v, want one send", got)
	}
}

func TestARateLimitIsBackedOffThenFailsWithRateLimited(t *testing.T) {
	t.Parallel()

	h := start(t)
	c := h.fakeClock()
	limited := fmt.Errorf("failed to get devices: %w", whatsmeow.ErrIQRateOverLimit)

	// Backed off twice, then sent.
	h.script(limited, limited)
	done := h.goSend(h.ctx, 1)
	h.backOff(c, done, h.svc.backoff[:2])

	if err := h.done(done); err != nil {
		t.Fatalf("SendText: %v", err)
	}

	if got := h.stub.ids(); len(got) != 3 {
		t.Fatalf("whatsmeow got %v, want two tries and the send", got)
	}

	// Past the backoff: rate limited, and nothing was sent, so a repeat sends.
	h.script(limited, limited, limited, limited)
	done = h.goSend(h.ctx, 2)
	h.backOff(c, done, h.svc.backoff)

	err := h.done(done)
	wantReason(t, err, codes.ResourceExhausted, "RATE_LIMITED", nil)

	if got := h.stub.ids(); len(got) != 3+4 {
		t.Fatalf("whatsmeow got %v, want 4 more tries", got)
	}

	if exists, _, _, _ := h.record(2); exists {
		t.Fatal("a rate-limited send left a record")
	}

	// A wait that would pass the call's deadline is not waited.
	h.svc.backoff = []time.Duration{time.Hour}
	h.script(limited)

	ctx, cancel := context.WithTimeout(h.ctx, 10*time.Second)
	defer cancel()

	_, err = h.sendText(ctx, 3, "hi")
	wantReason(t, err, codes.ResourceExhausted, "RATE_LIMITED", nil)
	h.noWait(c)
}

// backOff passes the backoff's waits, each the one want says, while done has not come.
func (h *harness) backOff(c *clock, done <-chan error, want []time.Duration) {
	h.t.Helper()

	for _, d := range want {
		w := h.nextWait(c, done)
		if w.d != d {
			h.t.Fatalf("the backoff waited %v, want %v", w.d, d)
		}

		c.pass(w)
	}
}

// A try needs time beyond its backoff: one started with no time left would run out after the hand-off, an unknown
// outcome, and the id could never be sent again. So a backoff that leaves less than the margin is not waited.
func TestARateLimitIsNotTriedAgainWithoutTimeForTheTry(t *testing.T) {
	t.Parallel()

	h := start(t)
	h.svc.backoff, h.svc.margin = []time.Duration{10 * time.Millisecond}, time.Hour
	limited := fmt.Errorf("failed to get devices: %w", whatsmeow.ErrIQRateOverLimit)
	h.script(limited, limited)

	ctx, cancel := context.WithTimeout(h.ctx, 10*time.Second)
	defer cancel()

	_, err := h.sendText(ctx, 1, "hi")
	wantReason(t, err, codes.ResourceExhausted, "RATE_LIMITED", nil)

	if got := h.stub.ids(); len(got) != 1 {
		t.Fatalf("whatsmeow got %v, want one try", got)
	}

	// Nothing was sent: the id is free for a repeat.
	if exists, _, _, _ := h.record(1); exists {
		t.Fatal("a rate-limited send left a record")
	}
}

func TestQueuedSendsGoOutInOrderAndPaced(t *testing.T) {
	t.Parallel()

	h := start(t)
	h.svc.burst = 1
	c := h.fakeClock()
	release := h.hold()

	first := h.async(h.ctx, 1)
	h.next()

	second := h.async(h.ctx, 2)
	h.wait(h.queued, "second queued")

	third := h.async(h.ctx, 3)
	h.wait(h.queued, "third queued")
	close(release)

	if r := h.outcome(first); r.err != nil {
		t.Fatalf("the first failed: %v", r.err)
	}

	// The second and the third each wait an interval after the send before them, and the jitter.
	for _, done := range []<-chan result{second, third} {
		w := h.nextWait(c, nil)
		if want := h.svc.pace + h.svc.pace/2 - time.Nanosecond; w.d != want {
			t.Fatalf("a queued send waited %v, want the interval and the jitter, %v", w.d, want)
		}

		c.pass(w)

		if r := h.outcome(done); r.err != nil {
			t.Fatalf("a send failed: %v", r.err)
		}
	}

	sent := h.stub.sent()
	if len(sent) != 3 || sent[0].id != id(1) || sent[1].id != id(2) || sent[2].id != id(3) {
		t.Fatalf("whatsmeow got %v, want 1, 2, 3 in order", h.stub.ids())
	}
}

func TestQueuedSendsFailWhenTheSessionStopsBeingOnline(t *testing.T) {
	t.Parallel()

	h := start(t)
	release := h.hold()

	first := h.async(h.ctx, 1)
	h.next()

	queued := h.async(h.ctx, 2)
	h.wait(h.queued, "second queued")

	// The session is logged out while the first is still in flight: the queued one fails at once.
	h.sessions.leave(fmt.Errorf("session %q: %w", "s1", server.ErrLoggedOut))
	wantReason(t, h.outcome(queued).err, codes.FailedPrecondition, "LOGGED_OUT", nil)

	close(release)
	h.outcome(first)

	if got := h.stub.ids(); len(got) != 1 {
		t.Fatalf("whatsmeow got %v, want only the first", got)
	}

	if exists, _, _, _ := h.record(2); exists {
		t.Fatal("the failed queued send left a record")
	}
}

// A call that ends while its send's record is in a write batch still has the record kept, as every write that joined a
// batch is (ADR 0068). Nothing was handed to whatsmeow yet, so the send ends with the call: nothing sent, the record
// gone, not left pending, and a repeat sends. Called on the slice itself, as for a cancel while queued.
func TestACancelWhileItsRecordIsWrittenSendsNothingAndLeavesNoRecord(t *testing.T) {
	t.Parallel()

	h := start(t)
	ctx, cancel := context.WithCancel(h.ctx)
	h.svc.onRecorded = cancel

	_, err := h.svc.SendText(ctx, &whatsmeowv1.SendTextRequest{SessionId: "s1", Chat: "111@lid", ClientMessageId: id(1), Text: "hello"})
	if !errors.Is(err, context.Canceled) {
		t.Fatalf("the send cancelled while its record was written = %v; want its context's end", err)
	}

	if got := h.stub.ids(); len(got) != 0 {
		t.Fatalf("whatsmeow got %v, want nothing: the call had ended", got)
	}

	if exists, _, _, _ := h.record(1); exists {
		t.Fatal("the cancelled send left its record pending: a repeat would read it as an unknown outcome")
	}

	h.svc.onRecorded = nil

	if _, err := h.sendText(h.ctx, 1, "hi"); err != nil {
		t.Fatalf("the repeat: %v", err)
	}

	if got := h.stub.ids(); len(got) != 1 || got[0] != id(1) {
		t.Fatalf("whatsmeow got %v, want the repeat of 1", got)
	}
}

func TestACancelWhileQueuedSendsNothingAndLeavesNoRecord(t *testing.T) {
	t.Parallel()

	h := start(t)
	release := h.hold()

	first := h.async(h.ctx, 1)
	h.next()

	// Called on the slice itself: a gRPC client returns CANCELLED as soon as its context ends, before the handler saw
	// the cancel, so the queue could take the send after the test checked.
	ctx, cancel := context.WithCancel(h.ctx)
	queued := make(chan error, 1)

	go func() {
		_, err := h.svc.SendText(ctx, &whatsmeowv1.SendTextRequest{SessionId: "s1", Chat: "111@lid", ClientMessageId: id(2), Text: "hello"})
		queued <- err
	}()

	h.wait(h.queued, "second queued")
	cancel()

	select {
	case err := <-queued:
		if !errors.Is(err, context.Canceled) {
			t.Fatalf("the cancelled queued send = %v; want its context's end", err)
		}
	case <-h.ctx.Done():
		t.Fatal("the cancelled queued send did not return")
	}

	close(release)
	h.outcome(first)

	if exists, _, _, _ := h.record(2); exists {
		t.Fatal("the cancelled queued send left a record")
	}

	if _, err := h.sendText(h.ctx, 2, "hi"); err != nil {
		t.Fatalf("the repeat: %v", err)
	}

	if got := h.stub.ids(); len(got) != 2 || got[1] != id(2) {
		t.Fatalf("whatsmeow got %v, want 1, then the repeat of 2", got)
	}
}

// The pacing holds across sends made one after another, the queue idle between them, not only across queued ones.
func TestSendsOneAfterAnotherArePacedToo(t *testing.T) {
	t.Parallel()

	h := start(t)
	h.svc.burst = 1
	c := h.fakeClock()

	if _, err := h.sendText(h.ctx, 0, "hi"); err != nil {
		t.Fatalf("SendText 0: %v", err)
	}

	second := h.goSend(h.ctx, 1)
	w := h.nextWait(c, second)

	if want := h.svc.pace + h.svc.pace/2 - time.Nanosecond; w.d != want {
		t.Fatalf("the second waited %v, want the interval and the jitter, %v", w.d, want)
	}

	c.pass(w)

	if err := h.done(second); err != nil {
		t.Fatalf("SendText 1: %v", err)
	}

	if sent := h.stub.sent(); len(sent) != 2 {
		t.Fatalf("whatsmeow got %v, want both", sent)
	}
}

// A session's queue holds its pacing, so it stays while that pacing still holds back a send; once it ran out, the
// queue is the same as a new one and goes, a deleted session's too, so the queues do not grow with every session.
func TestAnIdleQueueGoesOnceItsPacingRanOut(t *testing.T) {
	t.Parallel()

	h := start(t)
	c := h.fakeClock()

	if _, err := h.sendText(h.ctx, 1, "hi"); err != nil {
		t.Fatalf("SendText: %v", err)
	}

	// s1 is deleted; s3 sends, while s1's pacing still holds, and once it ran out.
	h.sessions.leave(fmt.Errorf("session %q: %w", "s1", server.ErrSessionNotFound))

	for n, want := range []bool{true, false} {
		if _, err := h.client.SendText(h.ctx, &whatsmeowv1.SendTextRequest{SessionId: "s3", Chat: "111@lid", ClientMessageId: id(2 + n), Text: "hi"}); err != nil {
			t.Fatalf("SendText on s3: %v", err)
		}

		h.svc.mu.Lock()
		_, kept := h.svc.queues["s1"]
		h.svc.mu.Unlock()

		if kept != want {
			t.Fatalf("the deleted session's idle queue kept = %v, want %v", kept, want)
		}

		c.advance(h.svc.pace)
	}
}

func TestASessionThatIsNotOnlineFailsAtOnceAndRecordsNothing(t *testing.T) {
	t.Parallel()

	h := start(t)
	h.sessions.leave(fmt.Errorf("session %q: %w", "s1", server.ErrNotConnected))

	_, err := h.sendText(h.ctx, 1, "hi")
	wantReason(t, err, codes.Unavailable, "NOT_CONNECTED", nil)

	_, err = h.client.SendText(h.ctx, &whatsmeowv1.SendTextRequest{SessionId: "s2", Chat: "111@lid", ClientMessageId: id(1), Text: "hi"})
	wantReason(t, err, codes.NotFound, "SESSION_NOT_FOUND", nil)

	if exists, _, _, _ := h.record(1); exists || len(h.stub.sent()) != 0 {
		t.Fatal("a send that failed fast left a record or reached whatsmeow")
	}
}

func TestARepeatOfASentMessageIsAnsweredWhileOffline(t *testing.T) {
	t.Parallel()

	h := start(t)

	first, err := h.sendText(h.ctx, 1, "hi")
	if err != nil {
		t.Fatalf("SendText: %v", err)
	}

	h.sessions.leave(fmt.Errorf("session %q: %w", "s1", server.ErrNotConnected))

	if again, err := h.sendText(h.ctx, 1, "hi"); err != nil || !proto.Equal(again, first) {
		t.Fatalf("the repeat while offline = %v, %v; want %v", again, err, first)
	}
}

func TestAMalformedSendIsInvalidAndSendsNothing(t *testing.T) {
	t.Parallel()

	h := start(t)
	text := func(chat, clientID, body string) error {
		_, err := h.client.SendText(h.ctx, &whatsmeowv1.SendTextRequest{SessionId: "s1", Chat: chat, ClientMessageId: clientID, Text: body})

		return err //nolint:wrapcheck // the test reads the status
	}
	raw := func(message *waE2E.Message) error {
		_, err := h.client.SendMessage(h.ctx, &whatsmeowv1.SendMessageRequest{SessionId: "s1", Chat: "111@lid", ClientMessageId: id(1), Message: message})

		return err //nolint:wrapcheck // the test reads the status
	}

	cases := []struct {
		err   error
		field string
	}{
		{text("111@lid", id(1), ""), "text"},
		{text("", id(1), "hi"), "chat"},
		{text("111:2@lid", id(1), "hi"), "chat"},
		{text("1@newsletter", id(1), "hi"), "chat"},
		{text("111@lid", "", "hi"), "client_message_id"},
		{raw(nil), "message"},
		// VideoEndCard's four fields are proto2 required; whatsmeow's proto.Marshal would refuse it (send.go:1261).
		{raw(&waE2E.Message{ExtendedTextMessage: &waE2E.ExtendedTextMessage{Text: proto.String("hi"), EndCardTiles: []*waE2E.VideoEndCard{{}}}}), "message"},
	}

	for _, c := range cases {
		wantReason(t, c.err, codes.InvalidArgument, "INVALID_ARGUMENT", map[string]string{"field": c.field})
	}

	_, err := h.client.SendText(h.ctx, &whatsmeowv1.SendTextRequest{SessionId: "not an id!", Chat: "111@lid", ClientMessageId: id(1), Text: "hi"})
	wantReason(t, err, codes.InvalidArgument, "INVALID_ARGUMENT", map[string]string{"field": "session_id"})

	if exists, _, _, _ := h.record(1); exists || len(h.stub.sent()) != 0 {
		t.Fatal("a malformed send left a record or reached whatsmeow")
	}
}

func TestMarkReadSendsOneReadReceipt(t *testing.T) {
	t.Parallel()

	h := start(t)
	before := time.Now()

	if _, err := h.client.MarkRead(h.ctx, &whatsmeowv1.MarkReadRequest{
		SessionId: "s1", Chat: "120363000000000000@g.us", Sender: "111@lid", MessageIds: []string{"M1", "M2"},
	}); err != nil {
		t.Fatalf("MarkRead: %v", err)
	}

	h.stub.mu.Lock()
	reads := h.stub.reads
	h.stub.mu.Unlock()

	if len(reads) != 1 || fmt.Sprint(reads[0].ids) != "[M1 M2]" || reads[0].chat != jid("120363000000000000@g.us") ||
		reads[0].sender != jid("111@lid") || reads[0].at.Before(before) || reads[0].at.After(time.Now()) {
		t.Fatalf("whatsmeow got %v, want one read of M1 and M2 by 111@lid in the group, timestamped now", reads)
	}

	// A direct chat needs no sender.
	if _, err := h.client.MarkRead(h.ctx, &whatsmeowv1.MarkReadRequest{SessionId: "s1", Chat: "111@lid", MessageIds: []string{"M3"}}); err != nil {
		t.Fatalf("MarkRead in a direct chat: %v", err)
	}
}

func TestMarkReadIsInvalidWithoutMessagesOrAGroupSenderAndFailsWhenNotConnected(t *testing.T) {
	t.Parallel()

	h := start(t)

	for _, c := range []struct {
		req   *whatsmeowv1.MarkReadRequest
		field string
	}{
		{&whatsmeowv1.MarkReadRequest{SessionId: "s1", Chat: "111@lid"}, "message_ids"},
		{&whatsmeowv1.MarkReadRequest{SessionId: "s1", Chat: "120363000000000000@g.us", MessageIds: []string{"M1"}}, "sender"},
		{&whatsmeowv1.MarkReadRequest{SessionId: "s1", Chat: "", MessageIds: []string{"M1"}}, "chat"},
		{&whatsmeowv1.MarkReadRequest{SessionId: "s1", Chat: "111@lid", Sender: "nobody@", MessageIds: []string{"M1"}}, "sender"},
	} {
		_, err := h.client.MarkRead(h.ctx, c.req)
		wantReason(t, err, codes.InvalidArgument, "INVALID_ARGUMENT", map[string]string{"field": c.field})
	}

	// whatsmeow's sendNode without a socket (client.go:983-985).
	h.stub.readErr = whatsmeow.ErrNotConnected

	_, err := h.client.MarkRead(h.ctx, &whatsmeowv1.MarkReadRequest{SessionId: "s1", Chat: "111@lid", MessageIds: []string{"M1"}})
	wantReason(t, err, codes.Unavailable, "NOT_CONNECTED", nil)

	h.sessions.leave(fmt.Errorf("session %q: %w", "s1", server.ErrLoggedOut))

	_, err = h.client.MarkRead(h.ctx, &whatsmeowv1.MarkReadRequest{SessionId: "s1", Chat: "111@lid", MessageIds: []string{"M1"}})
	wantReason(t, err, codes.FailedPrecondition, "LOGGED_OUT", nil)
}
