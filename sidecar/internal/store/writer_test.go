package store

import (
	"context"
	"database/sql"
	"errors"
	"fmt"
	"path/filepath"
	"runtime"
	"testing"
	"time"

	"github.com/rs/zerolog"
)

// openWriter returns the gate of a new store, and a bound for a broken gate: every wait below ends on a condition.
func openWriter(t *testing.T) (*Writer, context.Context) {
	t.Helper()

	s, err := Open(context.Background(), filepath.Join(t.TempDir(), "whatsmeow-net.db"), zerolog.Nop())
	if err != nil {
		t.Fatalf("open the store: %v", err)
	}

	t.Cleanup(func() { _ = s.Close() })

	ctx, cancel := context.WithTimeout(context.Background(), 30*time.Second)
	t.Cleanup(cancel)

	return s.Writer, ctx
}

// queued waits until n writers wait for the gate.
func queued(ctx context.Context, t *testing.T, w *Writer, n int) {
	t.Helper()

	for {
		w.mu.Lock()
		got := len(w.waiting)
		w.mu.Unlock()

		if got == n {
			return
		}

		if ctx.Err() != nil {
			t.Fatalf("%d writers wait for the gate, want %d", got, n)
		}

		runtime.Gosched()
	}
}

// slowWriter writes one session row per turn, and holds each transaction open until the test lets it commit: the
// test is the disk.
type slowWriter struct {
	name    string
	commit  chan struct{}
	entered chan<- string
}

func (s slowWriter) write(ctx context.Context, w *Writer, n int) error {
	return w.Tx(ctx, func(ctx context.Context, tx *sql.Tx) error {
		if _, err := tx.ExecContext(ctx, "INSERT INTO session (id) VALUES (?)", fmt.Sprintf("%s-%d", s.name, n)); err != nil {
			return fmt.Errorf("insert: %w", err)
		}

		s.entered <- s.name
		<-s.commit

		return nil
	})
}

func TestTheGateHandsItselfOverInArrivalOrder(t *testing.T) {
	t.Parallel()

	const writers, rounds = 4, 3

	w, ctx := openWriter(t)
	entered := make(chan string)
	all := make([]slowWriter, writers)
	done := make(chan error, writers)

	for i := range all {
		all[i] = slowWriter{name: fmt.Sprintf("w%d", i), commit: make(chan struct{}), entered: entered}
	}

	// Each writer asks again as soon as its write committed, as the starved test's writers did (#89).
	start := func(s slowWriter) {
		go func() {
			for n := range rounds {
				if err := s.write(ctx, w, n); err != nil {
					done <- err

					return
				}
			}

			done <- nil
		}()
	}

	// w0 holds the gate; the others queue behind it in order.
	start(all[0])

	if got := <-entered; got != "w0" {
		t.Fatalf("%s entered first, want w0", got)
	}

	for i := 1; i < writers; i++ {
		start(all[i])
		queued(ctx, t, w, i)
	}

	// Every commit hands the gate to the oldest waiter, so each writer waits for the other three, never more.
	left := make([]int, writers) // writes not yet entered
	for i := range left {
		left[i] = rounds
	}

	left[0]--

	holder := 0
	for turn := 1; turn < writers*rounds; turn++ {
		all[holder].commit <- struct{}{}

		next := turn % writers
		if got := <-entered; got != all[next].name {
			t.Fatalf("turn %d went to %s, want %s: it waited longest", turn, got, all[next].name)
		}

		left[next]--

		// The one that committed asks again, if it has a write left, before the next commit.
		waiting := 0
		for i, n := range left {
			if i != next && n > 0 {
				waiting++
			}
		}

		queued(ctx, t, w, waiting)

		holder = next
	}

	all[holder].commit <- struct{}{}

	for range writers {
		if err := <-done; err != nil {
			t.Fatalf("write: %v", err)
		}
	}
}

