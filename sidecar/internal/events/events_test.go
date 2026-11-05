package events

import (
	"context"
	"database/sql"
	"errors"
	"fmt"
	"net"
	"path/filepath"
	"slices"
	"strings"
	"testing"
	"time"

	"github.com/rs/zerolog"
	"go.mau.fi/whatsmeow/proto/waCommon"
	"go.mau.fi/whatsmeow/proto/waE2E"
	"go.mau.fi/whatsmeow/store"
	"go.mau.fi/whatsmeow/types"
	waevents "go.mau.fi/whatsmeow/types/events"
	"google.golang.org/genproto/googleapis/rpc/errdetails"
	"google.golang.org/grpc"
	"google.golang.org/grpc/codes"
	"google.golang.org/grpc/credentials/insecure"
	"google.golang.org/grpc/metadata"
	"google.golang.org/grpc/status"
	"google.golang.org/grpc/test/bufconn"
	"google.golang.org/protobuf/encoding/protowire"
	"google.golang.org/protobuf/proto"
	"google.golang.org/protobuf/types/known/timestamppb"

	whatsmeowv1 "github.com/tcortega/Whatsmeow.NET/sidecar/gen/whatsmeow/v1"
	sidecarlog "github.com/tcortega/Whatsmeow.NET/sidecar/internal/log"
	"github.com/tcortega/Whatsmeow.NET/sidecar/internal/outbox"
	"github.com/tcortega/Whatsmeow.NET/sidecar/internal/server"
	sidecarstore "github.com/tcortega/Whatsmeow.NET/sidecar/internal/store"
)

const token = "00112233445566778899aabbccddeeff00112233445566778899aabbccddeeff"

// harness is the slice on a real store, served by the real server over an in-memory listener.
type harness struct {
	// returned gets what each StreamEvents handler returned, as it returns.
	returned chan error
	lis      *bufconn.Listener
	t        *testing.T
	path     string
	db       *sidecarstore.Store
	box      *outbox.Outbox
	svc      *Service
	client   whatsmeowv1.EventServiceClient
	ctx      context.Context //nolint:containedctx // the test's call context
	stop     func()
}

// observed is the slice with each StreamEvents return reported, so a test sees a handler end.
type observed struct {
	*Service

	returned chan error
}

func (o observed) StreamEvents(req *whatsmeowv1.StreamEventsRequest, stream grpc.ServerStreamingServer[whatsmeowv1.StreamEventsResponse]) error {
	err := o.Service.StreamEvents(req, stream)
	o.returned <- err

	return err
}

func start(t *testing.T, sessions ...string) *harness {
	t.Helper()

	return startOn(t, filepath.Join(t.TempDir(), "whatsmeow-net.db"), sessions...)
}

func startOn(t *testing.T, path string, sessions ...string) *harness {
	t.Helper()

	db, err := sidecarstore.Open(context.Background(), path, zerolog.Nop())
	if err != nil {
		t.Fatalf("open the store: %v", err)
	}

	for _, id := range sessions {
		if _, err := db.DB.ExecContext(context.Background(), "INSERT INTO session (id) VALUES (?)", id); err != nil {
			t.Fatalf("create session %s: %v", id, err)
		}
	}

	h := &harness{t: t, path: path, db: db, box: outbox.New(db.DB, db.Writer)}
	h.svc = New(h.box, zerolog.Nop())

	srv := server.New(token, zerolog.Nop(), sidecarlog.NewRedactor())
	h.returned = make(chan error, 16)
	whatsmeowv1.RegisterEventServiceServer(srv, observed{Service: h.svc, returned: h.returned})

	lis := bufconn.Listen(1 << 20)
	h.lis = lis
	served := make(chan error, 1)

	go func() { served <- srv.Serve(lis) }()

	conn, err := grpc.NewClient("passthrough:///bufnet",
		grpc.WithContextDialer(func(ctx context.Context, _ string) (net.Conn, error) { return lis.DialContext(ctx) }),
		grpc.WithTransportCredentials(insecure.NewCredentials()))
	if err != nil {
		t.Fatalf("dial: %v", err)
	}

	stopped := false
	h.stop = func() {
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

		if err := db.Close(); err != nil {
			t.Errorf("close the store: %v", err)
		}
	}
	t.Cleanup(h.stop)

	h.bound()
	h.client = whatsmeowv1.NewEventServiceClient(conn)

	return h
}

// bound gives the calls a new bound for a broken slice, never a wait: every wait below ends on a condition. A test
// whose setup is slow takes one after it, so the bound covers what it checks.
func (h *harness) bound() {
	ctx, cancel := context.WithTimeout(context.Background(), 30*time.Second)
	h.t.Cleanup(cancel)

	h.ctx = metadata.AppendToOutgoingContext(ctx, "authorization", "Bearer "+token)
}

func jid(s string) types.JID {
	j, err := types.ParseJID(s)
	if err != nil {
		panic(err)
	}

	return j
}

// messageEvent is a decrypted message as whatsmeow dispatches it.
func messageEvent(id string) *waevents.Message {
	return &waevents.Message{
		Info: types.MessageInfo{
			MessageSource: types.MessageSource{Chat: jid("111@s.whatsapp.net"), Sender: jid("111@s.whatsapp.net")},
			ID:            id,
			Timestamp:     time.Unix(1_700_000_000, 0),
		},
		Message: &waE2E.Message{Conversation: proto.String("hello " + id)},
	}
}

