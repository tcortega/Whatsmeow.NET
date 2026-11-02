package events

import (
	"strings"

	"go.mau.fi/whatsmeow/proto/waE2E"
	"go.mau.fi/whatsmeow/types"
	waevents "go.mau.fi/whatsmeow/types/events"
	"google.golang.org/protobuf/proto"
	"google.golang.org/protobuf/types/known/timestamppb"

	whatsmeowv1 "github.com/tcortega/Whatsmeow.NET/sidecar/gen/whatsmeow/v1"
	"github.com/tcortega/Whatsmeow.NET/sidecar/internal/server"
)

// toEvent maps a whatsmeow event to the outbox event it records and that event's natural key, or false for an
// event this slice does not record. Connection events are not here: the session slice records them as it changes
// state (session.(*Service).transition). It is pure: no I/O.
//
// The key ties a repeated event to its first recording, so it keeps its seq: whatsmeow dispatches a message again
// from its decrypted-event buffer after a crash between our commit and its buffer clear (message.go:459-477).
func toEvent(evt any) (string, *whatsmeowv1.StreamEventsResponse, bool) {
	switch e := evt.(type) {
	case *waevents.Message:
		// A history sync notification is the History slice's (internal/history): never a live message.
		if onlySenderKey(e.Message) || e.Message.GetProtocolMessage().GetHistorySyncNotification() != nil {
			return "", nil, false
		}

		return key("m", &e.Info), &whatsmeowv1.StreamEventsResponse{Payload: &whatsmeowv1.StreamEventsResponse_Message{
			Message: MessageEvent(e),
		}}, true
	case *waevents.UndecryptableMessage:
		return key("u", &e.Info), &whatsmeowv1.StreamEventsResponse{Payload: &whatsmeowv1.StreamEventsResponse_UndecryptableMessage{
			UndecryptableMessage: &whatsmeowv1.UndecryptableMessageEvent{
				Info:            info(&e.Info),
				HidePlaceholder: e.DecryptFailMode == waevents.DecryptFailHide,
				Unavailable:     e.IsUnavailable,
				UnavailableType: unavailableType(e.UnavailableType),
			},
		}}, true
	default:
		return "", nil, false
	}
}

// unavailableType is why WhatsApp sent no ciphertext, as events.proto has it; unspecified for none and for a type
// the contract does not have.
func unavailableType(t waevents.UnavailableType) whatsmeowv1.UnavailableType {
	if t == waevents.UnavailableTypeViewOnce {
		return whatsmeowv1.UnavailableType_UNAVAILABLE_TYPE_VIEW_ONCE
	}

	return whatsmeowv1.UnavailableType_UNAVAILABLE_TYPE_UNSPECIFIED
}

// MessageEvent is a whatsmeow message as the contract's MessageEvent: a live one's, and a history message's, which the
// History slice maps with it (internal/history), so a history message carries what a live one does.
func MessageEvent(e *waevents.Message) *whatsmeowv1.MessageEvent {
	return &whatsmeowv1.MessageEvent{Info: info(&e.Info), Message: e.Message, ViewOnce: e.IsViewOnce}
}

// onlySenderKey is a message with nothing but a sender key and its context info: the part of a group stanza that
// carries the sender key next to the skmsg content (send.go:795-831). whatsmeow dispatches each decrypted part as a
// Message with the same info (message.go:1172-1178) and stores the sender key itself (processProtocolParts,
// message.go:938-961), so recording it would take the content's natural key and drop the content.
func onlySenderKey(message *waE2E.Message) bool {
	if message.GetSenderKeyDistributionMessage() == nil {
		return false
	}

	rest := proto.CloneOf(message)
	rest.SenderKeyDistributionMessage = nil
	rest.MessageContextInfo = nil

	return proto.Size(rest) == 0
}

// key is a message's natural key: its kind, chat, sender and id. A redispatch parses the same stanza again, so it
// has the same JIDs.
func key(kind string, info *types.MessageInfo) string {
	return strings.Join([]string{kind, info.Chat.String(), info.Sender.String(), info.ID}, "\x00")
}

func info(info *types.MessageInfo) *whatsmeowv1.MessageInfo {
	return &whatsmeowv1.MessageInfo{
		Source:      source(&info.MessageSource),
		Id:          server.Text(info.ID),
		PushName:    server.Text(info.PushName),
		MessageTime: timestamppb.New(info.Timestamp),
	}
}

func source(source *types.MessageSource) *whatsmeowv1.MessageSource {
	return &whatsmeowv1.MessageSource{
		Chat:         server.Text(source.Chat.String()),
		Sender:       server.Text(source.Sender.String()),
		SenderAlt:    server.Text(source.SenderAlt.String()),
		RecipientAlt: server.Text(source.RecipientAlt.String()),
		IsFromMe:     source.IsFromMe,
	}
}
