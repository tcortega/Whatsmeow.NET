package session

import (
	"fmt"
	"time"

	"go.mau.fi/whatsmeow/store"

	whatsmeowv1 "github.com/tcortega/Whatsmeow.NET/sidecar/gen/whatsmeow/v1"
	"github.com/tcortega/Whatsmeow.NET/sidecar/internal/server"
)

// Online returns the client of a session that is logged in to WhatsApp right now (Connected or CaughtUp), for another
// slice's call to WhatsApp, and a channel closed once the session leaves those states or is deleted. A call never
// waits for a connection (ARCHITECTURE §7.5): a session in any other state fails at once with its reason,
// NOT_LOGGED_IN, LOGGED_OUT, TEMPORARILY_BANNED (with its end when known) or NOT_CONNECTED.
func (svc *Service) Online(id string) (Client, <-chan struct{}, error) {
	s, err := svc.get(id)
	if err != nil {
		return nil, nil, err
	}

	return s.clientOnline()
}

// clientOnline is Online for a session get found. DeleteSession may have run since: its channel is closed, and one made
// now would never close, so a deleted session fails as get does.
func (s *session) clientOnline() (Client, <-chan struct{}, error) {
	s.mu.Lock()
	defer s.mu.Unlock()

	if s.gone() {
		return nil, nil, fmt.Errorf("session %q: %w", s.id, server.ErrSessionNotFound)
	}

	if up(s.state) {
		if s.online == nil {
			s.online = make(chan struct{})
		}

		return s.client, s.online, nil
	}

	switch kind := s.state.GetKind().(type) {
	case *whatsmeowv1.SessionState_LoggedOut_:
		return nil, nil, fmt.Errorf("session %q: %w", s.id, server.ErrLoggedOut)
	case *whatsmeowv1.SessionState_TempBanned_:
		if expire := kind.TempBanned.GetExpireTime(); expire != nil {
			end := expire.AsTime().UTC().Format(time.RFC3339)

			return nil, nil, fmt.Errorf("session %q until %s: %w", s.id, end, server.WithMetadata(server.ErrTemporarilyBanned, "expireTime", end))
		}

		return nil, nil, fmt.Errorf("session %q: %w", s.id, server.ErrTemporarilyBanned)
	}

	if s.jid == "" {
		return nil, nil, fmt.Errorf("session %q: %w", s.id, server.ErrNotLoggedIn)
	}

	return nil, nil, fmt.Errorf("session %q: %w", s.id, server.ErrNotConnected)
}

// Up returns a channel closed once the session is logged in to WhatsApp (Connected or CaughtUp), already closed when it
// is now, and closed when it is deleted: for another slice that waits for the session to come online, on a bound of
// its own, then calls Online.
func (svc *Service) Up(id string) (<-chan struct{}, error) {
	s, err := svc.get(id)
	if err != nil {
		return nil, err
	}

	s.mu.Lock()
	defer s.mu.Unlock()

	if s.gone() {
		return nil, fmt.Errorf("session %q: %w", s.id, server.ErrSessionNotFound)
	}

	if up(s.state) {
		now := make(chan struct{})
		close(now)

		return now, nil
	}

	if s.coming == nil {
		s.coming = make(chan struct{})
	}

	return s.coming, nil
}

// Device returns the device of a paired session, in any state, for another slice's read of what its store learned;
// it needs no connection. A session without a device fails with NOT_LOGGED_IN, or LOGGED_OUT after a logout.
func (svc *Service) Device(id string) (*store.Device, error) {
	s, err := svc.get(id)
	if err != nil {
		return nil, err
	}

	s.mu.Lock()
	defer s.mu.Unlock()

	if s.jid != "" {
		return s.device, nil
	}

	if is[*whatsmeowv1.SessionState_LoggedOut_](s.state) {
		return nil, fmt.Errorf("session %q: %w", s.id, server.ErrLoggedOut)
	}

	return nil, fmt.Errorf("session %q: %w", s.id, server.ErrNotLoggedIn)
}

// up is a state logged in to WhatsApp right now: Connected or CaughtUp.
func up(state *whatsmeowv1.SessionState) bool {
	return is[*whatsmeowv1.SessionState_Connected_](state) || is[*whatsmeowv1.SessionState_CaughtUp_](state)
}

// track closes and clears the channel Online handed out once the session leaves Connected and CaughtUp, and the one
// Up handed out once it gets there; the next call makes a new one. s.mu is held.
func (s *session) track(state *whatsmeowv1.SessionState) {
	if !up(state) && s.online != nil {
		close(s.online)
		s.online = nil
	}

	if up(state) && s.coming != nil {
		close(s.coming)
		s.coming = nil
	}
}
