package events

import (
	"bytes"
	"context"
	"fmt"
	"net"
	"strings"
	"testing"
	"time"

	"github.com/rs/zerolog"
	"go.mau.fi/whatsmeow/types"
	waevents "go.mau.fi/whatsmeow/types/events"
	"google.golang.org/grpc"
	"google.golang.org/grpc/codes"
	"google.golang.org/grpc/credentials/insecure"
	"google.golang.org/protobuf/proto"
	"google.golang.org/protobuf/types/known/timestamppb"

	whatsmeowv1 "github.com/tcortega/Whatsmeow.NET/sidecar/gen/whatsmeow/v1"
)

// presence is user n's presence, its last seen n seconds after an instant, so each one is told apart.
func presence(n int) *waevents.Presence {
	return &waevents.Presence{From: jid(fmt.Sprintf("%d@s.whatsapp.net", 1000+n)), LastSeen: time.Unix(1_700_000_000+int64(n), 0)}
}

func streamedPresence(n int) *whatsmeowv1.StreamEphemeralEventsResponse {
	return &whatsmeowv1.StreamEphemeralEventsResponse{Payload: &whatsmeowv1.StreamEphemeralEventsResponse_Presence{Presence: &whatsmeowv1.PresenceEvent{
		From: fmt.Sprintf("%d@s.whatsapp.net", 1000+n), State: whatsmeowv1.PresenceState_PRESENCE_STATE_AVAILABLE,
		LastSeen: timestamppb.New(time.Unix(1_700_000_000+int64(n), 0)),
	}}}
}

func overflow(dropped uint64) *whatsmeowv1.StreamEphemeralEventsResponse {
	return &whatsmeowv1.StreamEphemeralEventsResponse{Payload: &whatsmeowv1.StreamEphemeralEventsResponse_QueueOverflow{
		QueueOverflow: &whatsmeowv1.QueueOverflowEvent{Dropped: dropped},
	}}
}

// openEphemeral opens the session's ephemeral stream and returns once the sidecar placed it: it sends the headers
// then.
func (h *harness) openEphemeral(client whatsmeowv1.EventServiceClient, session string) grpc.ServerStreamingClient[whatsmeowv1.StreamEphemeralEventsResponse] {
	h.t.Helper()

	stream, err := client.StreamEphemeralEvents(h.ctx, &whatsmeowv1.StreamEphemeralEventsRequest{SessionId: session})
	if err != nil {
		h.t.Fatalf("StreamEphemeralEvents: %v", err)
	}

	if _, err := stream.Header(); err != nil {
		h.t.Fatalf("StreamEphemeralEvents headers: %v", err)
	}

	return stream
}

func recvEphemeral(t *testing.T, stream grpc.ServerStreamingClient[whatsmeowv1.StreamEphemeralEventsResponse], want ...*whatsmeowv1.StreamEphemeralEventsResponse) {
	t.Helper()

	for i, w := range want {
		got, err := stream.Recv()
		if err != nil {
			t.Fatalf("event %d: Recv: %v", i, err)
		}

		if !proto.Equal(got, w) {
			t.Fatalf("event %d = %v, want %v", i, got, w)
		}
	}
}

