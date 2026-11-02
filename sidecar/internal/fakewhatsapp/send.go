//go:build fakewhatsapp

package fakewhatsapp

import (
	"context"
	"errors"
	"fmt"
	"time"

	"go.mau.fi/whatsmeow"
	"go.mau.fi/whatsmeow/proto/waE2E"
	"go.mau.fi/whatsmeow/types"
	"google.golang.org/protobuf/proto"
	"google.golang.org/protobuf/types/known/timestamppb"

	fakev1 "github.com/tcortega/Whatsmeow.NET/sidecar/gen/whatsmeow/fake/v1"
)

// retryWait is how long whatsmeow's retryFrame waits for a reconnect after the connection of a send in flight ended
// (request.go:199).
const retryWait = 5 * time.Second

// SendMessage sends a message as whatsmeow's does at the pin, in its order (send.go:191-489): it refuses a device and
// a client without a device ID, sends a phone number's message to its LID (lidForPN) or fails with ErrRecipientNotOnWhatsApp,
// fails a rate-limited IQ, marshals the message (so an unset required field fails), and writes the node, which the
// log shows. WhatsApp then answers with its timestamp in whole seconds, unless HoldNextSend held it.
func (c *Client) SendMessage(ctx context.Context, to types.JID, message *waE2E.Message, extra ...whatsmeow.SendRequestExtra) (whatsmeow.SendResponse, error) {
	var req whatsmeow.SendRequestExtra
	if len(extra) == 1 {
		req = extra[0]
	}

	switch {
	case to.Device > 0:
		return whatsmeow.SendResponse{}, whatsmeow.ErrRecipientADJID
	case c.Store.ID == nil:
		return whatsmeow.SendResponse{}, whatsmeow.ErrNotLoggedIn
	case req.ID == "":
		return whatsmeow.SendResponse{}, errors.New("fake WhatsApp: a send without an id; the sidecar always gives one") //nolint:err113 // a misuse of the fake
	}

	// A peer message goes to the account itself, by its phone number (send.go:341).
	if to.Server == types.DefaultUserServer && !req.Peer {
		lid, err := c.lidForPN(ctx, to)
		if err != nil {
			return whatsmeow.SendResponse{}, err
		}

		to = lid
	}

	if c.whatsApp.take(c.sessionID, func(s *script) bool { return s.rateLimitSends > 0 }, func(s *script) { s.rateLimitSends-- }) {
		return whatsmeow.SendResponse{}, fmt.Errorf("failed to get devices: %w", whatsmeow.ErrIQRateOverLimit)
	}

	if _, err := proto.Marshal(message); err != nil {
		return whatsmeow.SendResponse{}, fmt.Errorf("failed to marshal message: %w", err)
	}

	var (
		held    chan struct{}
		refusal int32
	)

	c.whatsApp.take(c.sessionID, func(s *script) bool { return s.holdSend }, func(s *script) { s.holdSend, held = false, s.held })

	c.conn.Lock()
	connected, connection := c.connected, c.connection
	c.conn.Unlock()

	if !connected {
		return whatsmeow.SendResponse{}, fmt.Errorf("failed to send message node: %w", whatsmeow.ErrNotConnected)
	}

	at := time.Now().Truncate(time.Second)
	c.whatsApp.append(&fakev1.Sent{SessionId: c.sessionID, Kind: &fakev1.Sent_Message{Message: &fakev1.SentMessage{
		Id: req.ID, Chat: to.String(), Message: message, MessageTime: timestamppb.New(at),
	}}})

	if c.whatsApp.take(c.sessionID, func(s *script) bool { return s.refuseSend != 0 }, func(s *script) { refusal, s.refuseSend = s.refuseSend, 0 }) {
		// An answer with an error attribute, as whatsmeow reads it (send.go:468-470).
		return whatsmeow.SendResponse{ID: req.ID, Timestamp: at, Chat: to}, fmt.Errorf("%w %d", whatsmeow.ErrServerReturnedError, refusal)
	}

	if held != nil {
		// Once it ended, however it ended, nothing is held any more.
		defer c.whatsApp.setScript(c.sessionID, func(s *script) {
			if s.held == held {
				s.held = nil
			}
		})

		select {
		case <-held:
		case <-ctx.Done():
			return whatsmeow.SendResponse{}, ctx.Err() //nolint:wrapcheck // as whatsmeow returns it (send.go:451-454)
		case <-connection:
			// whatsmeow's response waiters get the stream end (client.go:618, 720, 730), and retryFrame waits for a
			// reconnect before it gives up (request.go:198-201); the fake never reconnects on its own.
			select {
			case <-time.After(retryWait):
			case <-ctx.Done():
				return whatsmeow.SendResponse{}, ctx.Err() //nolint:wrapcheck // as whatsmeow returns it (request.go:224-225)
			}

			return whatsmeow.SendResponse{}, &whatsmeow.DisconnectedError{Action: "message send"}
		}
	}

	return whatsmeow.SendResponse{ID: req.ID, Timestamp: at, Chat: to}, nil
}

// MarkRead sends a read receipt as whatsmeow's does (receipt.go:197-237): the participant only outside a direct chat.
func (c *Client) MarkRead(_ context.Context, ids []types.MessageID, _ time.Time, chat, sender types.JID, _ ...types.ReceiptType) error {
	if len(ids) == 0 {
		return errors.New("no message IDs specified") //nolint:err113 // whatsmeow's own error text (receipt.go:198-200)
	}

	c.conn.Lock()
	connected := c.connected
	c.conn.Unlock()

	if !connected {
		return whatsmeow.ErrNotConnected
	}

	read := &fakev1.ReadReceipt{Chat: chat.String(), MessageIds: ids}
	if !sender.IsEmpty() && chat.Server != types.DefaultUserServer && chat.Server != types.HiddenUserServer && chat.Server != types.MessengerServer {
		read.Participant = sender.ToNonAD().String()
	}

	c.whatsApp.append(&fakev1.Sent{SessionId: c.sessionID, Kind: &fakev1.Sent_Read{Read: read}})

	return nil
}
