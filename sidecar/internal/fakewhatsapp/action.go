//go:build fakewhatsapp

package fakewhatsapp

import (
	"time"

	"go.mau.fi/whatsmeow/proto/waCommon"
	"go.mau.fi/whatsmeow/proto/waE2E"
	"go.mau.fi/whatsmeow/types"
)

// messageKey names a message as whatsmeow's BuildMessageKey does at the pin (send.go:511-523): the account's own when
// the sender is empty or the account by its JID or LID; another's, with the sender as participant outside a direct
// chat.
func (c *Client) messageKey(chat, sender types.JID, id types.MessageID) *waCommon.MessageKey {
	key := &waCommon.MessageKey{FromMe: new(true), ID: new(id), RemoteJID: new(chat.String())}

	self, lid := c.self(), c.Store.GetLID()
	if !sender.IsEmpty() && sender.User != self.User && sender.User != lid.User {
		key.FromMe = new(false)
		if chat.Server != types.DefaultUserServer && chat.Server != types.HiddenUserServer && chat.Server != types.MessengerServer {
			key.Participant = new(sender.ToNonAD().String())
		}
	}

	return key
}

// BuildReaction builds a reaction as whatsmeow's does (send.go:551-561).
func (c *Client) BuildReaction(chat, sender types.JID, id types.MessageID, reaction string) *waE2E.Message {
	return &waE2E.Message{ReactionMessage: &waE2E.ReactionMessage{
		Key: c.messageKey(chat, sender, id), Text: new(reaction), SenderTimestampMS: new(time.Now().UnixMilli()),
	}}
}

// BuildRevoke builds a revoke as whatsmeow's does (send.go:536-544).
func (c *Client) BuildRevoke(chat, sender types.JID, id types.MessageID) *waE2E.Message {
	return &waE2E.Message{ProtocolMessage: &waE2E.ProtocolMessage{Type: waE2E.ProtocolMessage_REVOKE.Enum(), Key: c.messageKey(chat, sender, id)}}
}

// BuildEdit builds an edit as whatsmeow's does (send.go:616-635).
func (c *Client) BuildEdit(chat types.JID, id types.MessageID, newContent *waE2E.Message) *waE2E.Message {
	return &waE2E.Message{EditedMessage: &waE2E.FutureProofMessage{Message: &waE2E.Message{ProtocolMessage: &waE2E.ProtocolMessage{
		Key:  &waCommon.MessageKey{FromMe: new(true), ID: new(id), RemoteJID: new(chat.String())},
		Type: waE2E.ProtocolMessage_MESSAGE_EDIT.Enum(), EditedMessage: newContent, TimestampMS: new(time.Now().UnixMilli()),
	}}}}
}