// deliver runs the session's handler on evt as whatsmeow's dispatchEvent does (client.go:999-1013): a panic is
// recovered and counts as success, so the stanza is answered.
func (h *harness) deliver(session string, evt any) (answered bool) {
	defer func() {
		if recover() != nil {
			answered = true
		}
	}()

	return h.svc.Handler(session, nil)(evt)
}

func (h *harness) mustDeliver(session string, evt any) {
	h.t.Helper()

	if !h.deliver(session, evt) {
		h.t.Fatalf("the handler withheld the answer to %v", evt)
	}
}

func (h *harness) open(session, consumer string, after uint64) grpc.ServerStreamingClient[whatsmeowv1.StreamEventsResponse] {
	h.t.Helper()

	stream, err := h.client.StreamEvents(h.ctx, &whatsmeowv1.StreamEventsRequest{SessionId: session, Consumer: consumer, AfterSeq: after})
	if err != nil {
		h.t.Fatalf("StreamEvents: %v", err)
	}

	return stream
}

// ids reads n events and returns their seqs and message ids.
func ids(t *testing.T, stream grpc.ServerStreamingClient[whatsmeowv1.StreamEventsResponse], n int) ([]uint64, []string) {
	t.Helper()

	var (
		seqs []uint64
		got  []string
	)

	for range n {
		event, err := stream.Recv()
		if err != nil {
			t.Fatalf("Recv: %v", err)
		}

		seqs = append(seqs, event.GetSeq())
		got = append(got, event.GetMessage().GetInfo().GetId())
	}

	return seqs, got
}

func (h *harness) ack(session, consumer string, upTo uint64) error {
	_, err := h.client.AckEvents(h.ctx, &whatsmeowv1.AckEventsRequest{SessionId: session, Consumer: consumer, UpToSeq: upTo})

	return err //nolint:wrapcheck // the test reads the status
}

func wantReason(t *testing.T, err error, code codes.Code, reason string) {
	t.Helper()

	st, ok := status.FromError(err)
	if !ok || st.Code() != code || len(st.Details()) != 1 {
		t.Fatalf("error %v, want %v with one ErrorInfo", err, code)
	}

	if info, _ := st.Details()[0].(*errdetails.ErrorInfo); info.GetReason() != reason {
		t.Fatalf("reason %q, want %q", info.GetReason(), reason)
	}
}

func equal(t *testing.T, got, want []string) {
	t.Helper()

	if !slices.Equal(got, want) {
		t.Fatalf("got %v, want %v", got, want)
	}
}

func TestEveryWhatsmeowEventMapsToOneOutboxEventOrNone(t *testing.T) {
	t.Parallel()

	at := time.Unix(1_700_000_000, 0)
	source := types.MessageSource{
		Chat: jid("120363000000000000@g.us"), Sender: jid("111:2@lid"), IsFromMe: true,
		SenderAlt: jid("5511999999999@s.whatsapp.net"), RecipientAlt: jid("222@lid"),
	}
	info := &whatsmeowv1.MessageInfo{
		Source: &whatsmeowv1.MessageSource{
			Chat: "120363000000000000@g.us", Sender: "111:2@lid", SenderAlt: "5511999999999@s.whatsapp.net",
			RecipientAlt: "222@lid", IsFromMe: true,
		},
		Id: "M1", PushName: "Ana", MessageTime: timestamppb.New(at),
	}
	content := &waE2E.Message{Conversation: proto.String("hi")}

	cases := []struct {
		evt  any
		key  string
		want *whatsmeowv1.StreamEventsResponse
	}{
		{
			&waevents.Message{Info: types.MessageInfo{MessageSource: source, ID: "M1", PushName: "Ana", Timestamp: at}, Message: content},
			"m\x00120363000000000000@g.us\x00111:2@lid\x00M1",
			&whatsmeowv1.StreamEventsResponse{Payload: &whatsmeowv1.StreamEventsResponse_Message{Message: &whatsmeowv1.MessageEvent{Info: info, Message: content}}},
		},
		{
			&waevents.UndecryptableMessage{Info: types.MessageInfo{MessageSource: source, ID: "M1", PushName: "Ana", Timestamp: at}, DecryptFailMode: waevents.DecryptFailHide},
			"u\x00120363000000000000@g.us\x00111:2@lid\x00M1",
			&whatsmeowv1.StreamEventsResponse{Payload: &whatsmeowv1.StreamEventsResponse_UndecryptableMessage{
				UndecryptableMessage: &whatsmeowv1.UndecryptableMessageEvent{Info: info, HidePlaceholder: true},
			}},
		},
		{
			&waevents.UndecryptableMessage{Info: types.MessageInfo{MessageSource: source, ID: "M1", PushName: "Ana", Timestamp: at}},
			"u\x00120363000000000000@g.us\x00111:2@lid\x00M1",
			&whatsmeowv1.StreamEventsResponse{Payload: &whatsmeowv1.StreamEventsResponse_UndecryptableMessage{
				UndecryptableMessage: &whatsmeowv1.UndecryptableMessageEvent{Info: info},
			}},
		},
		// Only a sender key: whatsmeow stored its effect, and the content comes as its own Message with the same info.
		{senderKeyOnly("M1"), "", nil},
		// A message the client cannot read: whatsmeow's ParseWebMessage, for a placeholder resend, takes the id
		// unchecked and accepts an empty chat (client.go:1027-1039; message.go:874-880) (#122).
		{&waevents.Message{Info: types.MessageInfo{MessageSource: source, Timestamp: at}, Message: content}, "", nil},
		{&waevents.Message{Info: types.MessageInfo{MessageSource: types.MessageSource{Sender: source.Sender}, ID: "M1", Timestamp: at}, Message: content}, "", nil},
		{&waevents.Message{Info: types.MessageInfo{MessageSource: types.MessageSource{Chat: source.Chat, Sender: types.JID{User: "a:b:c", Server: types.HiddenUserServer}}, ID: "M1"}, Message: content}, "", nil},
		{&waevents.UndecryptableMessage{Info: types.MessageInfo{MessageSource: source, Timestamp: at}}, "", nil},
		{&waevents.UndecryptableMessage{Info: types.MessageInfo{MessageSource: types.MessageSource{Chat: source.Chat}, ID: "M1"}}, "", nil},
		// The session slice records connection events as transitions; receipts come with #9.
		{&waevents.Connected{}, "", nil},
		{&waevents.Disconnected{}, "", nil},
		{&waevents.LoggedOut{}, "", nil},
		{&waevents.Receipt{}, "", nil},
		{&waevents.Presence{}, "", nil},
	}

	for _, c := range cases {
		key, got, ok := toEvent(c.evt, nil, nil)
		if ok != (c.want != nil) || key != c.key || !proto.Equal(got, c.want) {
			t.Errorf("%T: key %q, %v, %v; want %q, %v", c.evt, key, got, ok, c.key, c.want)
		}
	}
}

