package messaging

import (
	"context"
	"fmt"

	"go.mau.fi/whatsmeow/proto/waE2E"
	"go.mau.fi/whatsmeow/types"
	"google.golang.org/protobuf/proto"
	"google.golang.org/protobuf/types/known/timestamppb"

	whatsmeowv1 "github.com/tcortega/Whatsmeow.NET/sidecar/gen/whatsmeow/v1"
)

// SendReaction reacts to a message, or removes the account's reaction with an empty emoji (message.proto), through the
// same queue and send records as a text. The reaction is whatsmeow's own (BuildReaction, send.go:551-561): an empty
// sender or the account's own names the account's message, and whatsmeow marks a removal for the server itself
// (send.go:1076).
func (svc *Service) SendReaction(ctx context.Context, req *whatsmeowv1.SendReactionRequest) (*whatsmeowv1.SendReactionResponse, error) {
	sender, err := target(req.GetTargetId(), req.GetTargetSender())
	if err != nil {
		return nil, err
	}

	sent, err := svc.send(ctx, req.GetSessionId(), req.GetChat(), req.GetClientMessageId(), func(wa whatsApp, chat types.JID) *waE2E.Message {
		return wa.BuildReaction(chat, sender, req.GetTargetId(), req.GetEmoji())
	})
	if err != nil {
		return nil, err
	}

	return &whatsmeowv1.SendReactionResponse{Chat: sent.chat, MessageTime: timestamppb.New(sent.at)}, nil
}

// SendEdit replaces the text of a message the account sent (message.proto). The edit is whatsmeow's own (BuildEdit,
// send.go:616-635), which checks no edit window: whatsmeow's EditWindow is read nowhere at the pin.
func (svc *Service) SendEdit(ctx context.Context, req *whatsmeowv1.SendEditRequest) (*whatsmeowv1.SendEditResponse, error) {
	if req.GetTargetId() == "" {
		return nil, invalid("target_id", "no target message")
	}

	if req.GetText() == "" {
		return nil, invalid("text", "the text is empty")
	}

	sent, err := svc.send(ctx, req.GetSessionId(), req.GetChat(), req.GetClientMessageId(), func(wa whatsApp, chat types.JID) *waE2E.Message {
		return wa.BuildEdit(chat, req.GetTargetId(), &waE2E.Message{Conversation: proto.String(req.GetText())})
	})
	if err != nil {
		return nil, err
	}

	return &whatsmeowv1.SendEditResponse{Chat: sent.chat, MessageTime: timestamppb.New(sent.at)}, nil
}

// SendRevoke deletes a message for everyone (message.proto). The revoke is whatsmeow's own (BuildRevoke,
// send.go:536-544): an empty sender or the account's own revokes the account's message, another member's is an admin
// revoke, which whatsmeow marks for the server itself (send.go:1063-1069).
func (svc *Service) SendRevoke(ctx context.Context, req *whatsmeowv1.SendRevokeRequest) (*whatsmeowv1.SendRevokeResponse, error) {
	sender, err := target(req.GetTargetId(), req.GetTargetSender())
	if err != nil {
		return nil, err
	}

	sent, err := svc.send(ctx, req.GetSessionId(), req.GetChat(), req.GetClientMessageId(), func(wa whatsApp, chat types.JID) *waE2E.Message {
		return wa.BuildRevoke(chat, sender, req.GetTargetId())
	})
	if err != nil {
		return nil, err
	}

	return &whatsmeowv1.SendRevokeResponse{Chat: sent.chat, MessageTime: timestamppb.New(sent.at)}, nil
}

// target checks a target message's id and sender; no sender is the empty JID, the account's own message.
func target(id, sender string) (types.JID, error) {
	if id == "" {
		return types.JID{}, invalid("target_id", "no target message")
	}

	if sender == "" {
		return types.EmptyJID, nil
	}

	return user("target_sender", sender)
}

// contextInfo is the context of a reply and its mentions, as mautrix sends it (from-matrix.go:56-96, 344-375): the
// quoted message's id and sender, the quoted text as a conversation (empty when none was given, as mautrix falls back
// to), an explicit quote, and the mentioned users. No reply and no mentions is none. A reply without a sender quotes
// the account's own message: its device's JID names it.
func (svc *Service) contextInfo(sessionID string, reply *whatsmeowv1.MessageReply, mentions []string) (*waE2E.ContextInfo, error) {
	if reply == nil && len(mentions) == 0 {
		return nil, nil //nolint:nilnil // no context is not sent
	}

	info := &waE2E.ContextInfo{}

	for _, mention := range mentions {
		j, err := user("mentions", mention)
		if err != nil {
			return nil, err
		}

		info.MentionedJID = append(info.MentionedJID, j.ToNonAD().String())
	}

	if reply == nil {
		return info, nil
	}

	if reply.GetId() == "" {
		return nil, invalid("reply_to.id", "no quoted message")
	}

	sender, err := svc.quoted(sessionID, reply.GetSender())
	if err != nil {
		return nil, err
	}

	info.StanzaID = proto.String(reply.GetId())
	info.Participant = proto.String(sender.ToNonAD().String())
	info.QuotedMessage = &waE2E.Message{Conversation: proto.String(reply.GetQuotedText())}
	info.QuotedType = waE2E.ContextInfo_EXPLICIT.Enum()

	return info, nil
}

// quoted is the sender of a quoted message: the one given, or the account's own JID when none was.
func (svc *Service) quoted(sessionID, sender string) (types.JID, error) {
	if sender != "" {
		return user("reply_to.sender", sender)
	}

	if err := validateSession(sessionID); err != nil {
		return types.JID{}, err
	}

	device, err := svc.sessions.Device(sessionID)
	if err != nil {
		return types.JID{}, fmt.Errorf("reply: %w", err)
	}

	if device.ID == nil {
		return types.JID{}, fmt.Errorf("reply: session %q has no device JID", sessionID) //nolint:err113 // Device returns only paired devices
	}

	return *device.ID, nil
}

// withContext puts info on the media message of message, inside a captioned document's wrapper.
func withContext(message *waE2E.Message, info *waE2E.ContextInfo) {
	if info == nil {
		return
	}

	if wrapped := message.GetDocumentWithCaptionMessage().GetMessage(); wrapped != nil {
		message = wrapped
	}

	switch {
	case message.ImageMessage != nil:
		message.ImageMessage.ContextInfo = info
	case message.VideoMessage != nil:
		message.VideoMessage.ContextInfo = info
	case message.AudioMessage != nil:
		message.AudioMessage.ContextInfo = info
	case message.DocumentMessage != nil:
		message.DocumentMessage.ContextInfo = info
	case message.StickerMessage != nil:
		message.StickerMessage.ContextInfo = info
	}
}