func TestEveryEphemeralEventMapsToOneStreamedEvent(t *testing.T) {
	t.Parallel()

	at := time.Unix(1_700_000_000, 0)
	source := types.MessageSource{
		Chat: jid("120363000000000000@g.us"), Sender: jid("111:2@lid"), IsFromMe: false, IsGroup: true,
		SenderAlt: jid("5511999999999@s.whatsapp.net"),
	}
	streamedSource := &whatsmeowv1.MessageSource{Chat: "120363000000000000@g.us", Sender: "111:2@lid", SenderAlt: "5511999999999@s.whatsapp.net"}
	chat := func(state whatsmeowv1.ChatPresenceState) *whatsmeowv1.StreamEphemeralEventsResponse {
		return &whatsmeowv1.StreamEphemeralEventsResponse{Payload: &whatsmeowv1.StreamEphemeralEventsResponse_ChatPresence{
			ChatPresence: &whatsmeowv1.ChatPresenceEvent{Source: streamedSource, State: state},
		}}
	}
	user := func(state whatsmeowv1.PresenceState, lastSeen *timestamppb.Timestamp) *whatsmeowv1.StreamEphemeralEventsResponse {
		return &whatsmeowv1.StreamEphemeralEventsResponse{Payload: &whatsmeowv1.StreamEphemeralEventsResponse_Presence{
			Presence: &whatsmeowv1.PresenceEvent{From: "111@lid", State: state, LastSeen: lastSeen},
		}}
	}

	cases := []struct {
		name string
		evt  any
		want *whatsmeowv1.StreamEphemeralEventsResponse
	}{
		{"online", &waevents.Presence{From: jid("111@lid"), LastSeen: at}, user(whatsmeowv1.PresenceState_PRESENCE_STATE_AVAILABLE, timestamppb.New(at))},
		{"offline", &waevents.Presence{From: jid("111@lid"), Unavailable: true, LastSeen: at}, user(whatsmeowv1.PresenceState_PRESENCE_STATE_UNAVAILABLE, timestamppb.New(at))},
		// whatsmeow leaves LastSeen zero when the stanza has none or "deny" (presence.go:49-52).
		{"last seen hidden", &waevents.Presence{From: jid("111@lid"), Unavailable: true}, user(whatsmeowv1.PresenceState_PRESENCE_STATE_UNAVAILABLE, nil)},
		{"typing", &waevents.ChatPresence{MessageSource: source, State: types.ChatPresenceComposing}, chat(whatsmeowv1.ChatPresenceState_CHAT_PRESENCE_STATE_TYPING)},
		{"recording", &waevents.ChatPresence{MessageSource: source, State: types.ChatPresenceComposing, Media: types.ChatPresenceMediaAudio}, chat(whatsmeowv1.ChatPresenceState_CHAT_PRESENCE_STATE_RECORDING)},
		{"paused", &waevents.ChatPresence{MessageSource: source, State: types.ChatPresencePaused}, chat(whatsmeowv1.ChatPresenceState_CHAT_PRESENCE_STATE_PAUSED)},
		// whatsmeow dispatches a state tag it does not know (presence.go:26-35).
		{"unknown state", &waevents.ChatPresence{MessageSource: source, State: "recording-video"}, chat(whatsmeowv1.ChatPresenceState_CHAT_PRESENCE_STATE_UNSPECIFIED)},
		{"composing with unknown media", &waevents.ChatPresence{MessageSource: source, State: types.ChatPresenceComposing, Media: "video"}, chat(whatsmeowv1.ChatPresenceState_CHAT_PRESENCE_STATE_UNSPECIFIED)},
	}

	for _, c := range cases {
		got, ok := toEphemeral(c.evt)
		if !ok || !proto.Equal(got, c.want) {
			t.Errorf("%s: toEphemeral = %v, %v; want %v", c.name, got, ok, c.want)
		}
	}

	if got, ok := toEphemeral(messageEvent("M1")); ok {
		t.Errorf("a message is ephemeral: %v", got)
	}
}

func TestEphemeralEventsAreStreamedWithoutASeqAndNeverRecorded(t *testing.T) {
	t.Parallel()

	h := start(t, "s1")
	h.mustDeliver("s1", presence(1))
	h.mustDeliver("s1", &waevents.ChatPresence{MessageSource: types.MessageSource{Chat: jid("111@lid"), Sender: jid("111@lid")}, State: types.ChatPresenceComposing})

	recvEphemeral(t, h.openEphemeral(h.client, "s1"), streamedPresence(1), &whatsmeowv1.StreamEphemeralEventsResponse{
		Payload: &whatsmeowv1.StreamEphemeralEventsResponse_ChatPresence{ChatPresence: &whatsmeowv1.ChatPresenceEvent{
			Source: &whatsmeowv1.MessageSource{Chat: "111@lid", Sender: "111@lid"}, State: whatsmeowv1.ChatPresenceState_CHAT_PRESENCE_STATE_TYPING,
		}},
	})

	if highest, err := h.box.Highest(h.ctx, "s1"); err != nil || highest != 0 {
		t.Fatalf("the session's highest recorded seq is %d, %v; want 0, nothing recorded", highest, err)
	}
}