func TestTheHandlerHasRecordedTheEventWhenItReturnsTrue(t *testing.T) {
	t.Parallel()

	h := start(t, "s1")
	h.mustDeliver("s1", messageEvent("M1"))

	events, err := h.box.Read(h.ctx, "s1", 0, 10)
	if err != nil || len(events) != 1 || events[0].GetMessage().GetInfo().GetId() != "M1" {
		t.Fatalf("the outbox has %v, %v; want M1", events, err)
	}
}

func TestTheHandlerWithholdsTheAnswerWhenTheWriteFails(t *testing.T) {
	t.Parallel()

	h := start(t, "s1")
	if _, err := h.db.DB.ExecContext(h.ctx, "DROP TABLE outbox"); err != nil {
		t.Fatalf("drop the outbox: %v", err)
	}

	if h.deliver("s1", messageEvent("M1")) {
		t.Fatal("the handler answered a message it could not record")
	}
}

func TestTheHandlerWithholdsTheAnswerWhenItPanics(t *testing.T) {
	t.Parallel()

	h := start(t, "s1")

	// Mapping a nil message panics; whatsmeow would recover that and answer the stanza.
	if h.deliver("s1", (*waevents.Message)(nil)) {
		t.Fatal("the handler answered after a panic")
	}
}

func TestTheHandlerAnswersWithNoConsumerAndNeverWaitsForOne(t *testing.T) {
	t.Parallel()

	h := start(t, "s1")
	answered := make(chan bool, 1)

	go func() { answered <- h.deliver("s1", messageEvent("M1")) }()

	select {
	case ok := <-answered:
		if !ok {
			t.Fatal("the handler withheld the answer because no consumer is connected")
		}
	case <-h.ctx.Done():
		t.Fatal("the handler waited for a consumer")
	}
}

func TestTheHandlerAnswersAnEventItDoesNotRecord(t *testing.T) {
	t.Parallel()

	h := start(t, "s1")
	if !h.deliver("s1", &waevents.Presence{}) {
		t.Fatal("the handler withheld the answer to an event it does not record")
	}
}

func TestARedispatchedMessageKeepsItsSeqAndIsStreamedOnce(t *testing.T) {
	t.Parallel()

	h := start(t, "s1")
	h.mustDeliver("s1", messageEvent("M1"))
	h.mustDeliver("s1", messageEvent("M1"))
	h.mustDeliver("s1", messageEvent("M2"))

	seqs, got := ids(t, h.open("s1", "c1", 0), 2)
	equal(t, got, []string{"M1", "M2"})

	// The repeat kept seq 1. SQLite still spent a seq on the insert that did nothing [observed], so M2 is not 2;
	// seqs only have to increase.
	if seqs[0] != 1 || seqs[1] <= seqs[0] {
		t.Fatalf("seqs %v, want M1 at 1 and M2 after it", seqs)
	}
}

func TestStreamSendsTheBacklogThenLiveEventsInSeqOrder(t *testing.T) {
	t.Parallel()

	h := start(t, "s1", "s2")
	h.mustDeliver("s1", messageEvent("M1"))
	h.mustDeliver("s2", messageEvent("X1"))
	h.mustDeliver("s1", messageEvent("M2"))

	stream := h.open("s1", "c1", 0)
	_, backlog := ids(t, stream, 2)
	equal(t, backlog, []string{"M1", "M2"})

	h.mustDeliver("s1", messageEvent("M3"))

	seqs, live := ids(t, stream, 1)
	equal(t, live, []string{"M3"})

	if seqs[0] != 4 {
		t.Fatalf("seq %d, want 4: sessions share the sequence", seqs[0])
	}
}

func TestStreamStartsAfterTheCursorOrAfterSeqWhicheverIsHigher(t *testing.T) {
	t.Parallel()

	h := start(t, "s1")
	for _, id := range []string{"M1", "M2", "M3", "M4"} {
		h.mustDeliver("s1", messageEvent(id))
	}

	// Another consumer keeps every event in the outbox, so only the cursor decides where c1 starts.
	if err := h.ack("s1", "c2", 0); err != nil {
		t.Fatalf("ack: %v", err)
	}

	if err := h.ack("s1", "c1", 1); err != nil {
		t.Fatalf("ack: %v", err)
	}

	_, got := ids(t, h.open("s1", "c1", 0), 1)
	equal(t, got, []string{"M2"})

	// Above the cursor, after_seq skips events without acking them: a new stream from 0 starts at the cursor again.
	_, got = ids(t, h.open("s1", "c1", 3), 1)
	equal(t, got, []string{"M4"})

	_, got = ids(t, h.open("s1", "c1", 0), 1)
	equal(t, got, []string{"M2"})
}

