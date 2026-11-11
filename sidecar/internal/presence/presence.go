// Package presence is the Presence slice: the account's own presence and typing state, and the subscription to a
// user's presence (presence.proto). What WhatsApp sends back is ephemeral, streamed by the Events slice.
package presence

import (
	"context"
	"errors"
	"fmt"

	"go.mau.fi/whatsmeow"
	"go.mau.fi/whatsmeow/socket"
	"go.mau.fi/whatsmeow/types"

	whatsmeowv1 "github.com/tcortega/Whatsmeow.NET/sidecar/gen/whatsmeow/v1"
	"github.com/tcortega/Whatsmeow.NET/sidecar/internal/server"
	"github.com/tcortega/Whatsmeow.NET/sidecar/internal/session"
)

// Client is what the slice needs of a whatsmeow client. *whatsmeow.Client and the fake WhatsApp's client both
// satisfy it (ADR 0019).
type Client interface {
	SendPresence(ctx context.Context, state types.Presence) error
	SubscribePresence(ctx context.Context, jid types.JID) error
	SendChatPresence(ctx context.Context, jid types.JID, state types.ChatPresence, media types.ChatPresenceMedia) error
}

var _ Client = (*whatsmeow.Client)(nil)

// Sessions is what the slice needs of the Sessions slice: the client of a session that is online.
type Sessions interface {
	Online(id string) (session.Client, <-chan struct{}, error)
}

// Service implements whatsmeowv1.PresenceServiceServer.
type Service struct {
	whatsmeowv1.UnimplementedPresenceServiceServer

	sessions Sessions
}

// New returns the slice on the sessions.
func New(sessions Sessions) *Service { return &Service{sessions: sessions} }

// SendPresence sets the account's presence (presence.proto).
func (svc *Service) SendPresence(ctx context.Context, req *whatsmeowv1.SendPresenceRequest) (*whatsmeowv1.SendPresenceResponse, error) {
	var state types.Presence

	switch req.GetState() {
	case whatsmeowv1.PresenceState_PRESENCE_STATE_AVAILABLE:
		state = types.PresenceAvailable
	case whatsmeowv1.PresenceState_PRESENCE_STATE_UNAVAILABLE:
		state = types.PresenceUnavailable
	case whatsmeowv1.PresenceState_PRESENCE_STATE_UNSPECIFIED:
		fallthrough
	default:
		return nil, fmt.Errorf("send presence: %w", server.InvalidArgument("state", "%v is not a presence", req.GetState()))
	}

	client, err := svc.client(req.GetSessionId())
	if err != nil {
		return nil, fmt.Errorf("send presence: %w", err)
	}

	if err := client.SendPresence(ctx, state); err != nil {
		return nil, sendFailed("send presence", req.GetSessionId(), err)
	}

	return &whatsmeowv1.SendPresenceResponse{}, nil
}

// SubscribePresence asks WhatsApp for the user's presence (presence.proto).
func (svc *Service) SubscribePresence(ctx context.Context, req *whatsmeowv1.SubscribePresenceRequest) (*whatsmeowv1.SubscribePresenceResponse, error) {
	user, err := parseJID("user", req.GetUser())
	if err != nil {
		return nil, fmt.Errorf("subscribe presence: %w", err)
	}

	client, err := svc.client(req.GetSessionId())
	if err != nil {
		return nil, fmt.Errorf("subscribe presence: %w", err)
	}

	if err := client.SubscribePresence(ctx, user); err != nil {
		return nil, sendFailed("subscribe presence", req.GetSessionId(), err)
	}

	return &whatsmeowv1.SubscribePresenceResponse{}, nil
}

// SendChatPresence sets the account's state in a chat (presence.proto): typing is composing, recording is composing
// audio, paused stops either (presence.go:130-144).
func (svc *Service) SendChatPresence(ctx context.Context, req *whatsmeowv1.SendChatPresenceRequest) (*whatsmeowv1.SendChatPresenceResponse, error) {
	var (
		state types.ChatPresence
		media types.ChatPresenceMedia
	)

	switch req.GetState() {
	case whatsmeowv1.ChatPresenceState_CHAT_PRESENCE_STATE_TYPING:
		state = types.ChatPresenceComposing
	case whatsmeowv1.ChatPresenceState_CHAT_PRESENCE_STATE_RECORDING:
		state, media = types.ChatPresenceComposing, types.ChatPresenceMediaAudio
	case whatsmeowv1.ChatPresenceState_CHAT_PRESENCE_STATE_PAUSED:
		state = types.ChatPresencePaused
	case whatsmeowv1.ChatPresenceState_CHAT_PRESENCE_STATE_UNSPECIFIED:
		fallthrough
	default:
		return nil, fmt.Errorf("send chat presence: %w", server.InvalidArgument("state", "%v is not a chat presence", req.GetState()))
	}

	chat, err := parseJID("chat", req.GetChat())
	if err != nil {
		return nil, fmt.Errorf("send chat presence: %w", err)
	}

	client, err := svc.client(req.GetSessionId())
	if err != nil {
		return nil, fmt.Errorf("send chat presence: %w", err)
	}

	if err := client.SendChatPresence(ctx, chat, state, media); err != nil {
		return nil, sendFailed("send chat presence", req.GetSessionId(), err)
	}

	return &whatsmeowv1.SendChatPresenceResponse{}, nil
}

// client is the online session's client, as this slice's interface.
func (svc *Service) client(id string) (Client, error) {
	online, _, err := svc.sessions.Online(id)
	if err != nil {
		return nil, fmt.Errorf("online: %w", err)
	}

	client, ok := online.(Client)
	if !ok {
		return nil, fmt.Errorf("session %q: its client %T cannot send presence", id, online)
	}

	return client, nil
}

// parseJID parses a JID in whatsmeow's string form that has a user and a server.
// Its text names the field, never the JID, which names a person (ADR 0039).
func parseJID(field, value string) (types.JID, error) {
	jid, err := types.ParseJID(value)
	if err != nil || jid.User == "" || jid.Server == "" {
		return types.JID{}, fmt.Errorf("parse the %s: %w", field, server.InvalidArgument(field, "not a JID"))
	}

	return jid, nil
}

// sendFailed is the reason a stanza whatsmeow was handed did not go out.
func sendFailed(what, id string, err error) error {
	switch {
	case errors.Is(err, whatsmeow.ErrNotConnected), errors.Is(err, socket.ErrSocketClosed):
		return fmt.Errorf("%s for session %q: %w: %w", what, id, server.ErrNotConnected, err)
	case errors.Is(err, whatsmeow.ErrNoPushName):
		return fmt.Errorf("%s for session %q: %w: %w", what, id, server.ErrNoPushName, err)
	case errors.Is(err, whatsmeow.ErrNotLoggedIn):
		// Logged out, not never paired: the session was online, so its device was deleted meanwhile (ADR 0025).
		return fmt.Errorf("%s for session %q: %w: %w", what, id, server.ErrLoggedOut, err)
	default:
		return fmt.Errorf("%s for session %q: %w", what, id, err)
	}
}
