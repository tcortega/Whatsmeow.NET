package chatstate

import (
	"time"

	"go.mau.fi/whatsmeow/types"
	"go.mau.fi/whatsmeow/types/events"
	"google.golang.org/protobuf/types/known/timestamppb"

	whatsmeowv1 "github.com/tcortega/Whatsmeow.NET/sidecar/gen/whatsmeow/v1"
	"github.com/tcortega/Whatsmeow.NET/sidecar/internal/server"
)

// Event is the slice's events.Mapper: it maps a chat state change of the app state sync to the ChatStateEvent the
// outbox records, or false for any other event. It is pure: no I/O.
//
// None is gated: FetchAppState ignores the handlers' result (appstate.go:36-38), a server_sync notification's ack
// waits for nothing but the fetch (notification.go:73-89, 481-483), and the echo of the session's own write comes on a
// goroutine (appstate.go:631-635). So the key is empty, as for a ConnectionStateEvent: they are never redispatched
// from the decrypted-event buffer, and a repeat (an echo, a full sync) carries the same value (ADR 0031).
func Event(_ string, evt any) (string, *whatsmeowv1.StreamEventsResponse, bool) {
	var event *whatsmeowv1.ChatStateEvent

	switch e := evt.(type) {
	case *events.Mute:
		event = changed(e.JID, e.Timestamp, e.FromFullSync)
		event.Change = &whatsmeowv1.ChatStateEvent_Mute{Mute: muted(e.Action.GetMuted(), e.Action.GetMuteEndTimestamp())}
	case *events.Pin:
		event = changed(e.JID, e.Timestamp, e.FromFullSync)
		event.Change = &whatsmeowv1.ChatStateEvent_Pin{Pin: &whatsmeowv1.ChatPin{Pinned: e.Action.GetPinned()}}
	case *events.Archive:
		event = changed(e.JID, e.Timestamp, e.FromFullSync)
		event.Change = &whatsmeowv1.ChatStateEvent_Archive{Archive: &whatsmeowv1.ChatArchive{Archived: e.Action.GetArchived()}}
	case *events.MarkChatAsRead:
		event = changed(e.JID, e.Timestamp, e.FromFullSync)
		event.Change = &whatsmeowv1.ChatStateEvent_Read{Read: &whatsmeowv1.ChatRead{Read: e.Action.GetRead()}}
	case *events.Star:
		event = changed(e.ChatJID, e.Timestamp, e.FromFullSync)
		event.Change = &whatsmeowv1.ChatStateEvent_Star{Star: &whatsmeowv1.MessageStar{
			MessageId: server.Text(e.MessageID), Sender: text(e.SenderJID.ToNonAD()), IsFromMe: e.IsFromMe, Starred: e.Action.GetStarred(),
		}}
	default:
		return "", nil, false
	}

	return "", &whatsmeowv1.StreamEventsResponse{Payload: &whatsmeowv1.StreamEventsResponse_ChatState{ChatState: event}}, true
}

func changed(chat types.JID, at time.Time, fromFullSync bool) *whatsmeowv1.ChatStateEvent {
	return &whatsmeowv1.ChatStateEvent{Chat: text(chat.ToNonAD()), ChangeTime: timestamppb.New(at), FromFullSync: fromFullSync}
}

// muted is a mute's state. whatsmeow reads an end below 0 as forever (appstate.go:271-275), mautrix-whatsapp one of 0
// or less (5dd621f pkg/connector/handlewhatsapp.go:915-917); so does this. The end is in milliseconds, as whatsmeow
// reads it [inferred: see mute].
func muted(isMuted bool, end int64) *whatsmeowv1.ChatMute {
	m := &whatsmeowv1.ChatMute{Muted: isMuted}
	if isMuted && end > 0 {
		m.Until = timestamppb.New(time.UnixMilli(end))
	}

	return m
}

// text is a JID's string form, empty for the empty JID.
func text(jid types.JID) string {
	if jid.IsEmpty() {
		return ""
	}

	return server.Text(jid.String())
}