func TestStreamWithAfterSeqAboveTheHighestIsInvalidArgument(t *testing.T) {
	t.Parallel()

	h := start(t, "s1")
	h.mustDeliver("s1", messageEvent("M1"))

	_, err := h.open("s1", "c1", 2).Recv()
	wantReason(t, err, codes.InvalidArgument, "INVALID_ARGUMENT")
}

func TestStreamAndAckRejectAnUnknownSessionAndABadConsumer(t *testing.T) {
	t.Parallel()

	h := start(t, "s1")

	_, err := h.open("nope", "c1", 0).Recv()
	wantReason(t, err, codes.NotFound, "SESSION_NOT_FOUND")

	_, err = h.open("s1", "bad consumer", 0).Recv()
	wantReason(t, err, codes.InvalidArgument, "INVALID_ARGUMENT")

	wantReason(t, h.ack("nope", "c1", 0), codes.NotFound, "SESSION_NOT_FOUND")
	wantReason(t, h.ack("s1", "", 0), codes.InvalidArgument, "INVALID_ARGUMENT")
}

func TestAckNeverMovesTheCursorBackAndIsBoundByTheRecordedSeq(t *testing.T) {
	t.Parallel()

	h := start(t, "s1")
	for _, id := range []string{"M1", "M2", "M3"} {
		h.mustDeliver("s1", messageEvent(id))
	}

	// Another consumer keeps every event in the outbox, so only the cursor decides where c1 starts.
	if err := h.ack("s1", "c2", 0); err != nil {
		t.Fatalf("ack: %v", err)
	}

	if err := h.ack("s1", "c1", 2); err != nil {
		t.Fatalf("ack: %v", err)
	}

	if err := h.ack("s1", "c1", 1); err != nil {
		t.Fatalf("ack below the cursor: %v", err)
	}

	wantReason(t, h.ack("s1", "c1", 4), codes.InvalidArgument, "INVALID_ARGUMENT")

	_, got := ids(t, h.open("s1", "c1", 0), 1)
	equal(t, got, []string{"M3"})
}

func TestAnAckAfterARestartSucceedsAfterTheRowsAreGone(t *testing.T) {
	t.Parallel()

	path := filepath.Join(t.TempDir(), "whatsmeow-net.db")
	h := startOn(t, path, "s1")
	h.mustDeliver("s1", messageEvent("M1"))
	h.mustDeliver("s1", messageEvent("M2"))

	if err := h.ack("s1", "c1", 2); err != nil {
		t.Fatalf("ack: %v", err)
	}

	h.stop()

	// The restarted sidecar streamed nothing yet, and the rows were deleted; the bound is the recorded seq.
	again := startOn(t, path)
	if err := again.ack("s1", "c1", 2); err != nil {
		t.Fatalf("ack after the restart: %v", err)
	}
}

func TestANewerStreamReplacesTheOpenOne(t *testing.T) {
	t.Parallel()

	h := start(t, "s1")
	h.mustDeliver("s1", messageEvent("M1"))

	first := h.open("s1", "c1", 0)
	ids(t, first, 1)

	second := h.open("s1", "c1", 0)
	ids(t, second, 1)

	_, err := first.Recv()
	wantReason(t, err, codes.Aborted, "CONSUMER_REPLACED")

	// Another consumer is not replaced.
	other := h.open("s1", "c2", 0)
	ids(t, other, 1)
	h.mustDeliver("s1", messageEvent("M2"))

	_, got := ids(t, second, 1)
	equal(t, got, []string{"M2"})

	_, got = ids(t, other, 1)
	equal(t, got, []string{"M2"})
}

func TestTheReplacedStreamsCleanupLeavesTheNewOneRegistered(t *testing.T) {
	t.Parallel()

	h := start(t, "s1")
	h.mustDeliver("s1", messageEvent("M1"))

	first := h.open("s1", "c1", 0)
	ids(t, first, 1)

	second := h.open("s1", "c1", 0)
	ids(t, second, 1)

	// The first stream ended, so its cleanup ran.
	_, err := first.Recv()
	wantReason(t, err, codes.Aborted, "CONSUMER_REPLACED")

	// The second one is still the consumer's: a third replaces it.
	third := h.open("s1", "c1", 0)
	ids(t, third, 1)

	_, err = second.Recv()
	wantReason(t, err, codes.Aborted, "CONSUMER_REPLACED")
}

func TestStreamEndsWithSessionNotFoundWhenTheSessionIsDeleted(t *testing.T) {
	t.Parallel()

	h := start(t, "s1")
	h.mustDeliver("s1", messageEvent("M1"))

	stream := h.open("s1", "c1", 0)
	ids(t, stream, 1)

	// As the session slice's DeleteSession does.
	if _, err := h.db.DB.ExecContext(h.ctx, "DELETE FROM session WHERE id = 's1'"); err != nil {
		t.Fatalf("delete the session: %v", err)
	}

	h.box.Delete("s1")

	_, err := stream.Recv()
	wantReason(t, err, codes.NotFound, "SESSION_NOT_FOUND")
}

