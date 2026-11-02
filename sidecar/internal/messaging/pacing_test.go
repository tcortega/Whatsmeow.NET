package messaging

import (
	"context"
	"errors"
	"fmt"
	"strings"
	"sync"
	"testing"
	"time"

	"github.com/rs/zerolog"
	"go.mau.fi/whatsmeow"
	waBinary "go.mau.fi/whatsmeow/binary"
	"go.mau.fi/whatsmeow/proto/waE2E"
	"google.golang.org/grpc/codes"
	"google.golang.org/protobuf/proto"

	whatsmeowv1 "github.com/tcortega/Whatsmeow.NET/sidecar/gen/whatsmeow/v1"
	"github.com/tcortega/Whatsmeow.NET/sidecar/internal/server"
)

// clock is the queues' fake clock: each wait the slice asks for comes on waits, and passes only when the test passes it.
type clock struct {
	mu    sync.Mutex
	at    time.Time
	waits chan wait
}

// wait is one wait the slice asked the clock for.
type wait struct {
	d       time.Duration
	elapsed chan time.Time
}

func (c *clock) now() time.Time {
	c.mu.Lock()
	defer c.mu.Unlock()

	return c.at
}

func (c *clock) after(d time.Duration) <-chan time.Time {
	w := wait{d: d, elapsed: make(chan time.Time, 1)}
	c.waits <- w

	return w.elapsed
}

// pass moves the clock past w and ends it.
func (c *clock) pass(w wait) {
	c.mu.Lock()
	c.at = c.at.Add(w.d)
	at := c.at
	c.mu.Unlock()

	w.elapsed <- at
}

// fakeClock gives the slice a fake clock and a jitter of the most it may be, just under the half interval it is given,
// so a wait shows both.
func (h *harness) fakeClock() *clock {
	c := &clock{at: time.Unix(1_800_000_000, 0), waits: make(chan wait, 64)}
	h.svc.now, h.svc.after = c.now, c.after
	h.svc.jitter = func(n time.Duration) time.Duration { return n - time.Nanosecond }

	return c
}

// nextWait is the next wait the slice asks for; it fails if done comes first, a send that went out without waiting.
func (h *harness) nextWait(c *clock, done <-chan error) wait {
	h.t.Helper()

	select {
	case w := <-c.waits:
		return w
	case err := <-done:
		h.t.Fatalf("the send returned (%v) without waiting for its turn", err)
	case <-h.ctx.Done():
		h.t.Fatal("no wait was asked for")
	}

	return wait{}
}

// noWait fails if the slice asked for a wait.
func (h *harness) noWait(c *clock) {
	h.t.Helper()

	select {
	case w := <-c.waits:
		h.t.Fatalf("the slice waited %v", w.d)
	default:
	}
}

func (h *harness) goSend(ctx context.Context, n int) <-chan error {
	done := make(chan error, 1)

	go func() {
		_, err := h.sendText(ctx, n, "hi")
		done <- err
	}()

	return done
}

func (h *harness) done(done <-chan error) error {
	h.t.Helper()

	select {
	case err := <-done:
		return err
	case <-h.ctx.Done():
		h.t.Fatal("the send did not return")

		return nil
	}
}

// At the defaults a burst of five leaves at once, in order; the sixth waits its turn, a second, and the jitter.
func TestTheDefaultPacingSendsABurstThenOneASecondWithJitter(t *testing.T) {
	t.Parallel()

	h := start(t)
	h.svc.pace, h.svc.burst = DefaultInterval, DefaultBurst
	c := h.fakeClock()

	for n := range DefaultBurst {
		if _, err := h.sendText(h.ctx, n, "hi"); err != nil {
			t.Fatalf("send %d: %v", n, err)
		}
	}

	h.noWait(c)

	sixth := h.goSend(h.ctx, DefaultBurst)
	w := h.nextWait(c, sixth)

	if want := time.Second + time.Second/2 - time.Nanosecond; w.d != want {
		t.Fatalf("the sixth waited %v, want a second and the jitter, %v", w.d, want)
	}

	c.pass(w)

	if err := h.done(sixth); err != nil {
		t.Fatalf("the sixth: %v", err)
	}

	got := h.stub.ids()
	for n := range DefaultBurst + 1 {
		if len(got) != DefaultBurst+1 || got[n] != id(n) {
			t.Fatalf("whatsmeow got %v, want 0 to %d in order", got, DefaultBurst)
		}
	}
}

