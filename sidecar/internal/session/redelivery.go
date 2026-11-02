package session

import "time"

// A session's redelivery (ADR 0044). WhatsApp delivers a stanza whose answer was withheld again at the device's next
// login, and was never seen to while the connection stayed up (ARCHITECTURE §14, #10), so after a withheld answer
// the sidecar resets the connection: whatsmeow's ResetConnection closes it and logs in again at once
// (client.go:612-631, 653-660, 712-724).
const (
	// settle is how long after a withheld answer the connection is reset, so that a burst of failed writes, as a
	// store fault makes, is one reset.
	settle = 5 * time.Second
	// coolDown is the least time between two resets. It doubles each time an answer is withheld again within it,
	// up to maxCoolDown, so a failure that never clears resets rarely, and is back to coolDown after one with none.
	coolDown    = time.Minute
	maxCoolDown = 30 * time.Minute
	// resetWait bounds how long a reset holds the session for whatsmeow's Disconnected; it comes as soon as the
	// stopped socket's read loop ends [inferred: client.go:612-631, unmeasured].
	resetWait = 10 * time.Second
)

// withheld wakes the session's redelivery, without waiting: an event handler of its client withheld an answer to
// WhatsApp. A wake already pending absorbs this one.
func (s *session) withheld() {
	select {
	case s.withholds <- struct{}{}:
	default:
	}
}

// redeliver is the session's redelivery, from its creation until its deletion or the sidecar's end: once a settle
// delay passed after a withheld answer, it resets the connection, then waits out the cool-down.
func (svc *Service) redeliver(s *session) {
	wait := coolDown

	for {
		select {
		case <-svc.lifetime.Done():
			return
		case <-s.deleted:
			return
		case <-s.withholds:
		}

		if !svc.sleep(s, settle) {
			return
		}

		// The answers withheld during the settle delay are this reset's.
		select {
		case <-s.withholds:
		default:
		}

		if !svc.reset(s) {
			continue
		}

		if !svc.sleep(s, wait) {
			return
		}

		wait = cooled(wait, len(s.withholds) > 0)
	}
}

// cooled is the cool-down after one of wait: doubled, up to maxCoolDown, when an answer was withheld again within it,
// otherwise coolDown.
func cooled(wait time.Duration, again bool) time.Duration {
	if !again {
		return coolDown
	}

	return min(2*wait, maxCoolDown)
}

// sleep waits d, and reports false when the session was deleted or the sidecar ends first.
func (svc *Service) sleep(s *session, d time.Duration) bool {
	timer := time.NewTimer(d)
	defer timer.Stop()

	select {
	case <-svc.lifetime.Done():
		return false
	case <-s.deleted:
		return false
	case <-timer.C:
		return true
	}
}

// reset resets the connection of a session logged in to WhatsApp, Connected or CaughtUp, and reports whether it did.
// A session in any other state is not: one Connecting or Reconnecting logs in soon, and WhatsApp delivers again then;
// one disconnected does at its next Connect. It holds s.ops until the session left Connected and CaughtUp, at most
// resetWait: ResetConnection returns once it stopped the socket, and whatsmeow clears the socket and dispatches
// Disconnected later, on its own goroutine (client.go:612-631, 712-724), so a Logout that ran in between would send
// its unlink on the stopped socket. After it, a call finds the session Reconnecting.
func (svc *Service) reset(s *session) bool {
	if err := s.lock(); err != nil {
		return false
	}
	defer s.ops.Unlock()

	s.mu.Lock()
	client, online := s.client, up(s.state)

	// Closed once the session leaves Connected and CaughtUp (track).
	if online && s.online == nil {
		s.online = make(chan struct{})
	}

	left := s.online
	s.mu.Unlock()

	if !online || svc.lifetime.Err() != nil {
		return false
	}

	svc.log.Warn().Str("session_id", s.id).Msg("an answer to WhatsApp was withheld; resetting the connection, so WhatsApp delivers it again at the login")
	client.ResetConnection()

	timer := time.NewTimer(resetWait)
	defer timer.Stop()

	select {
	case <-left:
	case <-svc.lifetime.Done():
	case <-timer.C:
		svc.log.Warn().Str("session_id", s.id).Msgf("no Disconnected came within %s of the reset; the session's calls go on", resetWait)
	}

	return true
}