// senderKeyOnly is the part of a group stanza that only carries the sender key (the pkmsg or msg next to the skmsg,
// send.go:795-831): whatsmeow dispatches it as a Message with the same info as the content (message.go:1172-1178).
func senderKeyOnly(id string) *waevents.Message {
	evt := messageEvent(id)
	evt.Message = &waE2E.Message{
		SenderKeyDistributionMessage: &waE2E.SenderKeyDistributionMessage{GroupID: proto.String("120363000000000000@g.us"), AxolotlSenderKeyDistributionMessage: []byte{1}},
		MessageContextInfo:           &waE2E.MessageContextInfo{DeviceListMetadataVersion: proto.Int32(2)},
	}

	return evt
}

func TestASenderKeyOnlyPartIsNotRecordedSoTheContentIs(t *testing.T) {
	t.Parallel()

	h := start(t, "s1")
	h.mustDeliver("s1", senderKeyOnly("M1"))
	h.mustDeliver("s1", messageEvent("M1"))

	events, err := h.box.Read(h.ctx, "s1", 0, 10)
	if err != nil || len(events) != 1 || !proto.Equal(events[0].GetMessage().GetMessage(), messageEvent("M1").Message) {
		t.Fatalf("the outbox has %v, %v; want only M1's content", events, err)
	}
}

// stuckStream is a stream whose client stopped reading: Send blocks as grpc-go's does on flow control, woken only
// by the stream's own end, never by a context derived from it (http2_server.go:624, flowcontrol.go:54-66).
type stuckStream struct {
	grpc.ServerStream

	ctx   context.Context //nolint:containedctx // the stream's context, as grpc.ServerStream has
	sends chan struct{}
	end   chan struct{}
}

func (s *stuckStream) Context() context.Context { return s.ctx }

func (s *stuckStream) Send(*whatsmeowv1.StreamEventsResponse) error {
	s.sends <- struct{}{}
	<-s.end

	return errors.New("the stream ended")
}

func TestAHandlerWriteStuckBehindTheGatePastItsDeadlineGivesUpAndWithholds(t *testing.T) {
	t.Parallel()

	h := start(t, "s1")
	h.svc.recordTimeout = 100 * time.Millisecond

	// Another write holds the gate, as one stuck behind whatsmeow's own writes would.
	holding, release := make(chan struct{}), make(chan struct{})
	held := make(chan error, 1)

	go func() {
		held <- h.db.Writer.Tx(h.ctx, func(context.Context, *sql.Tx) error {
			close(holding)
			<-release

			return nil
		})
	}()

	<-holding

	// The holder lets go once the handler returned, or at the test's bound for a handler that waits for it.
	returned := make(chan struct{})

	go func() {
		select {
		case <-returned:
		case <-h.ctx.Done():
		}

		close(release)
	}()

	answered := h.deliver("s1", messageEvent("M1"))
	close(returned)

	if answered {
		t.Fatal("the handler waited for the gate past its deadline and answered")
	}

	if err := <-held; err != nil {
		t.Fatalf("the holding write: %v", err)
	}

	if events, err := h.box.Read(h.ctx, "s1", 0, 10); err != nil || len(events) != 0 {
		t.Fatalf("the outbox has %v, %v; want nothing: the write gave up", events, err)
	}

	// Its redelivery is recorded once the gate is free, with the real deadline: only the stuck write was meant to run
	// out of its 100 ms, not a commit on a slow disk.
	h.svc.recordTimeout = sidecarstore.BusyTimeout

	h.mustDeliver("s1", messageEvent("M1"))
}

func TestAReplacedStreamStuckInSendEnds(t *testing.T) {
	t.Parallel()

	h := start(t, "s1")
	h.mustDeliver("s1", messageEvent("M1"))

	stuck := &stuckStream{ctx: h.ctx, sends: make(chan struct{}, 1), end: make(chan struct{})}
	t.Cleanup(func() { close(stuck.end) })

	ended := make(chan error, 1)

	go func() {
		ended <- h.svc.StreamEvents(&whatsmeowv1.StreamEventsRequest{SessionId: "s1", Consumer: "c1"}, stuck)
	}()

	<-stuck.sends

	second := h.open("s1", "c1", 0)
	ids(t, second, 1)

	select {
	case err := <-ended:
		if !errors.Is(err, server.ErrConsumerReplaced) {
			t.Fatalf("the replaced stream ended with %v, want %v", err, server.ErrConsumerReplaced)
		}
	case <-h.ctx.Done():
		t.Fatal("the replaced stream stayed in Send")
	}
}

func TestASenderKeyPartWithMoreThanTheKeyStillLetsTheContentThrough(t *testing.T) {
	t.Parallel()

	h := start(t, "s1")

	// A field this whatsmeow does not know makes the part more than a sender key, so it is recorded.
	part := senderKeyOnly("M1")
	part.Message.ProtoReflect().SetUnknown(protowire.AppendVarint(protowire.AppendTag(nil, 9999, protowire.VarintType), 1))
	h.mustDeliver("s1", part)
	h.mustDeliver("s1", messageEvent("M1"))
	h.mustDeliver("s1", messageEvent("M1"))

	events, err := h.box.Read(h.ctx, "s1", 0, 10)
	if err != nil || len(events) != 2 || !proto.Equal(events[1].GetMessage().GetMessage(), messageEvent("M1").Message) {
		t.Fatalf("the outbox has %v, %v; want the sender-key part, then M1's content once", events, err)
	}
}