// Jitter only delays a send that has to wait, so the bucket is never exceeded: once the burst is spent, each send waits
// its interval since the last turn and its own jitter, and the jitter of one does not move the next one's turn.
func TestJitterDelaysOnlyASendThatWaitsAndNeverExceedsTheBucket(t *testing.T) {
	t.Parallel()

	h := start(t)
	h.svc.pace, h.svc.burst = 10*time.Second, 2
	c := h.fakeClock()
	jitters := []time.Duration{3 * time.Second, time.Second}
	h.svc.jitter = func(n time.Duration) time.Duration {
		if n != 5*time.Second {
			t.Errorf("jitter up to %v, want half the interval", n)
		}

		j := jitters[0]
		jitters = jitters[1:]

		return j
	}

	for n := range 2 {
		if _, err := h.sendText(h.ctx, n, "hi"); err != nil {
			t.Fatalf("send %d: %v", n, err)
		}
	}

	h.noWait(c)

	// The third: its turn is 10 s away, and 3 s of jitter more. It leaves at 13 s.
	third := h.goSend(h.ctx, 2)
	w := h.nextWait(c, third)

	if w.d != 13*time.Second {
		t.Fatalf("the third waited %v, want 13s", w.d)
	}

	c.pass(w)

	if err := h.done(third); err != nil {
		t.Fatalf("the third: %v", err)
	}

	// The fourth: its turn is 20 s, the third's jitter did not move it, so 7 s from now, and 1 s of jitter more.
	fourth := h.goSend(h.ctx, 3)
	w = h.nextWait(c, fourth)

	if w.d != 8*time.Second {
		t.Fatalf("the fourth waited %v, want 8s", w.d)
	}

	c.pass(w)

	if err := h.done(fourth); err != nil {
		t.Fatalf("the fourth: %v", err)
	}
}

func TestTheJitterIsFromZeroUpToTheBoundGiven(t *testing.T) {
	t.Parallel()

	svc := New(nil, nil, nil, zerolog.Nop(), Pacing{Interval: DefaultInterval, Burst: DefaultBurst})

	if j := svc.jitter(0); j != 0 {
		t.Fatalf("the jitter up to 0 = %v, want 0", j)
	}

	seen := map[time.Duration]bool{}

	for range 10_000 {
		j := svc.jitter(4)
		if j < 0 || j >= 4 {
			t.Fatalf("the jitter up to 4ns = %v, want 0 to 3ns", j)
		}

		seen[j] = true
	}

	if len(seen) != 4 {
		t.Fatalf("the jitter up to 4ns took %v, want every one of 0 to 3ns", seen)
	}
}

// Every kind of send takes its turn from the session's one budget: with a burst of one, each next send, whatever
// its kind, waits an interval.
func TestEveryKindOfSendSharesTheSessionsOneBudget(t *testing.T) {
	t.Parallel()

	h := start(t)
	h.svc.pace, h.svc.burst = 10*time.Second, 1
	c := h.fakeClock()
	h.svc.jitter = func(time.Duration) time.Duration { return 0 }

	kinds := []struct {
		name string
		send func(n int) error
		is   func(*waE2E.Message) bool
	}{
		{"text", func(n int) error { _, err := h.sendText(h.ctx, n, "hi"); return err }, func(m *waE2E.Message) bool { return m.GetConversation() == "hi" }},
		{"raw", func(n int) error {
			_, err := h.client.SendMessage(h.ctx, &whatsmeowv1.SendMessageRequest{SessionId: "s1", Chat: "111@lid", ClientMessageId: id(n), Message: &waE2E.Message{Conversation: proto.String("raw")}})
			return err //nolint:wrapcheck // the test reads the status
		}, func(m *waE2E.Message) bool { return m.GetConversation() == "raw" }},
		{"media", func(n int) error {
			_, err := h.sendMedia(n, ref(whatsmeowv1.MediaType_MEDIA_TYPE_IMAGE, nil), image(&whatsmeowv1.ImageMetadata{}))
			return err
		}, func(m *waE2E.Message) bool { return m.GetImageMessage() != nil }},
		{"reaction", func(n int) error {
			_, err := h.client.SendReaction(h.ctx, &whatsmeowv1.SendReactionRequest{SessionId: "s1", Chat: "111@lid", ClientMessageId: id(n), TargetId: "ABC", Emoji: "👍"})
			return err //nolint:wrapcheck // the test reads the status
		}, func(m *waE2E.Message) bool { return m.GetReactionMessage() != nil }},
		{"edit", func(n int) error {
			_, err := h.client.SendEdit(h.ctx, &whatsmeowv1.SendEditRequest{SessionId: "s1", Chat: "111@lid", ClientMessageId: id(n), TargetId: "ABC", Text: "fixed"})
			return err //nolint:wrapcheck // the test reads the status
		}, func(m *waE2E.Message) bool { return m.GetEditedMessage() != nil }},
		{"revoke", func(n int) error {
			_, err := h.client.SendRevoke(h.ctx, &whatsmeowv1.SendRevokeRequest{SessionId: "s1", Chat: "111@lid", ClientMessageId: id(n), TargetId: "ABC"})
			return err //nolint:wrapcheck // the test reads the status
		}, func(m *waE2E.Message) bool { return m.GetProtocolMessage().GetType() == waE2E.ProtocolMessage_REVOKE }},
	}

	for n, kind := range kinds {
		done := make(chan error, 1)

		go func() { done <- kind.send(n) }()

		if n > 0 {
			w := h.nextWait(c, done)
			if w.d != 10*time.Second {
				t.Fatalf("the %s waited %v, want the interval", kind.name, w.d)
			}

			c.pass(w)
		}

		if err := h.done(done); err != nil {
			t.Fatalf("the %s: %v", kind.name, err)
		}
	}

	sent := h.stub.sent()
	for n, kind := range kinds {
		if len(sent) != len(kinds) || !kind.is(sent[n].message) {
			t.Fatalf("whatsmeow got %v, want each kind once in order", h.stub.ids())
		}
	}
}

