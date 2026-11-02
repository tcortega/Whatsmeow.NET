package session

import (
	"context"
	"path/filepath"
	"sync"
	"testing"
	"testing/synctest"
	"time"

	"github.com/rs/zerolog"
	"go.mau.fi/whatsmeow/store"

	whatsmeowv1 "github.com/tcortega/Whatsmeow.NET/sidecar/gen/whatsmeow/v1"
	"github.com/tcortega/Whatsmeow.NET/sidecar/internal/outbox"
	sidecarstore "github.com/tcortega/Whatsmeow.NET/sidecar/internal/store"
)

// redelivery is the slice on a real store inside a synctest bubble, whose fake clock the waits run on, with session
// s1 created: its stub records when each reset came, and withheld is the callback the slice gave its client.
type redelivery struct {
	t        *testing.T
	svc      *Service
	client   *stub
	withheld func()
	start    time.Time
}

func startRedelivery(t *testing.T) *redelivery {
	t.Helper()

	db, err := sidecarstore.Open(context.Background(), filepath.Join(t.TempDir(), "whatsmeow-net.db"), zerolog.Nop())
	if err != nil {
		t.Fatalf("open the store: %v", err)
	}

	r := &redelivery{t: t, start: time.Now()}

	var mu sync.Mutex

	svc, err := New(context.Background(), db.DB, db.Writer, db.Devices, outbox.New(db.DB, db.Writer), func(_ string, device *store.Device, withheld func()) Client {
		mu.Lock()
		defer mu.Unlock()

		r.client, r.withheld = &stub{device: device}, withheld

		return r.client
	}, zerolog.Nop())
	if err != nil {
		_ = db.Close()

		t.Fatalf("New: %v", err)
	}

	t.Cleanup(func() {
		svc.Close()

		if err := db.Close(); err != nil {
			t.Errorf("close the store: %v", err)
		}
	})

	r.svc = svc

	if _, err := svc.CreateSession(context.Background(), &whatsmeowv1.CreateSessionRequest{SessionId: "s1"}); err != nil {
		t.Fatalf("CreateSession: %v", err)
	}

	r.set(caughtUp())

	return r
}

// set puts s1 in state, as if it got there.
func (r *redelivery) set(state *whatsmeowv1.SessionState) {
	r.svc.mu.Lock()
	s := r.svc.sessions["s1"]
	r.svc.mu.Unlock()

	s.mu.Lock()
	s.state = state
	s.mu.Unlock()
}

// resets is when each reset of s1's connection came, after the bubble's start.
func (r *redelivery) resets() []time.Duration {
	r.client.mu.Lock()
	defer r.client.mu.Unlock()

	at := make([]time.Duration, 0, len(r.client.resetAt))
	for _, t := range r.client.resetAt {
		at = append(at, t.Sub(r.start))
	}

	return at
}

// after lets the fake clock run for d, then until every goroutine of the bubble waits.
func after(d time.Duration) {
	time.Sleep(d)
	synctest.Wait()
}

func TestAWithheldAnswerResetsTheConnectionOnceTheSettleDelayPassed(t *testing.T) {
	synctest.Test(t, func(t *testing.T) {
		r := startRedelivery(t)

		// A burst of failures is one reset: a store fault fails every write for a while.
		r.withheld()
		after(time.Second)

		for range 10 {
			r.withheld()
		}

		after(settle - time.Second - time.Millisecond)

		if got := r.resets(); len(got) != 0 {
			t.Fatalf("reset at %v, before the settle delay %v passed", got, settle)
		}

		after(time.Hour)

		if got := r.resets(); len(got) != 1 || got[0] != settle {
			t.Fatalf("resets at %v, want one at %v", got, settle)
		}
	})
}