func TestAReplacedStreamWhoseClientStoppedReadingEnds(t *testing.T) {
	t.Parallel()

	h := start(t, "s1")

	// 512 KiB, far more than grpc-go's 64 KiB stream window and its 64 KiB write quota: the first stream's handler
	// blocks in Send on flow control. In few events, because each is a commit: 64 took up to 24.8 s on a loaded
	// Windows disk, inside the bound of what this test checks (#89). The bound starts after them.
	big := strings.Repeat("x", 64<<10)
	for i := range 8 {
		evt := messageEvent(fmt.Sprintf("M%d", i))
		evt.Message.Conversation = proto.String(big)
		h.mustDeliver("s1", evt)
	}

	h.bound()

	// A fixed 64 KiB window: without one, grpc-go's BDP estimation grows the window of a stream nobody reads.
	slow, err := grpc.NewClient("passthrough:///bufnet",
		grpc.WithContextDialer(func(ctx context.Context, _ string) (net.Conn, error) { return h.lis.DialContext(ctx) }),
		grpc.WithTransportCredentials(insecure.NewCredentials()),
		grpc.WithInitialWindowSize(64<<10), grpc.WithInitialConnWindowSize(64<<10))
	if err != nil {
		t.Fatalf("dial: %v", err)
	}
	defer func() { _ = slow.Close() }()

	unread, err := whatsmeowv1.NewEventServiceClient(slow).StreamEvents(h.ctx, &whatsmeowv1.StreamEventsRequest{SessionId: "s1", Consumer: "c1"})
	if err != nil {
		t.Fatalf("StreamEvents: %v", err)
	}

	// The headers come with the first event: the handler is registered and sending, and blocks once the window is full.
	if _, err := unread.Header(); err != nil {
		t.Fatalf("the first stream's headers: %v", err)
	}

	// Not read: the second one replaces it, and the first one's handler must return though its client never reads.
	second := h.open("s1", "c1", 0)
	ids(t, second, 1)

	select {
	case err := <-h.returned:
		if !errors.Is(err, server.ErrConsumerReplaced) {
			t.Fatalf("the first handler returned %v, want %v", err, server.ErrConsumerReplaced)
		}
	case <-h.ctx.Done():
		t.Fatal("the replaced handler stayed in Send")
	}
}

func TestAViewOnceMessageAndAnUnavailableOneSayWhatTheyAre(t *testing.T) {
	t.Parallel()

	source := types.MessageSource{Chat: jid("111@lid"), Sender: jid("111@lid")}
	info := &whatsmeowv1.MessageInfo{Source: &whatsmeowv1.MessageSource{Chat: "111@lid", Sender: "111@lid"}, Id: "M1", MessageTime: timestamppb.New(time.Time{})}
	image := &waE2E.Message{ImageMessage: &waE2E.ImageMessage{Caption: proto.String("once")}}
	viewOnce := (&waevents.Message{
		Info:       types.MessageInfo{MessageSource: source, ID: "M1"},
		RawMessage: &waE2E.Message{ViewOnceMessageV2: &waE2E.FutureProofMessage{Message: image}},
	}).UnwrapRaw()
	unavailable := func(t waevents.UnavailableType) *waevents.UndecryptableMessage {
		return &waevents.UndecryptableMessage{Info: types.MessageInfo{MessageSource: source, ID: "M1"}, IsUnavailable: true, UnavailableType: t}
	}
	undecryptable := func(event *whatsmeowv1.UndecryptableMessageEvent) *whatsmeowv1.StreamEventsResponse {
		return &whatsmeowv1.StreamEventsResponse{Payload: &whatsmeowv1.StreamEventsResponse_UndecryptableMessage{UndecryptableMessage: event}}
	}

	cases := []struct {
		evt  any
		want *whatsmeowv1.StreamEventsResponse
	}{
		{viewOnce, &whatsmeowv1.StreamEventsResponse{Payload: &whatsmeowv1.StreamEventsResponse_Message{
			Message: &whatsmeowv1.MessageEvent{Info: info, Message: image, ViewOnce: true, RawMessage: viewOnce.RawMessage},
		}}},
		{unavailable(waevents.UnavailableTypeViewOnce), undecryptable(&whatsmeowv1.UndecryptableMessageEvent{
			Info: info, Unavailable: true, UnavailableType: whatsmeowv1.UnavailableType_UNAVAILABLE_TYPE_VIEW_ONCE,
		})},
		// No type, as for a group message whose sender key never came (message.go:419), and one this list does not have.
		{unavailable(waevents.UnavailableTypeUnknown), undecryptable(&whatsmeowv1.UndecryptableMessageEvent{Info: info, Unavailable: true})},
		{unavailable("hidden"), undecryptable(&whatsmeowv1.UndecryptableMessageEvent{Info: info, Unavailable: true})},
	}

	for _, c := range cases {
		if _, got, ok := toEvent(c.evt, nil, nil); !ok || !proto.Equal(got, c.want) {
			t.Errorf("%+v: %v, %v; want %v", c.evt, got, ok, c.want)
		}
	}
}

