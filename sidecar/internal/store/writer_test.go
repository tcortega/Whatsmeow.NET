package store

import (
	"context"
	"database/sql"
	"errors"
	"fmt"
	"path/filepath"
	"runtime"
	"sync/atomic"
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
	all := make(map[string]slowWriter, writers)
	done := make(chan error, writers)

	// Each writer asks again as soon as its write committed, as the starved test's writers did (#89).
	start := func(s slowWriter) {
		all[s.name] = s

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
	start(slowWriter{name: "w0", commit: make(chan struct{}), entered: entered})

	if got := <-entered; got != "w0" {
		t.Fatalf("%s entered first, want w0", got)
	}

	for i := 1; i < writers; i++ {
		start(slowWriter{name: fmt.Sprintf("w%d", i), commit: make(chan struct{}), entered: entered})
		queued(ctx, t, w, i)
	}

	// The writes enter in arrival order, so the first lap is w0 to w3, and a writer that asks again queues behind
	// every writer waiting: none enters a lap ahead of one still waiting (ADR 0068 batches them, in that order).
	entries := map[string]int{"w0": 1}
	all["w0"].commit <- struct{}{}

	for i := 1; i < writers; i++ {
		got := <-entered
		if want := fmt.Sprintf("w%d", i); got != want {
			t.Fatalf("entry %d went to %s, want %s: it waited longest", i, got, want)
		}

		entries[got]++
		all[got].commit <- struct{}{}
	}

	for range writers * (rounds - 1) {
		got := <-entered
		entries[got]++

		for name, n := range entries {
			if n < rounds && entries[got] > n+1 {
				t.Fatalf("%s entered for the %dth time while %s, still waiting, entered %d times", got, entries[got], name, n)
			}
		}

		all[got].commit <- struct{}{}
	}

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

	// One write a batch: every queued writer is handed the gate to lead the next batch, never taken into this one.
	var ticks atomic.Int64

	w.now = func() time.Time { return time.Unix(ticks.Add(1)*3600, 0) }

	// Handed the gate as its context ends: a wait that sees both picks either at random, so 64 rounds take the
	// cancelled branch all but surely. Either way the gate is free afterwards.
	for n := range 64 {
		release, first := held(ctx, t, w)
		gone, leave := context.WithCancel(ctx)
		left := make(chan error, 1)

		go func() { left <- w.Tx(gone, func(context.Context, *sql.Tx) error { return nil }) }()

		queued(ctx, t, w, 1)

		go leave()
		release()

		if err := <-first; err != nil {
			t.Fatalf("round %d: the first write: %v", n, err)
		}

		if err := <-left; err != nil && !errors.Is(err, context.Canceled) {
			t.Fatalf("round %d: the cancelled writer got %v, want nil or %v", n, err, context.Canceled)
		}

		next, cancel := context.WithTimeout(ctx, 5*time.Second)
		_, err := w.Exec(next, "INSERT INTO session (id) VALUES (?)", fmt.Sprintf("after-%d", n))

		cancel()

		if err != nil {
			t.Fatalf("round %d: the next write got %v: a cancelled writer kept the gate", n, err)
		}

		w.mu.Lock()
		kept := w.held
		w.mu.Unlock()

		if kept {
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

func TestAPanicInAWriteLeavesNoOpenTransaction(t *testing.T) {
	t.Parallel()

	w, ctx := openWriter(t)

	func() {
		defer func() { _ = recover() }()

		_ = w.Tx(ctx, func(ctx context.Context, tx *sql.Tx) error {
			if _, err := tx.ExecContext(ctx, "INSERT INTO session (id) VALUES ('panicked')"); err != nil {
				return err //nolint:wrapcheck // the test's own write
			}

			panic("the write panicked")
		})
	}()

	if inUse := w.db.Stats().InUse; inUse != 0 {
		t.Fatalf("%d connections are still in use after the panic, want 0: its transaction is open", inUse)
	}

	// SQLite's lock would be held by the open transaction: the next writer would wait out the busy_timeout.
	next, cancel := context.WithTimeout(ctx, 5*time.Second)
	defer cancel()

	if _, err := w.Exec(next, "INSERT INTO session (id) VALUES ('next')"); err != nil {
		t.Fatalf("the next write: %v", err)
	}

	var n int
	if err := w.db.QueryRowContext(ctx, "SELECT count(*) FROM session WHERE id = 'panicked'").Scan(&n); err != nil || n != 0 {
		t.Fatalf("the panicked write's row: %d, %v; want 0, rolled back", n, err)
	}
}

// A write runs on a context that never ends once it joined a batch (ADR 0068), so database/sql never rolls its
// transaction back on a goroutine of its own (Tx.awaitDone), which nobody waits for, still holding the database file
// while the store closes (#124): the write commits, its connection back in the pool before it returns. After the
// cancel, fn gives that goroutine up to 100 ms to take the transaction (a statement on it then fails with
// sql.ErrTxDone).
func TestACancelledWriteGivesItsConnectionBackBeforeItReturns(t *testing.T) {
	t.Parallel()

	w, ctx := openWriter(t)

	for n := range 4 {
		write, cancel := context.WithCancel(ctx)
		taken := false

		err := w.Tx(write, func(ctx context.Context, tx *sql.Tx) error {
			if _, err := tx.ExecContext(ctx, "INSERT INTO session (id) VALUES (?)", fmt.Sprintf("cancelled-%d", n)); err != nil {
				return err //nolint:wrapcheck // the test's own write
			}

			cancel()

			for deadline := time.Now().Add(100 * time.Millisecond); !taken && time.Now().Before(deadline); runtime.Gosched() {
				_, err := tx.ExecContext(context.WithoutCancel(ctx), "SELECT 1")
				taken = errors.Is(err, sql.ErrTxDone)
			}

			return ctx.Err()
		})
		if err != nil {
			t.Fatalf("round %d: the write cancelled after it joined got %v, want its batch's outcome, committed", n, err)
		}

		if taken {
			t.Fatalf("round %d: database/sql took the cancelled write's transaction from Tx, to roll it back on a goroutine of its own", n)
		}

		if inUse := w.db.Stats().InUse; inUse != 0 {
			t.Fatalf("round %d: %d connections are still in use after the cancelled write returned, want 0", n, inUse)
		}
	}

	var rows int
	if err := w.db.QueryRowContext(ctx, "SELECT count(*) FROM session").Scan(&rows); err != nil || rows != 4 {
		t.Fatalf("the cancelled writes' rows: %d, %v; want 4, committed", rows, err)
	}
}

// A write whose context ends after it joined a batch runs to the batch's outcome: its statements run on, never
// interrupted, which would roll back the whole batch's transaction, and it commits (ADR 0068).
func TestAWriteWhoseContextEndsAfterItJoinedCommits(t *testing.T) {
	t.Parallel()

	w, ctx := openWriter(t)
	write, cancel := context.WithCancel(ctx)

	err := w.Tx(write, func(ctx context.Context, tx *sql.Tx) error {
		if _, err := tx.ExecContext(ctx, "INSERT INTO session (id) VALUES ('joined-1')"); err != nil {
			return err //nolint:wrapcheck // the test's own write
		}

		cancel()

		_, err := tx.ExecContext(ctx, "INSERT INTO session (id) VALUES ('joined-2')")

		return err //nolint:wrapcheck // the test's own write
	})
	if err != nil {
		t.Fatalf("the write got %v, want nil", err)
	}

	if ids := sessions(ctx, t, w); fmt.Sprint(ids) != "[joined-1 joined-2]" {
		t.Fatalf("the sessions are %v, want both statements committed", ids)
	}
}

// holdWriteLock takes SQLite's write lock on a connection of its own, as a whatsmeow store write would, until the
// returned function lets it go.
func holdWriteLock(ctx context.Context, t *testing.T, w *Writer) func() {
	t.Helper()

	conn, err := w.db.Conn(ctx)
	if err != nil {
		t.Fatalf("a connection: %v", err)
	}

	if _, err := conn.ExecContext(ctx, "BEGIN IMMEDIATE"); err != nil {
		t.Fatalf("take the write lock: %v", err)
	}

	return func() {
		_, _ = conn.ExecContext(context.WithoutCancel(ctx), "ROLLBACK")
		_ = conn.Close()
	}
}

// A write whose context already ended never begins: BEGIN does not stop when its context ends, and would wait out the
// busy_timeout behind whatsmeow's write.
func TestAWriteWhoseContextAlreadyEndedNeverBegins(t *testing.T) {
	t.Parallel()

	w, ctx := openWriter(t)
	release := holdWriteLock(ctx, t, w)
	defer release()

	write, cancel := context.WithCancel(ctx)
	cancel()

	bound, stop := context.WithTimeout(ctx, 5*time.Second)
	defer stop()

	done := make(chan error, 1)

	go func() {
		done <- w.Tx(write, func(context.Context, *sql.Tx) error {
			t.Error("the cancelled write ran")

			return nil
		})
	}()

	select {
	case err := <-done:
		if !errors.Is(err, context.Canceled) {
			t.Fatalf("the cancelled write got %v, want %v", err, context.Canceled)
		}
	case <-bound.Done():
		t.Fatal("the cancelled write waited for the write lock")
	}

	// The lock's holder only.
	if inUse := w.db.Stats().InUse; inUse != 1 {
		t.Fatalf("%d connections are in use after the cancelled write returned, want 1, the lock's", inUse)
	}
}

// A write whose context ends while its BEGIN waits for whatsmeow's write lock does not run once it has the lock, and
// Tx has rolled it back when it returns.
func TestAWriteWhoseContextEndsInItsBeginNeverRuns(t *testing.T) {
	t.Parallel()

	w, ctx := openWriter(t)
	release := holdWriteLock(ctx, t, w)
	write, cancel := context.WithCancel(ctx)
	done := make(chan error, 1)

	go func() {
		done <- w.Tx(write, func(context.Context, *sql.Tx) error {
			t.Error("the write ran after its context ended")

			return nil
		})
	}()

	// The write has its connection: its BEGIN waits for the lock.
	for w.db.Stats().InUse != 2 {
		if ctx.Err() != nil {
			t.Fatal("the write never took a connection")
		}

		runtime.Gosched()
	}

	cancel()
	release()

	if err := <-done; !errors.Is(err, context.Canceled) {
		t.Fatalf("the write got %v, want %v", err, context.Canceled)
	}

	if inUse := w.db.Stats().InUse; inUse != 0 {
		t.Fatalf("%d connections are still in use after the write returned, want 0", inUse)
	}
}
