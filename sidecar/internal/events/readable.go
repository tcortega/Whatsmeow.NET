package events

import (
	"math"

	whatsmeowv1 "github.com/tcortega/Whatsmeow.NET/sidecar/gen/whatsmeow/v1"
	"github.com/tcortega/Whatsmeow.NET/sidecar/internal/server"
)

// Unreadable is the first field of a mapped event the client cannot read, as the contract names it, or "" for one it
// reads; "payload" for a payload case it does not know, so a new one is never recorded unchecked. It is the client's
// mapping restated (dotnet/src/Whatsmeow.Client, EventMapping.ToEvent and the slices' ToEvent): each field it reads
// with SidecarValues, a JID as server.ReadableJID says and an id when it is not empty. A change the client does not
// know makes it skip the event without reading it, so nothing is required of one. The Events slice records no event
// for which it is not "" (ARCHITECTURE §8.7, #122); a new strict read on the client adds its field here.
func Unreadable(event *whatsmeowv1.StreamEventsResponse) string {
	switch p := event.GetPayload().(type) {
	case *whatsmeowv1.StreamEventsResponse_Message:
		return first(readInfo(p.Message.GetInfo()), readTarget(p.Message.GetTarget()), readTarget(p.Message.GetReplyTo()))
	case *whatsmeowv1.StreamEventsResponse_UndecryptableMessage:
		return readInfo(p.UndecryptableMessage.GetInfo())
	case *whatsmeowv1.StreamEventsResponse_Contact:
		return readJID(p.Contact.GetChange() != nil, p.Contact.GetJid(), "ContactEvent.jid")
	case *whatsmeowv1.StreamEventsResponse_ChatState:
		c := p.ChatState

		return first(readJID(c.GetChange() != nil, c.GetChat(), "ChatStateEvent.chat"), readID(c.GetStar() != nil, c.GetStar().GetMessageId(), "MessageStar.message_id"))
	case *whatsmeowv1.StreamEventsResponse_Receipt:
		ids := ""
		for _, m := range p.Receipt.GetMessageIds() {
			ids = first(ids, readID(true, m, "ReceiptEvent.message_ids"))
		}

		return first(readSource(p.Receipt.GetSource()), ids)
	case *whatsmeowv1.StreamEventsResponse_Group:
		return readGroup(p.Group)
	case *whatsmeowv1.StreamEventsResponse_MediaRetry:
		r := p.MediaRetry
		m := r.GetMedia()
		mediaType, fileLength := "", ""

		if m != nil && m.GetMediaType() == whatsmeowv1.MediaType_MEDIA_TYPE_UNSPECIFIED {
			mediaType = "MediaRef.media_type"
		}

		if m.GetFileLength() > math.MaxInt64 {
			fileLength = "MediaRef.file_length"
		}

		return first(readID(true, r.GetMessageId(), "MediaRetryEvent.message_id"), readJID(true, r.GetChat(), "MediaRetryEvent.chat"), mediaType, fileLength)
	default:
		return "payload"
	}
}

// unreadableEphemeral is Unreadable for an ephemeral event: a presence's user, and a chat presence's chat and sender
// (EventMapping.ToEvent). whatsmeow requires them (presence.go:19, 40-56); the check is the gate's, for every event.
func unreadableEphemeral(event *whatsmeowv1.StreamEphemeralEventsResponse) string {
	switch p := event.GetPayload().(type) {
	case *whatsmeowv1.StreamEphemeralEventsResponse_Presence:
		return readJID(true, p.Presence.GetFrom(), "PresenceEvent.from")
	case *whatsmeowv1.StreamEphemeralEventsResponse_ChatPresence:
		return readSource(p.ChatPresence.GetSource())
	default:
		return ""
	}
}

func readInfo(i *whatsmeowv1.MessageInfo) string {
	return first(readID(true, i.GetId(), "MessageInfo.id"), readSource(i.GetSource()))
}

func readSource(s *whatsmeowv1.MessageSource) string {
	return first(readJID(true, s.GetChat(), "MessageSource.chat"), readJID(true, s.GetSender(), "MessageSource.sender"))
}

// readTarget is a reaction's, an edit's, a revoke's or a quote's message: the client reads its id unless the sender is
// one it cannot read, when it drops the key (ContentMapping.ToKey).
func readTarget(t *whatsmeowv1.MessageTarget) string {
	return readID(t != nil && (t.GetSender() == "" || server.ReadableJID(t.GetSender())), t.GetId(), "MessageTarget.id")
}

func readGroup(g *whatsmeowv1.GroupEvent) string {
	if joined := g.GetJoined(); joined != nil {
		field := readJID(true, joined.GetGroup().GetJid(), "Group.jid")
		for _, p := range joined.GetGroup().GetParticipants() {
			field = first(field, readJID(true, p.GetJid(), "GroupParticipant.jid"))
		}

		return field
	}

	changes := g.GetChanged().GetChanges()
	field := readJID(len(changes) > 0, g.GetGroup(), "GroupEvent.group")

	for _, c := range changes {
		for _, list := range []*whatsmeowv1.GroupChange_Participants{c.GetJoined(), c.GetLeft(), c.GetPromoted(), c.GetDemoted()} {
			for _, j := range list.GetJids() {
				field = first(field, readJID(true, j, "GroupChange.Participants.jids"))
			}
		}
	}

	return field
}

func readJID(required bool, value, field string) string {
	if required && !server.ReadableJID(value) {
		return field
	}

	return ""
}

func readID(required bool, value, field string) string {
	if required && value == "" {
		return field
	}

	return ""
}

// first is the first field that is not "".
func first(fields ...string) string {
	for _, f := range fields {
		if f != "" {
			return f
		}
	}

	return ""
}