// Pacing is per session: one session whose queue waits for its turn never slows another.
func TestOneSessionsWaitingQueueNeverSlowsAnother(t *testing.T) {
	t.Parallel()

	h := start(t)
	h.svc.pace, h.svc.burst = time.Hour, 1
	c := h.fakeClock()

	if _, err := h.sendText(h.ctx, 1, "hi"); err != nil {
		t.Fatalf("s1's first: %v", err)
	}

	waiting := h.goSend(h.ctx, 2)
	w := h.nextWait(c, waiting)

	// s1's second waits its turn, an hour away; s3 sends at once.
	if _, err := h.client.SendText(h.ctx, &whatsmeowv1.SendTextRequest{SessionId: "s3", Chat: "111@lid", ClientMessageId: id(3), Text: "hi"}); err != nil {
		t.Fatalf("s3's send: %v", err)
	}

	h.noWait(c)

	if got := h.stub.ids(); len(got) != 2 || got[1] != id(3) {
		t.Fatalf("whatsmeow got %v, want s1's first, then s3's", got)
	}

	c.pass(w)

	if err := h.done(waiting); err != nil {
		t.Fatalf("s1's second: %v", err)
	}
}

// A rate limit backs the queue off, 1 s, 2 s and 4 s, then fails with RATE_LIMITED; the send queued behind it waited
// meanwhile, and then goes out.
func TestARateLimitedSendHoldsTheQueueAndTheSendBehindItThenGoes(t *testing.T) {
	t.Parallel()

	h := start(t)
	c := h.fakeClock()
	h.svc.backoff = backoff()
	limited := fmt.Errorf("failed to get devices: %w", whatsmeow.ErrIQRateOverLimit)
	h.script(limited, limited, limited, limited)

	first := h.goSend(h.ctx, 1)
	w := h.nextWait(c, first)

	second := h.goSend(h.ctx, 2)
	h.wait(h.queued, "second queued")

	for _, want := range []time.Duration{time.Second, 2 * time.Second, 4 * time.Second} {
		if w.d != want {
			t.Fatalf("the backoff waited %v, want %v", w.d, want)
		}

		c.pass(w)

		if want < 4*time.Second {
			w = h.nextWait(c, first)
		}
	}

	wantReason(t, h.done(first), codes.ResourceExhausted, "RATE_LIMITED", nil)

	if err := h.done(second); err != nil {
		t.Fatalf("the send behind it: %v", err)
	}

	if got := h.stub.ids(); fmt.Sprint(got) != fmt.Sprint([]string{id(1), id(1), id(1), id(1), id(2)}) {
		t.Fatalf("whatsmeow got %v, want four tries of 1, then 2", got)
	}
}

