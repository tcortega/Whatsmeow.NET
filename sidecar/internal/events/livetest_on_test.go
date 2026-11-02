//go:build livetest

package events

import (
	"testing"

	"go.mau.fi/whatsmeow/proto/waE2E"
	waevents "go.mau.fi/whatsmeow/types/events"
	"google.golang.org/protobuf/proto"
)

func marked(id string) *waevents.Message {
	msg := messageEvent(id)
	msg.Message = &waE2E.Message{Conversation: proto.String(WithholdPrefix + id)}

	return msg
}

func TestTheLiveTestBuildWithholdsAMarkedMessageOnceThenRecordsIt(t *testing.T) {
	t.Parallel()

	h := start(t, "s1")
	if h.deliver("s1", marked("M1")) {
		t.Fatal("the first delivery of a marked message was answered")
	}

	if events, err := h.box.Read(h.ctx, "s1", 0, 10); err != nil || len(events) != 0 {
		t.Fatalf("the outbox has %v, %v after the withheld delivery; want nothing", events, err)
	}

	h.mustDeliver("s1", marked("M1"))

	_, got := ids(t, h.open("s1", "c1", 0), 1)
	equal(t, got, []string{"M1"})
}

func TestTheLiveTestBuildAnswersAnUnmarkedMessageAndWithholdsAMarkedExtendedText(t *testing.T) {
	t.Parallel()

	h := start(t, "s1")
	h.mustDeliver("s1", messageEvent("M1"))

	extended := messageEvent("M2")
	extended.Message = &waE2E.Message{ExtendedTextMessage: &waE2E.ExtendedTextMessage{Text: proto.String(WithholdPrefix + "M2")}}

	if h.deliver("s1", extended) {
		t.Fatal("the first delivery of a marked extended text was answered")
	}
}

func TestTheLiveTestBuildWithholdsAMarkedMessageOncePerSession(t *testing.T) {
	t.Parallel()

	h := start(t, "s1", "s2")
	if h.deliver("s1", marked("M1")) {
		t.Fatal("the first delivery of a marked message to s1 was answered")
	}

	// The same id in another session is another message: its first delivery is withheld too.
	if h.deliver("s2", marked("M1")) {
		t.Fatal("the first delivery of a marked message to s2 was answered")
	}

	h.mustDeliver("s2", marked("M1"))
}
