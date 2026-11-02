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

	sent, err := svc.send(ctx, req.GetSessionId(), req.GetChat(), req.GetClientMessageId(), func(_ context.Context, wa whatsApp, chat types.JID) (*waE2E.Message, error) {
		return wa.BuildReaction(chat, sender, req.GetTargetId(), req.GetEmoji()), nil
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

	sent, err := svc.send(ctx, req.GetSessionId(), req.GetChat(), req.GetClientMessageId(), func(_ context.Context, wa whatsApp, chat types.JID) (*waE2E.Message, error) {
		return wa.BuildEdit(chat, req.GetTargetId(), &waE2E.Message{Conversation: proto.String(req.GetText())}), nil
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

	sent, err := svc.send(ctx, req.GetSessionId(), req.GetChat(), req.GetClientMessageId(), func(_ context.Context, wa whatsApp, chat types.JID) (*waE2E.Message, error) {
		return wa.BuildRevoke(chat, sender, req.GetTargetId()), nil
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

// replyContext is the context of a reply and its mentions, as mautrix sends it (from-matrix.go:56-96, 344-375): the
// quoted message's id and sender, the quoted text as a conversation (empty when none was given, as mautrix falls back
// to), an explicit quote, and the mentioned users. No reply and no mentions is none. It is checked before the send, so
// a malformed one sends nothing. own is set for a reply without a sender: it quotes the account's own message, whose
// participant named fills in when the send is built.
type replyContext struct {
	info *waE2E.ContextInfo
	own  bool
}

func newReplyContext(reply *whatsmeowv1.MessageReply, mentions []string) (replyContext, error) {
	if reply == nil && len(mentions) == 0 {
		return replyContext{}, nil
	}

	info := &waE2E.ContextInfo{}

	for _, mention := range mentions {
		j, err := user("mentions", mention)
		if err != nil {
			return replyContext{}, err
		}

		info.MentionedJID = append(info.MentionedJID, j.ToNonAD().String())
	}

	if reply == nil {
		return replyContext{info: info}, nil
	}

	if reply.GetId() == "" {
		return replyContext{}, invalid("reply_to.id", "no quoted message")
	}

	info.StanzaID = proto.String(reply.GetId())
	info.QuotedMessage = &waE2E.Message{Conversation: proto.String(reply.GetQuotedText())}
	info.QuotedType = waE2E.ContextInfo_EXPLICIT.Enum()

	if reply.GetSender() == "" {
		return replyContext{info: info, own: true}, nil
	}

	sender, err := user("reply_to.sender", reply.GetSender())
	if err != nil {
		return replyContext{}, err
	}

	info.Participant = proto.String(sender.ToNonAD().String())

	return replyContext{info: info}, nil
}

// named is the build of message, whose context is c, once a reply to the account's own message names the account. It
// reads the session's device only then, after the send's record, so a repeat of a recorded reply gets its record
// whatever the session's state.
func (svc *Service) named(sessionID string, c replyContext, message *waE2E.Message) build {
	if !c.own {
		return as(message)
	}

	return func(ctx context.Context, wa whatsApp, chat types.JID) (*waE2E.Message, error) {
		account, err := svc.account(ctx, sessionID, wa, chat)
		if err != nil {
			return nil, err
		}

		c.info.Participant = proto.String(account.String())

		return message, nil
	}
}

// account is the account's own JID without a device, as whatsmeow names the account when it sends to chat
// (SendMessage's ownID, send.go:304-365): its LID in a direct chat, by phone number or LID, since whatsmeow sends a
// phone number's message to its LID, and in a LID-addressed group, whose addressing mode it reads from WhatsApp as
// whatsmeow's send does (getCachedGroupData, group.go:700-711, which GetGroupInfo fills); its phone number in any other
// chat, and while the LID is unknown. whatsmeow's other LID case, a community announcement group, needs a send's Meta,
// which the sidecar never sets. Whether WhatsApp resolves the quote either way is unknown (#36).
func (svc *Service) account(ctx context.Context, sessionID string, wa whatsApp, chat types.JID) (types.JID, error) {
	device, err := svc.sessions.Device(sessionID)
	if err != nil {
		return types.JID{}, fmt.Errorf("reply: %w", err)
	}

	byLID := false

	switch chat.Server {
	case types.DefaultUserServer, types.HiddenUserServer, types.BotServer:
		byLID = true
	case types.GroupServer:
		info, err := wa.GetGroupInfo(ctx, chat)
		if err != nil {
			// As SendMessage's own read of the group fails: before the node, so nothing was sent (classify).
			_, reason := classify(err)

			return types.JID{}, fmt.Errorf("reply: read the group: %w", reason)
		}

		byLID = info.AddressingMode == types.AddressingModeLID
	}

	if byLID && !device.LID.IsEmpty() {
		return device.LID.ToNonAD(), nil
	}

	if device.ID == nil {
		return types.JID{}, fmt.Errorf("reply: session %q has no device JID", sessionID) //nolint:err113 // Device returns only paired devices
	}

	return device.ID.ToNonAD(), nil
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
