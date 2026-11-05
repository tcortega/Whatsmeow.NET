package events

import (
	"bytes"
	"strings"
	"testing"

	"github.com/rs/zerolog"
	"go.mau.fi/whatsmeow/types"
	waevents "go.mau.fi/whatsmeow/types/events"

	whatsmeowv1 "github.com/tcortega/Whatsmeow.NET/sidecar/gen/whatsmeow/v1"
)

// mapping is a slice's mapper that records what it is given as is, so the gate sees exactly that event.
type mapping struct {
	event *whatsmeowv1.StreamEventsResponse
}

func (m mapping) mapper(_ string, evt any) (string, *whatsmeowv1.StreamEventsResponse, bool) {
	if _, ok := evt.(*waevents.Pin); !ok {
		return "", nil, false
	}

	return "", m.event, true
}

// TestAnEventTheClientCannotReadIsAnsweredNotRecordedAndLogged: the gate after every mapping (#122). The client skips
// and acks such an event (ARCHITECTURE §8.7), so it would be lost anyway; this way it is lost visibly, by kind and field,
// never by value (ADR 0039).
func TestAnEventTheClientCannotReadIsAnsweredNotRecordedAndLogged(t *testing.T) {
	t.Parallel()

	noChat := &whatsmeowv1.StreamEventsResponse{Payload: &whatsmeowv1.StreamEventsResponse_ChatState{ChatState: &whatsmeowv1.ChatStateEvent{
		Chat: "1.2.3@s.whatsapp.net", Change: &whatsmeowv1.ChatStateEvent_Pin{Pin: &whatsmeowv1.ChatPin{Pinned: true}},
	}}}
	// A payload case the gate does not know: refused, so a new one is never recorded unchecked.
	unknown := &whatsmeowv1.StreamEventsResponse{Payload: &whatsmeowv1.StreamEventsResponse_HistoryProgress{HistoryProgress: &whatsmeowv1.HistorySyncProgressEvent{}}}
	nowhere := messageEvent("M1")
	nowhere.Info.Chat = types.EmptyJID

	for _, c := range []struct {
		name, kind, field string
		mapped            *whatsmeowv1.StreamEventsResponse
		evt               any
	}{
		{"a mapper's", "chat_state", "ChatStateEvent.chat", noChat, &waevents.Pin{}},
		{"an unknown case", "history_progress", "payload", unknown, &waevents.Pin{}},
		{"the slice's own", "message", "MessageSource.chat", nil, nowhere},
	} {
		t.Run(c.name, func(t *testing.T) {
			t.Parallel()

			var log bytes.Buffer

			h := start(t, "s1")
			h.svc = New(h.box, zerolog.New(&log), mapping{c.mapped}.mapper)

			if !h.deliver("s1", c.evt) {
				t.Fatal("the handler withheld the answer to an event it does not record")
			}

			if highest, err := h.box.Highest(h.ctx, "s1"); err != nil || highest != 0 {
				t.Fatalf("the highest recorded seq is %d, %v; want 0, nothing recorded", highest, err)
			}

			line := log.String()
			if strings.Count(line, "\n") != 1 || !strings.Contains(line, `"level":"warn"`) || !strings.Contains(line, `"kind":"`+c.kind+`"`) ||
				!strings.Contains(line, `"field":"`+c.field+`"`) || strings.Contains(line, "1.2.3") || strings.Contains(line, "111") {
				t.Fatalf("log = %s; want one warn line naming %s and %s, and no value", line, c.kind, c.field)
			}
		})
	}

	// A readable one is recorded, and logs nothing.
	var log bytes.Buffer

	h := start(t, "s1")
	h.svc = New(h.box, zerolog.New(&log))
	h.mustDeliver("s1", messageEvent("M2"))

	if highest, err := h.box.Highest(h.ctx, "s1"); err != nil || highest != 1 || log.Len() != 0 {
		t.Fatalf("highest %d, %v, log %q; want M2 recorded and nothing logged", highest, err, log.String())
	}
}