func TestAWriterWhoseContextEndsLeavesTheQueue(t *testing.T) {
	t.Parallel()

	w, ctx := openWriter(t)
	entered := make(chan string)
	first := slowWriter{name: "first", commit: make(chan struct{}), entered: entered}
	last := slowWriter{name: "last", commit: make(chan struct{}), entered: entered}

	firstDone := make(chan error, 1)

	go func() { firstDone <- first.write(ctx, w, 0) }()

	<-entered

	gone, leave := context.WithCancel(ctx)
	goneDone := make(chan error, 1)

	go func() {
		goneDone <- w.Tx(gone, func(context.Context, *sql.Tx) error {
			t.Error("the cancelled writer wrote")

			return nil
		})
	}()

	queued(ctx, t, w, 1)

	lastDone := make(chan error, 1)

	go func() { lastDone <- last.write(ctx, w, 0) }()

	queued(ctx, t, w, 2)

	leave()

	if err := <-goneDone; !errors.Is(err, context.Canceled) {
		t.Fatalf("the cancelled writer got %v, want %v", err, context.Canceled)
	}

	queued(ctx, t, w, 1)

	first.commit <- struct{}{}

	if got := <-entered; got != "last" {
		t.Fatalf("%s entered after the first, want last", got)
	}

	last.commit <- struct{}{}

	if err := errors.Join(<-firstDone, <-lastDone); err != nil {
		t.Fatalf("write: %v", err)
	}
}

func TestACancelledWriterNeverKeepsTheGate(t *testing.T) {
	t.Parallel()

	w, ctx := openWriter(t)
	gone, leave := context.WithCancel(ctx)
	leave()

	// Handed the gate as its context ended: a wait that sees both picks either at random, so 64 rounds take the
	// cancelled branch all but surely. Either way the gate is free afterwards.
	for n := range 64 {
		if w.enqueue() != nil {
			t.Fatalf("round %d: the gate was not free", n)
		}

		turn := w.enqueue()
		w.release()

		if err := w.wait(gone, turn); err == nil {
			w.release()
		} else if !errors.Is(err, context.Canceled) {
			t.Fatalf("round %d: the cancelled writer got %v, want %v", n, err, context.Canceled)
		}

		w.mu.Lock()
		held := w.held
		w.mu.Unlock()

		if held {
			t.Fatalf("round %d: a cancelled writer kept the gate", n)
		}
	}
}

func TestAWriteInsideAWriteFailsAtOnce(t *testing.T) {
	t.Parallel()

	w, ctx := openWriter(t)

	var nestedExec, nestedTx error

	err := w.Tx(ctx, func(ctx context.Context, tx *sql.Tx) error {
		_, nestedExec = w.Exec(ctx, "INSERT INTO session (id) VALUES ('nested')")
		nestedTx = w.Tx(ctx, func(context.Context, *sql.Tx) error { return nil })

		_, err := tx.ExecContext(ctx, "INSERT INTO session (id) VALUES ('outer')")

		return err //nolint:wrapcheck // the test's own write
	})
	if err != nil {
		t.Fatalf("the outer write: %v", err)
	}

	if !errors.Is(nestedExec, ErrNestedWrite) || !errors.Is(nestedTx, ErrNestedWrite) {
		t.Fatalf("the nested Exec got %v and Tx %v, want %v", nestedExec, nestedTx, ErrNestedWrite)
	}

	var ids []string

	rows, err := w.db.QueryContext(ctx, "SELECT id FROM session ORDER BY id")
	if err != nil {
		t.Fatalf("read: %v", err)
	}
	defer func() { _ = rows.Close() }()

	for rows.Next() {
		var id string
		if err := rows.Scan(&id); err != nil {
			t.Fatalf("read: %v", err)
		}

		ids = append(ids, id)
	}

	if len(ids) != 1 || ids[0] != "outer" {
		t.Fatalf("the sessions are %v, want only outer", ids)
	}
}
