package messaging

import (
	"errors"
	"fmt"
	"math/rand/v2"
	"time"
)

// Pacing is how fast one session's sends leave (ADR 0042): a burst of up to Burst at once, then one every Interval,
// each send that has to wait for its turn delayed by a random part of half an Interval more. Every session has its own
// budget. Safe rates are not published, so the defaults are conservative guesses (RESEARCH item 18).
type Pacing struct {
	Interval time.Duration
	Burst    int
}

// The defaults: a burst of 5, then one a second (ADR 0025).
const (
	DefaultInterval = time.Second
	DefaultBurst    = 5
)

// The bounds of a pacing: (MaxBurst-1)*MaxInterval stays far inside a time.Duration.
const (
	MaxInterval = time.Hour
	MaxBurst    = 1000
)

// Check is nil for a pacing within the bounds: an interval more than zero and at most MaxInterval, and a burst of 1 to
// MaxBurst.
func (p Pacing) Check() error {
	var errs []error

	if p.Interval <= 0 || p.Interval > MaxInterval {
		errs = append(errs, fmt.Errorf("the send interval %v must be more than zero and at most %v", p.Interval, MaxInterval))
	}

	if p.Burst < 1 || p.Burst > MaxBurst {
		errs = append(errs, fmt.Errorf("the send burst %d must be 1 to %d", p.Burst, MaxBurst))
	}

	return errors.Join(errs...)
}

// turn is how long the next send of q waits: until the bucket lets it go, and, when that is not at once, a random part
// of half an interval more, so the sends that wait do not leave on a fixed beat. The jitter only delays a send, so the
// bucket is never exceeded. With a burst above one it never moves the next send's turn either, since less than half an
// interval late is still before the bucket's own turn (spend); with a burst of one each wait adds its jitter.
func (svc *Service) turn(q *queue) time.Duration {
	d := svc.due(q)
	if d == 0 {
		return 0
	}

	return d + svc.jitter(svc.pace/2)
}

// jitter is a random duration from zero up to, not including, n; zero when n is not more than zero.
func jitter(n time.Duration) time.Duration {
	if n <= 0 {
		return 0
	}

	return rand.N(n) //nolint:gosec // G404: the jitter needs no secret randomness
}

// due is how long the next send of q has to wait for the bucket.
func (svc *Service) due(q *queue) time.Duration {
	svc.mu.Lock()
	defer svc.mu.Unlock()

	return max(0, q.tat.Add(-time.Duration(svc.burst-1)*svc.pace).Sub(svc.now()))
}

// spend takes the send's turn, now.
func (svc *Service) spend(q *queue) {
	svc.mu.Lock()
	defer svc.mu.Unlock()

	now := svc.now()
	if q.tat.Before(now) {
		q.tat = now
	}

	q.tat = q.tat.Add(svc.pace)
}