func TestAStreamOpenedWithNoneOpenGetsTheDropCountThenTheNewest(t *testing.T) {
	t.Parallel()

	h := start(t, "s1")
	for i := range ringSize + 5 {
		h.mustDeliver("s1", presence(i))
	}

	want := []*whatsmeowv1.StreamEphemeralEventsResponse{overflow(5)}
	for i := 5; i < ringSize+5; i++ {
		want = append(want, streamedPresence(i))
	}

	stream := h.openEphemeral(h.client, "s1")
	recvEphemeral(t, stream, want...)

	// Then live ones, with nothing dropped.
	h.mustDeliver("s1", presence(1000))
	recvEphemeral(t, stream, streamedPresence(1000))
}

func TestAStreamOpenedWhileAnotherIsOpenStartsWithTheNextEventAndBothGetIt(t *testing.T) {
	t.Parallel()

	h := start(t, "s1")
	first := h.openEphemeral(h.client, "s1")
	h.mustDeliver("s1", presence(1))
	recvEphemeral(t, first, streamedPresence(1))

	second := h.openEphemeral(h.client, "s1")
	h.mustDeliver("s1", presence(2))

	recvEphemeral(t, first, streamedPresence(2))
	recvEphemeral(t, second, streamedPresence(2))
}

func TestAStreamThatFallsBehindGetsEveryEventOrItsDropCountAndTheHandlerNeverWaits(t *testing.T) {
	t.Parallel()

	h := start(t, "s1")

	// A fixed 64 KiB window: without one, grpc-go's BDP estimation grows the window of a stream nobody reads.
	slow, err := grpc.NewClient("passthrough:///bufnet",
		grpc.WithContextDialer(func(ctx context.Context, _ string) (net.Conn, error) { return h.lis.DialContext(ctx) }),
		grpc.WithTransportCredentials(insecure.NewCredentials()),
		grpc.WithInitialWindowSize(64<<10), grpc.WithInitialConnWindowSize(64<<10))
	if err != nil {
		t.Fatalf("dial: %v", err)
	}
	defer func() { _ = slow.Close() }()

	stream := h.openEphemeral(whatsmeowv1.NewEventServiceClient(slow), "s1")

	// Far more than the window and the ring hold, while the stream is not read: the handler returns every time.
	const pushed = 20_000

	delivered := make(chan struct{})

	go func() {
		defer close(delivered)

		for i := range pushed {
			if !h.deliver("s1", presence(i)) {
				t.Errorf("the handler withheld the answer to presence %d", i)
			}
		}
	}()

	select {
	case <-delivered:
	case <-h.ctx.Done():
		t.Fatal("the handler waited for a stream that does not read")
	}

	var received, dropped, overflows uint64

	last := -1

	for received+dropped < pushed {
		event, err := stream.Recv()
		if err != nil {
			t.Fatalf("Recv after %d received and %d dropped: %v", received, dropped, err)
		}

		if n := event.GetQueueOverflow().GetDropped(); n > 0 {
			dropped += n
			overflows++

			continue
		}

		// Oldest first: what is dropped is before what is sent.
		n := int(event.GetPresence().GetLastSeen().AsTime().Unix() - 1_700_000_000)
		if n <= last {
			t.Fatalf("presence %d after %d", n, last)
		}

		last = n
		received++
	}

	if received+dropped != pushed || overflows == 0 {
		t.Fatalf("received %d and dropped %d in %d overflows; want %d in all, some dropped", received, dropped, overflows, pushed)
	}
}