func TestAMessagesWrappersSayWhatTheyWere_AndTheRawMessageIsKeptWhenUnwrapped(t *testing.T) {
	t.Parallel()

	own := types.MessageSource{Chat: jid("111@lid"), Sender: jid("999:4@lid"), IsFromMe: true}
	text := &waE2E.Message{ExtendedTextMessage: &waE2E.ExtendedTextMessage{Text: proto.String("hi")}}
	unwrap := func(raw *waE2E.Message) *waevents.Message {
		return (&waevents.Message{Info: types.MessageInfo{MessageSource: own, ID: "M1"}, RawMessage: raw}).UnwrapRaw()
	}
	event := func(e *whatsmeowv1.MessageEvent) *whatsmeowv1.StreamEventsResponse {
		e.Info = &whatsmeowv1.MessageInfo{Source: &whatsmeowv1.MessageSource{Chat: "111@lid", Sender: "999:4@lid", IsFromMe: true}, Id: "M1", MessageTime: timestamppb.New(time.Time{})}

		return &whatsmeowv1.StreamEventsResponse{Payload: &whatsmeowv1.StreamEventsResponse_Message{Message: e}}
	}
	ephemeral := &waE2E.Message{EphemeralMessage: &waE2E.FutureProofMessage{Message: text}}
	deviceSent := &waE2E.Message{DeviceSentMessage: &waE2E.DeviceSentMessage{DestinationJID: proto.String("222@lid"), Message: ephemeral}}
	plain := &waE2E.Message{Conversation: proto.String("hi")}
	plainDSM := &waE2E.Message{Conversation: proto.String("sent")}
	badDestination := &waE2E.Message{DeviceSentMessage: &waE2E.DeviceSentMessage{DestinationJID: proto.String("222\xff@lid"), Message: plainDSM}}

	cases := []struct {
		evt  *waevents.Message
		want *whatsmeowv1.StreamEventsResponse
	}{
		// A disappearing message (types/events/events.go:390-393).
		{unwrap(ephemeral), event(&whatsmeowv1.MessageEvent{Message: text, Ephemeral: true, RawMessage: ephemeral})},
		// Sent from the account's phone: the chat it went to is in the device-sent wrapper (events.go:378-384).
		{unwrap(deviceSent), event(&whatsmeowv1.MessageEvent{Message: text, Ephemeral: true, DeviceSentDestination: "222@lid", RawMessage: deviceSent})},
		// The destination is WhatsApp data in a proto2 string: invalid UTF-8 becomes U+FFFD (server.Text).
		{unwrap(badDestination), event(&whatsmeowv1.MessageEvent{Message: plainDSM, DeviceSentDestination: "222\uFFFD@lid", RawMessage: badDestination})},
		// Nothing to unwrap: the message is what was received, sent once.
		{unwrap(plain), event(&whatsmeowv1.MessageEvent{Message: plain})},
	}

	for _, c := range cases {
		if _, got, ok := toEvent(c.evt, nil, nil); !ok || !proto.Equal(got, c.want) {
			t.Errorf("%v: %v, %v; want %v", c.evt.RawMessage, got, ok, c.want)
		}
	}
}

// The account a target test's session is: its device JID and LID, as WhatsApp assigns them.
const (
	accountPN  = "5511900000001:3@s.whatsapp.net"
	accountLID = "900000001:3@lid"
)

func account() *store.Device {
	return &store.Device{ID: new(jid(accountPN)), LID: jid(accountLID)}
}

// A reaction's, an edit's and a revoke's target is read from its key as mautrix reads one (pkg/msgconv/wa-poll.go:97-132
// at 5dd621f), without a device, and empty when it is the account's own message however the key names the account;
// a key that names no message has no target.
func TestAnActionsTargetIsReadFromItsKey_AndTheAccountsOwnMessageHasNoSender(t *testing.T) {
	t.Parallel()

	const (
		groupChat = "120363000000000000@g.us"
		peer      = "222@lid"
	)

	key := func(fromMe bool, id, participant string) *waCommon.MessageKey {
		k := &waCommon.MessageKey{FromMe: proto.Bool(fromMe), ID: proto.String(id)}
		if participant != "" {
			k.Participant = proto.String(participant)
		}

		return k
	}
	reaction := func(k *waCommon.MessageKey) *waE2E.Message {
		return &waE2E.Message{ReactionMessage: &waE2E.ReactionMessage{Key: k, Text: proto.String("👍")}}
	}
	revoke := func(k *waCommon.MessageKey) *waE2E.Message {
		return &waE2E.Message{ProtocolMessage: &waE2E.ProtocolMessage{Type: waE2E.ProtocolMessage_REVOKE.Enum(), Key: k}}
	}
	edit := &waE2E.Message{ProtocolMessage: &waE2E.ProtocolMessage{
		Type: waE2E.ProtocolMessage_MESSAGE_EDIT.Enum(), Key: key(true, "T1", ""), EditedMessage: &waE2E.Message{Conversation: proto.String("new")},
	}}
	from := func(chat, sender string, isFromMe bool) types.MessageSource {
		return types.MessageSource{Chat: jid(chat), Sender: jid(sender), IsFromMe: isFromMe}
	}
	member, admin := from(groupChat, "333:7@lid", false), from(groupChat, "444:1@lid", false)
	other, phone := from(peer, "222:2@lid", false), from(peer, "5511900000001@s.whatsapp.net", true)
	target := func(sender string) *whatsmeowv1.MessageTarget {
		return &whatsmeowv1.MessageTarget{Id: "T1", Sender: sender}
	}

	cases := []struct {
		name    string
		source  types.MessageSource
		message *waE2E.Message
		want    *whatsmeowv1.MessageTarget
	}{
		{"a member's revoke of their own message, without the device", member, revoke(key(true, "T1", "")), target("333@lid")},
		{"an admin's revoke of a member's", admin, revoke(key(false, "T1", "333:5@lid")), target("333@lid")},
		{"a member's edit", member, edit, target("333@lid")},
		{"a legacy participant", member, reaction(key(false, "T1", "5511900000002@c.us")), target("5511900000002@s.whatsapp.net")},
		{"a group key naming the account by its LID", member, reaction(key(false, "T1", "900000001:2@lid")), target("")},
		{"a group key naming the account by its phone number", member, reaction(key(false, "T1", "5511900000001@s.whatsapp.net")), target("")},
		{"a direct chat's other side on the account's message", other, reaction(key(false, "T1", "")), target("")},
		{"a direct chat key naming the account with a device", other, reaction(key(false, "T1", accountPN)), target("")},
		{"the account's phone on its own message", phone, revoke(key(true, "T1", "")), target("")},
		{"the account's phone on the other side's message", phone, reaction(key(false, "T1", "")), target(peer)},
		{"a group key neither the actor's nor naming a participant", member, reaction(key(false, "T1", "")), nil},
		{"a participant that is not a JID", member, reaction(key(false, "T1", "1:x@s.whatsapp.net")), nil},
		{"a participant without a server", member, reaction(key(false, "T1", "333@")), nil},
		{"a group as the participant", member, reaction(key(false, "T1", groupChat)), nil},
		{"a key without an id", member, reaction(key(true, "", "")), nil},
		{"a protocol message without a type", member, &waE2E.Message{ProtocolMessage: &waE2E.ProtocolMessage{Key: key(true, "T1", "")}}, nil},
		{"a text", member, &waE2E.Message{Conversation: proto.String("hi")}, nil},
	}

	for _, c := range cases {
		e := &waevents.Message{Info: types.MessageInfo{MessageSource: c.source, ID: "M1"}, Message: c.message}
		if got := MessageEvent(e, account()).GetTarget(); !proto.Equal(got, c.want) {
			t.Errorf("%s: target %v, want %v", c.name, got, c.want)
		}
	}
}

