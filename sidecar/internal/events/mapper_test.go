package events

import (
	"testing"

	"github.com/rs/zerolog"
	waevents "go.mau.fi/whatsmeow/types/events"

	whatsmeowv1 "github.com/tcortega/Whatsmeow.NET/sidecar/gen/whatsmeow/v1"
)

// contactMapper stands in for a slice's mapper: it records a PushName as a ContactEvent, keyed by the session.
func contactMapper(sessionID string, evt any) (string, *whatsmeowv1.StreamEventsResponse, bool) {
	e, ok := evt.(*waevents.PushName)
	if !ok {
		return "", nil, false
	}

	return sessionID + "/" + e.NewPushName, &whatsmeowv1.StreamEventsResponse{Payload: &whatsmeowv1.StreamEventsResponse_Contact{
		Contact: &whatsmeowv1.ContactEvent{Jid: e.JID.String(), Change: &whatsmeowv1.ContactEvent_PushName_{
			PushName: &whatsmeowv1.ContactEvent_PushName{Name: e.NewPushName},
		}},
	}}, true
}

func TestTheHandlerRecordsWhatASlicesMapperMapsUnderItsKey(t *testing.T) {
	t.Parallel()

	h := start(t, "s1")
	h.svc = New(h.box, zerolog.Nop(), contactMapper)

	h.mustDeliver("s1", &waevents.PushName{JID: jid("111@lid"), NewPushName: "Ana"})
	// The same key again is the same event: it keeps its seq, so the outbox still has one.
	h.mustDeliver("s1", &waevents.PushName{JID: jid("111@lid"), NewPushName: "Ana"})
	h.mustDeliver("s1", messageEvent("M1"))

	events, err := h.box.Read(h.ctx, "s1", 0, 10)
	if err != nil || len(events) != 2 {
		t.Fatalf("the outbox has %v, %v; want the push name, then M1", events, err)
	}

	if got := events[0].GetContact(); got.GetJid() != "111@lid" || got.GetPushName().GetName() != "Ana" {
		t.Fatalf("the first event is %v, want Ana's push name", events[0])
	}

	if events[1].GetMessage().GetInfo().GetId() != "M1" {
		t.Fatalf("the second event is %v, want M1", events[1])
	}
}

func TestTheHandlerWithholdsTheAnswerWhenAMappedWriteFails(t *testing.T) {
	t.Parallel()

	h := start(t, "s1")
	h.svc = New(h.box, zerolog.Nop(), contactMapper)

	if _, err := h.db.DB.ExecContext(h.ctx, "DROP TABLE outbox"); err != nil {
		t.Fatalf("drop the outbox: %v", err)
	}

	if h.deliver("s1", &waevents.PushName{JID: jid("111@lid"), NewPushName: "Ana"}) {
		t.Fatal("the handler answered an event it could not record")
	}
}