func TestResetsWhileAnswersAreWithheldAgainAreSpacedByACoolDownThatDoublesUpToItsCap(t *testing.T) {
	synctest.Test(t, func(t *testing.T) {
		r := startRedelivery(t)
		stop := make(chan struct{})

		// Every redelivery fails again, as a persistent store fault makes it.
		go func() {
			for {
				r.withheld()

				select {
				case <-stop:
					return
				case <-time.After(time.Second):
				}
			}
		}()

		after(3 * time.Hour)
		close(stop)
		synctest.Wait()

		// After each reset the cool-down, 1 min doubling to 30 min, then the settle delay of the withheld answer
		// that came during it.
		want := []time.Duration{settle}
		for wait := coolDown; ; wait = min(2*wait, maxCoolDown) {
			next := want[len(want)-1] + wait + settle
			if next > 3*time.Hour {
				break
			}

			want = append(want, next)
		}

		if got := r.resets(); !equal(got, want) {
			t.Fatalf("resets at %v, want %v", got, want)
		}
	})
}

func TestTheCoolDownIsBackToItsBaseAfterOneWithNoWithheldAnswer(t *testing.T) {
	synctest.Test(t, func(t *testing.T) {
		r := startRedelivery(t)

		// Withheld again within the first cool-down: the second reset comes after it, and the cool-down doubles.
		r.withheld()
		after(settle + time.Second)
		r.withheld()
		after(time.Hour)

		// The doubled cool-down passed with nothing withheld: the next two resets are a base cool-down apart.
		at := time.Since(r.start)
		r.withheld()
		after(settle + time.Second)
		r.withheld()
		after(time.Hour)

		want := []time.Duration{settle, settle + coolDown + settle, at + settle, at + settle + coolDown + settle}
		if got := r.resets(); !equal(got, want) {
			t.Fatalf("resets at %v, want %v", got, want)
		}
	})
}

func TestASessionNotLoggedInToWhatsAppIsNeverReset(t *testing.T) {
	for name, state := range map[string]*whatsmeowv1.SessionState{
		"connecting":   connecting(),
		"reconnecting": reconnecting(),
		"disconnected": disconnected(),
		"pairing":      pairing(),
		"logged out":   loggedOut(401),
	} {
		t.Run(name, func(t *testing.T) {
			synctest.Test(t, func(t *testing.T) {
				r := startRedelivery(t)
				r.set(state)

				r.withheld()
				after(time.Hour)

				if got := r.resets(); len(got) != 0 {
					t.Fatalf("reset at %v while %v", got, state)
				}

				// Not reset, so no cool-down: once logged in, the next withheld answer resets after the settle delay.
				r.set(connected())
				at := time.Since(r.start)
				r.withheld()
				after(time.Hour)

				if got := r.resets(); len(got) != 1 || got[0] != at+settle {
					t.Fatalf("resets at %v, want one at %v", got, at+settle)
				}
			})
		})
	}
}

func TestADeletedSessionIsNeverReset(t *testing.T) {
	synctest.Test(t, func(t *testing.T) {
		r := startRedelivery(t)

		r.withheld()

		if _, err := r.svc.DeleteSession(context.Background(), &whatsmeowv1.DeleteSessionRequest{SessionId: "s1"}); err != nil {
			t.Fatalf("DeleteSession: %v", err)
		}

		r.withheld()
		after(time.Hour)

		if got := r.resets(); len(got) != 0 {
			t.Fatalf("a deleted session was reset at %v", got)
		}
	})
}

func TestWithheldNeverWaitsForTheRedelivery(t *testing.T) {
	synctest.Test(t, func(t *testing.T) {
		r := startRedelivery(t)

		// The redelivery is in its settle delay, then in a cool-down: it reads no wake for minutes. A handler's
		// withheld returns at once all the same, so the fake clock does not move while it runs.
		for _, wait := range []time.Duration{0, settle + time.Second} {
			after(wait)

			before := time.Now()

			for range 100 {
				r.withheld()
			}

			if waited := time.Since(before); waited != 0 {
				t.Fatalf("withheld waited %v for the redelivery", waited)
			}
		}
	})
}

func equal(a, b []time.Duration) bool {
	if len(a) != len(b) {
		return false
	}

	for i := range a {
		if a[i] != b[i] {
			return false
		}
	}

	return true
}
