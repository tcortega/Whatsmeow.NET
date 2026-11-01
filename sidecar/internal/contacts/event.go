package contacts

import (
	"go.mau.fi/whatsmeow/types"
	"go.mau.fi/whatsmeow/types/events"

	whatsmeowv1 "github.com/tcortega/Whatsmeow.NET/sidecar/gen/whatsmeow/v1"
)

// Event is the slice's events.Mapper: it maps a contact change whatsmeow dispatched to the ContactEvent the outbox
// records, or false for any other event. It is pure: no I/O.
//
// None of these is gated: whatsmeow dispatches PushName and BusinessName on their own goroutines (message.go:56-61),
// Picture and UserAbout from notifications it acks whatever the handlers return (notification.go:91-115, 449-466,
// 472), so the key is empty, as for a ConnectionStateEvent. They are never redispatched from the decrypted-event
// buffer, and PushName and BusinessName fire only when the stored value changed (user.go:683, 713), so a repeat is
// rare and carries the same value (ADR 0028).
func Event(_ string, evt any) (string, *whatsmeowv1.StreamEventsResponse, bool) {
	var event *whatsmeowv1.ContactEvent

	switch e := evt.(type) {
	case *events.PushName:
		event = &whatsmeowv1.ContactEvent{Jid: user(e.JID), JidAlt: user(e.JIDAlt), Change: &whatsmeowv1.ContactEvent_PushName_{
			PushName: &whatsmeowv1.ContactEvent_PushName{Name: e.NewPushName},
		}}
	case *events.BusinessName:
		event = &whatsmeowv1.ContactEvent{Jid: user(e.JID), Change: &whatsmeowv1.ContactEvent_BusinessName_{
			BusinessName: &whatsmeowv1.ContactEvent_BusinessName{Name: e.NewBusinessName},
		}}
	case *events.Picture:
		picture := &whatsmeowv1.ContactEvent_Picture{Id: e.PictureID}
		if e.Remove {
			picture.Id = ""
		}

		event = &whatsmeowv1.ContactEvent{Jid: user(e.JID), Change: &whatsmeowv1.ContactEvent_Picture_{Picture: picture}}
	case *events.UserAbout:
		event = &whatsmeowv1.ContactEvent{Jid: user(e.JID), Change: &whatsmeowv1.ContactEvent_About_{
			About: &whatsmeowv1.ContactEvent_About{Text: e.Status},
		}}
	default:
		return "", nil, false
	}

	return "", &whatsmeowv1.StreamEventsResponse{Payload: &whatsmeowv1.StreamEventsResponse_Contact{Contact: event}}, true
}

// user is a JID without its device, empty for the empty JID.
func user(jid types.JID) string {
	return text(jid.ToNonAD())
}