func TestStreamEphemeralEventsOfAnUnknownSessionIsNotFoundAndEndsWhenTheSessionIsDeleted(t *testing.T) {
	t.Parallel()

	h := start(t, "s1")

	unknown, err := h.client.StreamEphemeralEvents(h.ctx, &whatsmeowv1.StreamEphemeralEventsRequest{SessionId: "nope"})
	if err == nil {
		_, err = unknown.Recv()
	}

	wantReason(t, err, codes.NotFound, "SESSION_NOT_FOUND")

	stream := h.openEphemeral(h.client, "s1")

	// As the session slice's DeleteSession does.
	if _, err := h.db.DB.ExecContext(h.ctx, "DELETE FROM session WHERE id = 's1'"); err != nil {
		t.Fatalf("delete the session: %v", err)
	}

	h.box.Delete("s1")

	_, err = stream.Recv()
	wantReason(t, err, codes.NotFound, "SESSION_NOT_FOUND")
}

func TestADeletedSessionsEphemeralEventsNeverReachTheNextSessionOfItsID(t *testing.T) {
	t.Parallel()

	h := start(t, "s1")
	h.mustDeliver("s1", presence(1))

	// As the session slice's DeleteSession does, then CreateSession with the same id.
	if _, err := h.db.DB.ExecContext(h.ctx, "DELETE FROM session WHERE id = 's1'"); err != nil {
		t.Fatalf("delete the session: %v", err)
	}

	h.box.Delete("s1")

	if _, err := h.db.DB.ExecContext(h.ctx, "INSERT INTO session (id) VALUES ('s1')"); err != nil {
		t.Fatalf("create the session again: %v", err)
	}

	h.mustDeliver("s1", presence(2))
	recvEphemeral(t, h.openEphemeral(h.client, "s1"), streamedPresence(2))
}

func TestALateEventOfADeletedSessionsClientNeverReachesTheNextSessionOfItsID(t *testing.T) {
	t.Parallel()

	h := start(t, "s1")
	old := h.svc.Handler("s1", nil)

	// As the session slice's DeleteSession does, then CreateSession with the same id and a client of its own.
	if _, err := h.db.DB.ExecContext(h.ctx, "DELETE FROM session WHERE id = 's1'"); err != nil {
		t.Fatalf("delete the session: %v", err)
	}

	h.box.Delete("s1")

	if _, err := h.db.DB.ExecContext(h.ctx, "INSERT INTO session (id) VALUES ('s1')"); err != nil {
		t.Fatalf("create the session again: %v", err)
	}

	next := h.svc.Handler("s1", nil)

	// The deleted session's client is still disconnecting, and its handler runs once more.
	if !old(presence(1)) {
		t.Fatal("the old client's handler withheld the answer")
	}

	if !next(presence(2)) {
		t.Fatal("the handler withheld the answer")
	}

	recvEphemeral(t, h.openEphemeral(h.client, "s1"), streamedPresence(2))
}

// TestAnEphemeralEventTheClientCannotReadIsNeverStreamed: the client reads a presence's user and a chat presence's
// chat and sender as it reads a message's (EventMapping.ToEvent), and logs and skips one it cannot read. whatsmeow
// requires them (presence.go:19, 40-56); the gate is the Events slice's for every event it hands on (#122).
func TestAnEphemeralEventTheClientCannotReadIsNeverStreamed(t *testing.T) {
	t.Parallel()

	var log bytes.Buffer

	h := start(t, "s1")
	h.svc.log = zerolog.New(&log)
	h.mustDeliver("s1", &waevents.Presence{LastSeen: time.Unix(1_700_000_000, 0)})
	h.mustDeliver("s1", &waevents.ChatPresence{MessageSource: types.MessageSource{Chat: jid("111@lid")}, State: types.ChatPresenceComposing})
	h.mustDeliver("s1", presence(1))

	recvEphemeral(t, h.openEphemeral(h.client, "s1"), streamedPresence(1))

	if got := strings.Count(log.String(), `"level":"warn"`); got != 2 || !strings.Contains(log.String(), `"field":"PresenceEvent.from"`) ||
		!strings.Contains(log.String(), `"field":"MessageSource.sender"`) || strings.Contains(log.String(), "111") {
		t.Fatalf("log = %s; want two warn lines naming the fields, and no value", log.String())
	}
}