// A send waiting for its turn fails when its call ends, by its deadline or a cancel, sends nothing and leaves no record.
func TestASendWaitingItsTurnFailsWhenItsCallEndsAndSendsNothing(t *testing.T) {
	t.Parallel()

	for _, end := range []error{context.DeadlineExceeded, context.Canceled} {
		h := start(t)
		h.svc.pace, h.svc.burst = time.Hour, 1
		c := h.fakeClock()

		if _, err := h.sendText(h.ctx, 1, "hi"); err != nil {
			t.Fatalf("the first: %v", err)
		}

		// Called on the slice itself, with a call context the test ends: no real deadline is waited for.
		ctx := &endable{Context: h.ctx, done: make(chan struct{})}
		waiting := make(chan error, 1)

		go func() {
			_, err := h.svc.SendText(ctx, &whatsmeowv1.SendTextRequest{SessionId: "s1", Chat: "111@lid", ClientMessageId: id(2), Text: "hi"})
			waiting <- err
		}()

		h.nextWait(c, waiting)
		ctx.end(end)

		if err := h.done(waiting); !errors.Is(err, end) {
			t.Fatalf("the waiting send = %v, want %v", err, end)
		}

		if got := h.stub.ids(); len(got) != 1 {
			t.Fatalf("whatsmeow got %v, want only the first", got)
		}

		if exists, _, _, _ := h.record(2); exists {
			t.Fatalf("the send ended by %v left a record", end)
		}
	}
}

// endable is a call context the test ends with a deadline or a cancel.
type endable struct {
	context.Context //nolint:containedctx // the call's context, whose values and deadline it keeps

	mu   sync.Mutex
	done chan struct{}
	err  error
}

func (e *endable) Done() <-chan struct{} { return e.done }

func (e *endable) Err() error {
	e.mu.Lock()
	defer e.mu.Unlock()

	return e.err
}

func (e *endable) end(err error) {
	e.mu.Lock()
	e.err = err
	e.mu.Unlock()
	close(e.done)
}

// A session that becomes TempBanned fails its queued sends at once with TEMPORARILY_BANNED and the ban's end.
func TestQueuedSendsFailWithTheBansEndWhenTheSessionIsTemporarilyBanned(t *testing.T) {
	t.Parallel()

	h := start(t)
	release := h.hold()

	first := h.async(h.ctx, 1)
	h.next()

	queued := h.async(h.ctx, 2)
	h.wait(h.queued, "second queued")

	const end = "2026-11-02T13:00:00Z"
	h.sessions.leave(fmt.Errorf("session %q until %s: %w", "s1", end, server.WithMetadata(server.ErrTemporarilyBanned, "expireTime", end)))
	wantReason(t, h.outcome(queued).err, codes.FailedPrecondition, "TEMPORARILY_BANNED", map[string]string{"expireTime": end})

	// And so does a new send, until the session is online again.
	_, err := h.sendText(h.ctx, 3, "hi")
	wantReason(t, err, codes.FailedPrecondition, "TEMPORARILY_BANNED", map[string]string{"expireTime": end})

	close(release)
	h.outcome(first)

	if got := h.stub.ids(); len(got) != 1 {
		t.Fatalf("whatsmeow got %v, want only the first", got)
	}
}

// Whether WhatsApp says how long to wait after a 429 is unknown, and whatsmeow reads only the error's code and text: the
// log names the error's attributes, never their values, so a live 429 shows whether there is one (ADR 0042).
func TestARateLimitLogsTheNamesOfTheErrorsAttributesOnly(t *testing.T) {
	t.Parallel()

	h := start(t)
	h.svc.backoff = nil
	node := waBinary.Node{Tag: "error", Attrs: waBinary.Attrs{"code": "429", "text": "rate-overlimit", "backoff": "4711"}}
	h.script(fmt.Errorf("failed to get devices: %w", &whatsmeow.IQError{Code: 429, Text: "rate-overlimit", ErrorNode: &node, RawNode: &waBinary.Node{Tag: "iq", Content: []waBinary.Node{node}}}))

	_, err := h.sendText(h.ctx, 1, "hi")
	wantReason(t, err, codes.ResourceExhausted, "RATE_LIMITED", nil)

	logged := h.logs.String()
	if !strings.Contains(logged, `"error_attributes":["backoff","code","text"]`) || strings.Contains(logged, "4711") {
		t.Fatalf("the log %s; want the attributes' names and none of their values", logged)
	}
}
