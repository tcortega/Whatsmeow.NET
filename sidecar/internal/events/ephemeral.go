package events

import (
	"fmt"

	"go.mau.fi/whatsmeow/types"
	waevents "go.mau.fi/whatsmeow/types/events"
	"google.golang.org/grpc"
	"google.golang.org/grpc/metadata"
	"google.golang.org/protobuf/types/known/timestamppb"

	whatsmeowv1 "github.com/tcortega/Whatsmeow.NET/sidecar/gen/whatsmeow/v1"
	"github.com/tcortega/Whatsmeow.NET/sidecar/internal/server"
)

// toEphemeral maps a whatsmeow event to the ephemeral event it streams, or false for one that is not ephemeral.
// Presence and chat presence are not gated: whatsmeow dispatches them on the node queue and answers nothing whatever
// the handlers return (presence.go:18-58). It is pure: no I/O.
func toEphemeral(evt any) (*whatsmeowv1.StreamEphemeralEventsResponse, bool) {
	switch e := evt.(type) {
	case *waevents.Presence:
		event := &whatsmeowv1.PresenceEvent{From: server.Text(e.From.String()), State: whatsmeowv1.PresenceState_PRESENCE_STATE_AVAILABLE}
		if e.Unavailable {
			event.State = whatsmeowv1.PresenceState_PRESENCE_STATE_UNAVAILABLE
		}

		// Zero when the stanza has no last seen, or "deny" (presence.go:49-52).
		if !e.LastSeen.IsZero() {
			event.LastSeen = timestamppb.New(e.LastSeen)
		}

		return &whatsmeowv1.StreamEphemeralEventsResponse{Payload: &whatsmeowv1.StreamEphemeralEventsResponse_Presence{Presence: event}}, true
	case *waevents.ChatPresence:
		return &whatsmeowv1.StreamEphemeralEventsResponse{Payload: &whatsmeowv1.StreamEphemeralEventsResponse_ChatPresence{
			ChatPresence: &whatsmeowv1.ChatPresenceEvent{Source: source(&e.MessageSource), State: chatState(e.State, e.Media)},
		}}, true
	default:
		return nil, false
	}
}

// chatState is the state of a chat state stanza; one whatsmeow does not know is unspecified (presence.go:26-35).
func chatState(state types.ChatPresence, media types.ChatPresenceMedia) whatsmeowv1.ChatPresenceState {
	switch {
	case state == types.ChatPresenceComposing && media == types.ChatPresenceMediaText:
		return whatsmeowv1.ChatPresenceState_CHAT_PRESENCE_STATE_TYPING
	case state == types.ChatPresenceComposing && media == types.ChatPresenceMediaAudio:
		return whatsmeowv1.ChatPresenceState_CHAT_PRESENCE_STATE_RECORDING
	case state == types.ChatPresencePaused:
		return whatsmeowv1.ChatPresenceState_CHAT_PRESENCE_STATE_PAUSED
	default:
		return whatsmeowv1.ChatPresenceState_CHAT_PRESENCE_STATE_UNSPECIFIED
	}
}

// ring returns the session's ring, making it for the first handler or stream. A ring of a deleted session is
// replaced, so a session created again with the same id never gets the old one's events.
func (svc *Service) ring(sessionID string) *ring {
	svc.mu.Lock()
	defer svc.mu.Unlock()

	if r, ok := svc.rings[sessionID]; ok && !r.gone() {
		return r
	}

	r := newRing(svc.box.Deleted(sessionID))
	svc.rings[sessionID] = r

	return r
}

// StreamEphemeralEvents sends the session's ephemeral events, best effort, until the client cancels or the session is
// deleted (events.proto). The headers go out once the stream is placed in the ring.
func (svc *Service) StreamEphemeralEvents(req *whatsmeowv1.StreamEphemeralEventsRequest, stream grpc.ServerStreamingServer[whatsmeowv1.StreamEphemeralEventsResponse]) error {
	id := req.GetSessionId()
	if err := server.CheckID("session_id", id); err != nil {
		return fmt.Errorf("session id: %w", err)
	}

	// Taken first: a session deleted from here on ends the stream.
	watch, _, err := svc.box.Watch(stream.Context(), id)
	if err != nil {
		return fmt.Errorf("stream the ephemeral events of session %q: %w", id, err)
	}

	ctx := stream.Context()
	r := svc.ring(id)
	next := r.start()

	if err := stream.SendHeader(metadata.MD{}); err != nil {
		return fmt.Errorf("stream the ephemeral events of session %q: %w", id, err)
	}

	for {
		dropped, events, after, changed := r.read(next)
		if dropped > 0 {
			overflow := &whatsmeowv1.StreamEphemeralEventsResponse{Payload: &whatsmeowv1.StreamEphemeralEventsResponse_QueueOverflow{
				QueueOverflow: &whatsmeowv1.QueueOverflowEvent{Dropped: dropped},
			}}
			// Counted as got at hand-off, before the send: a stream opened once the client read it starts after it.
			next += dropped
			r.got(next)

			if err := sendOrEnd(ctx, stream, overflow); err != nil {
				return fmt.Errorf("stream the ephemeral events of session %q: %w", id, err)
			}
		}

		for _, event := range events {
			next++
			r.got(next)

			if err := sendOrEnd(ctx, stream, event); err != nil {
				return fmt.Errorf("stream the ephemeral events of session %q: %w", id, err)
			}
		}

		next = after

		select {
		case <-ctx.Done():
			return fmt.Errorf("stream the ephemeral events of session %q: %w", id, ctx.Err())
		case <-watch.Deleted():
			return fmt.Errorf("session %q was deleted: %w", id, server.ErrSessionNotFound)
		case <-changed:
		}
	}
}