// A reply's quote is named as an action's target is: its participant without a device, c.us read as s.whatsapp.net,
// and empty for the account's own message however the context names the account, so an empty sender always means the
// account's; a quote whose participant is missing or not a user's JID names no sender, and has none.
func TestAQuoteIsNamedAsATargetIs_AndTheAccountsOwnMessageHasNoSender(t *testing.T) {
	t.Parallel()

	text := func(stanza, participant string) *waE2E.Message {
		c := &waE2E.ContextInfo{QuotedMessage: &waE2E.Message{Conversation: proto.String("the question")}}
		if stanza != "" {
			c.StanzaID = proto.String(stanza)
		}

		if participant != "" {
			c.Participant = proto.String(participant)
		}

		return &waE2E.Message{ExtendedTextMessage: &waE2E.ExtendedTextMessage{Text: proto.String("yes"), ContextInfo: c}}
	}
	quote := func(sender string) *whatsmeowv1.MessageTarget {
		return &whatsmeowv1.MessageTarget{Id: "Q1", Sender: sender}
	}
	image := &waE2E.Message{ImageMessage: &waE2E.ImageMessage{ContextInfo: text("Q1", "333:2@lid").GetExtendedTextMessage().GetContextInfo()}}
	edit := &waE2E.Message{ProtocolMessage: &waE2E.ProtocolMessage{
		Type: waE2E.ProtocolMessage_MESSAGE_EDIT.Enum(), Key: &waCommon.MessageKey{FromMe: proto.Bool(true), ID: proto.String("T1")}, EditedMessage: text("Q1", "333@lid"),
	}}
	poll := &waE2E.Message{PollCreationMessage: &waE2E.PollCreationMessage{ContextInfo: text("Q1", "333:2@lid").GetExtendedTextMessage().GetContextInfo()}}
	pollV3 := &waE2E.Message{PollCreationMessageV3: poll.GetPollCreationMessage()}

	cases := []struct {
		name    string
		message *waE2E.Message
		want    *whatsmeowv1.MessageTarget
	}{
		{"a member's message, without the device", text("Q1", "333:7@lid"), quote("333@lid")},
		{"a member's by phone number", text("Q1", "5511900000002@s.whatsapp.net"), quote("5511900000002@s.whatsapp.net")},
		{"a legacy participant", text("Q1", "5511900000002@c.us"), quote("5511900000002@s.whatsapp.net")},
		{"the account's, by its phone number", text("Q1", "5511900000001@s.whatsapp.net"), quote("")},
		{"the account's, by its phone number with a device", text("Q1", accountPN), quote("")},
		{"the account's, by its LID", text("Q1", "900000001@lid"), quote("")},
		{"the account's, by its LID with a device", text("Q1", "900000001:5@lid"), quote("")},
		{"the account's, by its legacy phone number", text("Q1", "5511900000001@c.us"), quote("")},
		{"on a media message", image, quote("333@lid")},
		{"in an edit's new content", edit, quote("333@lid")},
		{"on a poll", poll, quote("333@lid")},
		{"on a poll of another version", pollV3, quote("333@lid")},
		{"a quote without a participant", text("Q1", ""), nil},
		{"a participant without a server", text("Q1", "333@"), nil},
		{"a group as the participant", text("Q1", "120363000000000000@g.us"), nil},
		{"a participant that is not a JID", text("Q1", "1:x@s.whatsapp.net"), nil},
		{"a context without a quote", text("", "333@lid"), nil},
		{"no context", &waE2E.Message{Conversation: proto.String("hi")}, nil},
	}

	for _, c := range cases {
		src := types.MessageSource{Chat: jid("120363000000000000@g.us"), Sender: jid("444:1@lid")}
		e := &waevents.Message{Info: types.MessageInfo{MessageSource: src, ID: "M1"}, Message: c.message}
		if got := MessageEvent(e, account()).GetReplyTo(); !proto.Equal(got, c.want) {
			t.Errorf("%s: reply to %v, want %v", c.name, got, c.want)
		}
	}
}