// TestUnreadableNamesTheFirstFieldTheClientCannotRead: Unreadable restates the client's mapping, case by case.
func TestUnreadableNamesTheFirstFieldTheClientCannotRead(t *testing.T) {
	t.Parallel()

	source := func(chat, sender string) *whatsmeowv1.MessageSource {
		return &whatsmeowv1.MessageSource{Chat: chat, Sender: sender}
	}
	pn := "5511900000001@s.whatsapp.net"
	info := &whatsmeowv1.MessageInfo{Id: "M1", Source: source(pn, pn)}
	message := func(m *whatsmeowv1.MessageEvent) *whatsmeowv1.StreamEventsResponse {
		return &whatsmeowv1.StreamEventsResponse{Payload: &whatsmeowv1.StreamEventsResponse_Message{Message: m}}
	}
	group := func(g *whatsmeowv1.GroupEvent) *whatsmeowv1.StreamEventsResponse {
		return &whatsmeowv1.StreamEventsResponse{Payload: &whatsmeowv1.StreamEventsResponse_Group{Group: g}}
	}
	changed := func(jids ...string) *whatsmeowv1.GroupEvent_Changed_ {
		return &whatsmeowv1.GroupEvent_Changed_{Changed: &whatsmeowv1.GroupEvent_Changed{Changes: []*whatsmeowv1.GroupChange{
			{Change: &whatsmeowv1.GroupChange_Left{Left: &whatsmeowv1.GroupChange_Participants{Jids: jids}}},
		}}}
	}

	for want, event := range map[string]*whatsmeowv1.StreamEventsResponse{
		"":               message(&whatsmeowv1.MessageEvent{Info: info}),
		"MessageInfo.id": message(&whatsmeowv1.MessageEvent{Info: &whatsmeowv1.MessageInfo{Source: source(pn, pn)}}),
		"MessageSource.sender": {Payload: &whatsmeowv1.StreamEventsResponse_UndecryptableMessage{UndecryptableMessage: &whatsmeowv1.UndecryptableMessageEvent{
			Info: &whatsmeowv1.MessageInfo{Id: "M1", Source: source(pn, "")},
		}}},
		"MessageTarget.id": message(&whatsmeowv1.MessageEvent{Info: info, Target: &whatsmeowv1.MessageTarget{}}),
		"ContactEvent.jid": {Payload: &whatsmeowv1.StreamEventsResponse_Contact{Contact: &whatsmeowv1.ContactEvent{
			Change: &whatsmeowv1.ContactEvent_About_{About: &whatsmeowv1.ContactEvent_About{}},
		}}},
		"MessageStar.message_id": {Payload: &whatsmeowv1.StreamEventsResponse_ChatState{ChatState: &whatsmeowv1.ChatStateEvent{
			Chat: pn, Change: &whatsmeowv1.ChatStateEvent_Star{Star: &whatsmeowv1.MessageStar{}},
		}}},
		"ReceiptEvent.message_ids": {Payload: &whatsmeowv1.StreamEventsResponse_Receipt{Receipt: &whatsmeowv1.ReceiptEvent{
			Source: source(pn, pn), MessageIds: []string{"M1", ""},
		}}},
		"GroupEvent.group":              group(&whatsmeowv1.GroupEvent{Kind: changed(pn)}),
		"GroupChange.Participants.jids": group(&whatsmeowv1.GroupEvent{Group: "120363000000000000@g.us", Kind: changed(pn, "x@")}),
		"GroupParticipant.jid": group(&whatsmeowv1.GroupEvent{Kind: &whatsmeowv1.GroupEvent_Joined_{Joined: &whatsmeowv1.GroupEvent_Joined{Group: &whatsmeowv1.Group{
			Jid: "120363000000000000@g.us", Participants: []*whatsmeowv1.GroupParticipant{{}},
		}}}}),
		"MediaRetryEvent.chat": {Payload: &whatsmeowv1.StreamEventsResponse_MediaRetry{MediaRetry: &whatsmeowv1.MediaRetryEvent{MessageId: "M1"}}},
		"payload":              {},
	} {
		if got := Unreadable(event); got != want {
			t.Errorf("Unreadable(%v) = %q, want %q", event, got, want)
		}
	}
}
