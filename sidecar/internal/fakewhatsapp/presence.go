//go:build fakewhatsapp

package fakewhatsapp

import (
	"context"
	"fmt"

	"go.mau.fi/whatsmeow"
	"go.mau.fi/whatsmeow/types"
	"go.mau.fi/whatsmeow/types/events"

	fakev1 "github.com/tcortega/Whatsmeow.NET/sidecar/gen/whatsmeow/fake/v1"
)

// SendPresence sends the account's presence with its push name, and refuses without one, as whatsmeow's does
// (presence.go:64-87). Available turns active receipts on, unavailable off, before the send, whether it goes out or
// not (presence.go:70-75).
func (c *Client) SendPresence(_ context.Context, state types.Presence) error {
	c.conn.Lock()
	defer c.conn.Unlock()

	if c.Store.PushName == "" {
		return whatsmeow.ErrNoPushName
	}

	c.activeReceipts = state == types.PresenceAvailable

	if !c.connected {
		return whatsmeow.ErrNotConnected
	}

	c.whatsApp.append(&fakev1.Sent{SessionId: c.sessionID, Kind: &fakev1.Sent_Presence{Presence: &fakev1.Presence{Type: string(state), Name: c.Store.PushName}}})

	return nil
}

// SubscribePresence sends the subscription, as whatsmeow's does; it needs the device's own JID (tctoken.go:121-122).
// The fake keeps no privacy tokens, which whatsmeow sends without by default (presence.go:104-110).
func (c *Client) SubscribePresence(_ context.Context, jid types.JID) error {
	c.conn.Lock()
	defer c.conn.Unlock()

	if c.Store.ID == nil {
		return fmt.Errorf("failed to get privacy token: %w", whatsmeow.ErrNotLoggedIn)
	}

	if !c.connected {
		return whatsmeow.ErrNotConnected
	}

	c.whatsApp.append(&fakev1.Sent{SessionId: c.sessionID, Kind: &fakev1.Sent_SubscribePresence{SubscribePresence: &fakev1.SubscribePresence{To: jid.String()}}})

	return nil
}

// SendChatPresence sends the chat state, with the media only while composing, as whatsmeow's does
// (presence.go:130-144).
func (c *Client) SendChatPresence(_ context.Context, jid types.JID, state types.ChatPresence, media types.ChatPresenceMedia) error {
	c.conn.Lock()
	defer c.conn.Unlock()

	if !c.connected {
		return whatsmeow.ErrNotConnected
	}

	if state != types.ChatPresenceComposing {
		media = ""
	}

	c.whatsApp.append(&fakev1.Sent{SessionId: c.sessionID, Kind: &fakev1.Sent_ChatPresence{ChatPresence: &fakev1.ChatPresence{
		To: jid.String(), State: string(state), Media: string(media),
	}}})

	return nil
}

// receiptType is the type of the receipt whatsmeow answers a decrypted message with (sendMessageReceipt,
// receipt.go:280-296): sender for a message another of the account's devices sent; for one from someone else, none
// while active receipts are on, inactive otherwise. The one place the fake chooses it.
func (c *Client) receiptType(info *types.MessageInfo) string {
	if info.IsFromMe {
		return string(types.ReceiptTypeSender)
	}

	c.conn.Lock()
	defer c.conn.Unlock()

	if c.activeReceipts {
		return ""
	}

	return string(types.ReceiptTypeInactive)
}

// DeliverPresence dispatches a user's presence to the session's client on its queue.
func (c *Control) DeliverPresence(_ context.Context, req *fakev1.DeliverPresenceRequest) (*fakev1.DeliverPresenceResponse, error) {
	client, err := c.whatsApp.client(req.GetSessionId())
	if err != nil {
		return nil, fmt.Errorf("deliver presence: %w", err)
	}

	from, err := types.ParseJID(req.GetFrom())
	if err != nil {
		return nil, fmt.Errorf("deliver presence: jid %q: %w", req.GetFrom(), err)
	}

	client.dispatch(&events.Presence{From: from, Unavailable: req.GetUnavailable(), LastSeen: timeOf(req.GetLastSeen())})

	return &fakev1.DeliverPresenceResponse{}, nil
}

// DeliverChatPresence dispatches a user's chat state to the session's client on its queue.
func (c *Control) DeliverChatPresence(_ context.Context, req *fakev1.DeliverChatPresenceRequest) (*fakev1.DeliverChatPresenceResponse, error) {
	client, err := c.whatsApp.client(req.GetSessionId())
	if err != nil {
		return nil, fmt.Errorf("deliver chat presence: %w", err)
	}

	source, err := messageSource(req.GetChat(), req.GetSender(), "", "")
	if err != nil {
		return nil, fmt.Errorf("deliver chat presence: %w", err)
	}

	client.dispatch(&events.ChatPresence{MessageSource: source, State: types.ChatPresence(req.GetState()), Media: types.ChatPresenceMedia(req.GetMedia())})

	return &fakev1.DeliverChatPresenceResponse{}, nil
}

// SetPushName sets the device's push name and saves the device, as whatsmeow's app state sync does
// (appstate.go:364-374).
func (c *Control) SetPushName(ctx context.Context, req *fakev1.SetPushNameRequest) (*fakev1.SetPushNameResponse, error) {
	client, err := c.whatsApp.client(req.GetSessionId())
	if err != nil {
		return nil, fmt.Errorf("set the push name: %w", err)
	}

	client.conn.Lock()
	client.Store.PushName = req.GetName()
	client.conn.Unlock()

	if err := client.Store.Save(ctx); err != nil {
		return nil, fmt.Errorf("set the push name of session %q: save the device: %w", req.GetSessionId(), err)
	}

	return &fakev1.SetPushNameResponse{}, nil
}
