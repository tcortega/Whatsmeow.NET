package store

import (
	"context"
	"database/sql"
	"errors"
	"fmt"
	"path/filepath"
	"runtime"
	"strings"
	"sync"
	"sync/atomic"
	"testing"
	"time"

	"github.com/rs/zerolog"
)

// held is a write that holds the gate in its function until release is called, or the test ended, before the store
// closes, so the writes after it queue: the test is the disk.
func held(ctx context.Context, t *testing.T, w *Writer) (release func(), done <-chan error) {
	t.Helper()

	hold, holding := make(chan struct{}), make(chan struct{})
	release = sync.OnceFunc(func() { close(hold) })
	t.Cleanup(release)

	result := make(chan error, 1)

	go func() {
		result <- w.Tx(ctx, func(context.Context, *sql.Tx) error {
			close(holding)
			<-hold

			return nil
		})
	}()

	<-holding

	return release, result
}

// countCommits counts the batches' commits, and freezes the clock, so a batch is cut by its count limit alone.
func countCommits(w *Writer) *atomic.Int64 {
	var commits atomic.Int64

	SetBeforeCommit(w, func() { commits.Add(1) })
	StopTheClock(w)

	return &commits
}

func sessions(ctx context.Context, t *testing.T, w *Writer) []string {
	t.Helper()

	rows, err := w.db.QueryContext(ctx, "SELECT id FROM session ORDER BY id")
	if err != nil {
		t.Fatalf("read the sessions: %v", err)
	}
	defer func() { _ = rows.Close() }()

	var ids []string

	for rows.Next() {
		var id string
		if err := rows.Scan(&id); err != nil {
			t.Fatalf("read the sessions: %v", err)
		}

		ids = append(ids, id)
	}

	return ids
}

// The writes queued behind a commit share the next one, and each still gets its own outcome: a write that fails, on
// any statement, or panics is undone alone (its savepoint), and the others commit (ADR 0068).
func TestAFailingWriteInABatchFailsAlone(t *testing.T) {
	t.Parallel()

	w, ctx := openWriter(t)
	commits := countCommits(w)
	release, first := held(ctx, t, w)

	type outcome struct {
		err      error
		panicked any
	}

	results := make([]chan outcome, 4)
	writes := []func(context.Context) error{
		func(ctx context.Context) error {
			_, err := w.Exec(ctx, "INSERT INTO session (id) VALUES ('a')")

			return err
		},
		// Its second statement fails: its first is undone with it.
		func(ctx context.Context) error {
			return w.Tx(ctx, func(ctx context.Context, tx *sql.Tx) error {
				if _, err := tx.ExecContext(ctx, "INSERT INTO session (id) VALUES ('b')"); err != nil {
					return err //nolint:wrapcheck // the test's own write
				}

				_, err := tx.ExecContext(ctx, "INSERT INTO session (id) VALUES ('a')")

				return err //nolint:wrapcheck // the test's own write
			})
		},
		func(ctx context.Context) error {
			_, err := w.Exec(ctx, "INSERT INTO session (id) VALUES ('c')")

			return err
		},
		func(ctx context.Context) error {
			return w.Tx(ctx, func(ctx context.Context, tx *sql.Tx) error {
				if _, err := tx.ExecContext(ctx, "INSERT INTO session (id) VALUES ('d')"); err != nil {
					return err //nolint:wrapcheck // the test's own write
				}

				panic("write d panicked")
			})
		},
	}

	for i, write := range writes {
		results[i] = make(chan outcome, 1)

		go func() {
			var o outcome

			defer func() {
				o.panicked = recover()
				results[i] <- o
			}()

			o.err = write(ctx)
		}()

		queued(ctx, t, w, i+1)
	}

	release()

	if err := <-first; err != nil {
		t.Fatalf("the first write: %v", err)
	}

	got := make([]outcome, len(results))
	for i, r := range results {
		got[i] = <-r
	}

	if got[0].err != nil || got[2].err != nil {
		t.Fatalf("a and c got %v and %v, want both written", got[0].err, got[2].err)
	}

	if got[1].err == nil || !strings.Contains(got[1].err.Error(), "UNIQUE") {
		t.Fatalf("b got %v, want its own UNIQUE failure", got[1].err)
	}

	if got[3].panicked != "write d panicked" || got[3].err != nil {
		t.Fatalf("d got %v and panic %v, want its own panic on its own goroutine", got[3].err, got[3].panicked)
	}

	if ids := sessions(ctx, t, w); fmt.Sprint(ids) != "[a c]" {
		t.Fatalf("the sessions are %v, want [a c]: b's first statement and d undone, a and c kept", ids)
	}

	if n := commits.Load(); n != 1 {
		t.Fatalf("%d commits, want 1: the four writes queued behind the first share its commit", n)
	}

	if inUse := w.db.Stats().InUse; inUse != 0 {
		t.Fatalf("%d connections are still in use, want 0", inUse)
	}
}

// A write returns only once the commit that holds it is durable: never before its batch's COMMIT ran (§13 invariant 2).
func TestNoWriteReturnsBeforeItsBatchCommitted(t *testing.T) {
	t.Parallel()

	w, ctx := openWriter(t)

	var (
		returned atomic.Int64
		mu       sync.Mutex
		seen     []int64
	)

	SetBeforeCommit(w, func() {
		// A write signalled before the commit would have returned by now.
		time.Sleep(20 * time.Millisecond)
		mu.Lock()
		seen = append(seen, returned.Load())
		mu.Unlock()
	})
	StopTheClock(w)

	release, first := held(ctx, t, w)
	done := make(chan error, 2)

	for i := range 2 {
		go func() {
			_, err := w.Exec(ctx, "INSERT INTO session (id) VALUES (?)", fmt.Sprintf("durable-%d", i))
			returned.Add(1)
			done <- err
		}()

		queued(ctx, t, w, i+1)
	}

	release()

	if err := errors.Join(<-first, <-done, <-done); err != nil {
		t.Fatalf("write: %v", err)
	}

	mu.Lock()
	defer mu.Unlock()

	if fmt.Sprint(seen) != "[0]" {
		t.Fatalf("the writes that had returned at each commit: %v, want [0], one commit before any returned", seen)
	}
}

// A batch takes at most 64 writes: the writes behind them wait for one more commit, not for every one of them.
func TestABatchTakesAtMost64Writes(t *testing.T) {
	t.Parallel()

	const writes = 130

	w, ctx := openWriter(t)

	// The rows each commit makes durable, counted in its transaction: the first write inserts none.
	var committed []int

	SetCommit(w, func(tx *sql.Tx) error {
		var n int
		if err := tx.QueryRowContext(ctx, "SELECT count(*) FROM session").Scan(&n); err != nil {
			return err //nolint:wrapcheck // the test's own read
		}

		committed = append(committed, n)

		return tx.Commit()
	})
	StopTheClock(w)

	release, first := held(ctx, t, w)
	done := make(chan error, writes)

	for i := range writes {
		go func() {
			_, err := w.Exec(ctx, "INSERT INTO session (id) VALUES (?)", fmt.Sprintf("counted-%03d", i))
			done <- err
		}()
	}

	queued(ctx, t, w, writes)
	release()

	errs := []error{<-first}
	for range writes {
		errs = append(errs, <-done)
	}

	if err := errors.Join(errs...); err != nil {
		t.Fatalf("write: %v", err)
	}

	// The first and 63 more, then 64, then the last 3.
	if fmt.Sprint(committed) != "[63 127 130]" {
		t.Fatalf("the rows durable after each commit: %v, want [63 127 130]", committed)
	}
}

// A COMMIT that fails fails every write of its batch with its error, and keeps none of them.
func TestACommitThatFailsFailsEveryWriteOfItsBatch(t *testing.T) {
	t.Parallel()

	w, ctx := openWriter(t)
	full := errors.New("the disk is full")

	var commits atomic.Int64

	SetCommit(w, func(tx *sql.Tx) error {
		if commits.Add(1) == 1 {
			return full
		}

		return tx.Commit()
	})
	StopTheClock(w)

	release, first := held(ctx, t, w)
	done := make(chan error, 3)

	for i := range 3 {
		go func() {
			_, err := w.Exec(ctx, "INSERT INTO session (id) VALUES (?)", fmt.Sprintf("lost-%d", i))
			done <- err
		}()

		queued(ctx, t, w, i+1)
	}

	release()

	for _, err := range []error{<-first, <-done, <-done, <-done} {
		if !errors.Is(err, full) {
			t.Fatalf("a write of the failed batch got %v, want %v", err, full)
		}
	}

	if ids := sessions(ctx, t, w); len(ids) != 0 {
		t.Fatalf("the sessions are %v, want none: the failed commit keeps nothing", ids)
	}

	if _, err := w.Exec(ctx, "INSERT INTO session (id) VALUES ('after')"); err != nil {
		t.Fatalf("the next write: %v", err)
	}

	if inUse := w.db.Stats().InUse; inUse != 0 {
		t.Fatalf("%d connections are still in use, want 0", inUse)
	}
}

// A write that breaks the batch's transaction, ending it or its own savepoint, loses the batch: every write in it
// fails, the others with an error that names why, none is kept, and the writes still queued commit in the next batch.
func TestAWriteThatBreaksTheTransactionLosesItsBatch(t *testing.T) {
	t.Parallel()

	broke := errors.New("write b broke the transaction")

	for name, c := range map[string]struct {
		statement string
		returns   error
		cause     string
	}{
		"it ends the transaction":   {"ROLLBACK", broke, broke.Error()},
		"it releases its savepoint": {"RELEASE write", nil, "savepoint"},
	} {
		t.Run(name, func(t *testing.T) {
			t.Parallel()

			w, ctx := openWriter(t)
			StopTheClock(w)

			var batches atomic.Int64

			SetBeforeCommit(w, func() { batches.Add(1) })

			release, first := held(ctx, t, w)
			a, b := make(chan error, 1), make(chan error, 1)

			go func() {
				_, err := w.Exec(ctx, "INSERT INTO session (id) VALUES ('a')")
				a <- err
			}()

			queued(ctx, t, w, 1)

			go func() {
				b <- w.Tx(ctx, func(ctx context.Context, tx *sql.Tx) error {
					if _, err := tx.ExecContext(ctx, c.statement); err != nil {
						return err //nolint:wrapcheck // the test's own write
					}

					return c.returns
				})
			}()

			queued(ctx, t, w, 2)

			// Queued behind b: a lost batch takes no more writes, so it commits in the next.
			after := make(chan error, 1)

			go func() {
				_, err := w.Exec(ctx, "INSERT INTO session (id) VALUES ('c')")
				after <- err
			}()

			queued(ctx, t, w, 3)
			release()

			if err := <-b; c.returns != nil && !errors.Is(err, c.returns) {
				t.Fatalf("b got %v, want its own %v", err, c.returns)
			}

			for writer, err := range map[string]error{"the first": <-first, "a": <-a} {
				if err == nil || !strings.Contains(err.Error(), c.cause) {
					t.Fatalf("%s got %v, want the lost batch's error naming %q", writer, err, c.cause)
				}
			}

			if err := <-after; err != nil {
				t.Fatalf("c, queued behind the lost batch: %v", err)
			}

			if ids := sessions(ctx, t, w); fmt.Sprint(ids) != "[c]" {
				t.Fatalf("the sessions are %v, want [c]: nothing of the lost batch kept", ids)
			}

			if n := batches.Load(); n != 1 {
				t.Fatalf("%d commits, want 1, c's", n)
			}
		})
	}
}

// A write function that ends its goroutine (runtime.Goexit, as t.FailNow does) on the batch's leader still frees the
// gate: the batch's writes get an error instead of waiting forever, none is kept, and the write queued behind them
// leads the next batch.
func TestAWriteThatEndsTheLeadersGoroutineFreesTheGate(t *testing.T) {
	t.Parallel()

	w, ctx := openWriter(t)
	StopTheClock(w)
	release, _ := held(ctx, t, w)
	before, exits, after := make(chan error, 1), make(chan error, 1), make(chan error, 1)

	go func() {
		_, err := w.Exec(ctx, "INSERT INTO session (id) VALUES ('before')")
		before <- err
	}()

	queued(ctx, t, w, 1)

	go func() {
		exits <- w.Tx(ctx, func(context.Context, *sql.Tx) error {
			runtime.Goexit()

			return nil
		})
	}()

	queued(ctx, t, w, 2)

	go func() {
		_, err := w.Exec(ctx, "INSERT INTO session (id) VALUES ('after')")
		after <- err
	}()

	queued(ctx, t, w, 3)
	release()

	wait := func(writer string, done chan error) error {
		t.Helper()

		select {
		case err := <-done:
			return err
		case <-ctx.Done():
			t.Fatalf("%s never returned: the batch's leader ended and nobody told it", writer)

			return nil
		}
	}

	for writer, done := range map[string]chan error{"the write before it": before, "the exiting write": exits} {
		if err := wait(writer, done); !errors.Is(err, errAbandoned) {
			t.Fatalf("%s got %v, want %v", writer, err, errAbandoned)
		}
	}

	if err := wait("the write queued after it", after); err != nil {
		t.Fatalf("the write queued after it got %v, want it committed in the next batch", err)
	}

	if ids := sessions(ctx, t, w); fmt.Sprint(ids) != "[after]" {
		t.Fatalf("the sessions are %v, want [after]: nothing of the abandoned batch kept", ids)
	}
}

// A batch stops taking writes once its writes ran for 250 ms, so a slow write does not hold every one behind it for
// its whole batch.
func TestABatchStopsTakingWritesOnceItsTimeIsSpent(t *testing.T) {
	t.Parallel()

	w, ctx := openWriter(t)

	var (
		commits atomic.Int64
		ran     atomic.Int64
	)

	SetBeforeCommit(w, func() { commits.Add(1) })

	// Each queued write takes 100 ms of the clock.
	start := time.Now()
	w.now = func() time.Time { return start.Add(time.Duration(ran.Load()) * 100 * time.Millisecond) }

	release, first := held(ctx, t, w)
	done := make(chan error, 5)

	for i := range 5 {
		go func() {
			done <- w.Tx(ctx, func(ctx context.Context, tx *sql.Tx) error {
				ran.Add(1)
				_, err := tx.ExecContext(ctx, "INSERT INTO session (id) VALUES (?)", fmt.Sprintf("timed-%d", i))

				return err //nolint:wrapcheck // the test's own write
			})
		}()

		queued(ctx, t, w, i+1)
	}

	release()

	errs := []error{<-first}
	for range 5 {
		errs = append(errs, <-done)
	}

	if err := errors.Join(errs...); err != nil {
		t.Fatalf("write: %v", err)
	}

	// The first, then three writes (300 ms), then the last two.
	if n := commits.Load(); n != 2 {
		t.Fatalf("%d commits, want 2", n)
	}
}

// Closing the store waits for the batch in flight: the store never closes with a write's connection open (#124).
func TestCloseWaitsForTheBatchInFlight(t *testing.T) {
	t.Parallel()

	path := filepath.Join(t.TempDir(), "whatsmeow-net.db")

	s, err := Open(context.Background(), path, zerolog.Nop())
	if err != nil {
		t.Fatalf("open the store: %v", err)
	}

	ctx, cancel := context.WithTimeout(context.Background(), 30*time.Second)
	t.Cleanup(cancel)

	hold, holding := make(chan struct{}), make(chan struct{})
	release := sync.OnceFunc(func() { close(hold) })
	t.Cleanup(release)

	written := make(chan error, 1)

	go func() {
		written <- s.Writer.Tx(ctx, func(ctx context.Context, tx *sql.Tx) error {
			if _, err := tx.ExecContext(ctx, "INSERT INTO session (id) VALUES ('in-flight')"); err != nil {
				return err //nolint:wrapcheck // the test's own write
			}

			close(holding)
			<-hold

			return nil
		})
	}()

	<-holding

	closed := make(chan error, 1)

	go func() { closed <- s.Close() }()

	select {
	case err := <-closed:
		t.Fatalf("the store closed with a write's transaction open (%v)", err)
	case <-time.After(200 * time.Millisecond):
	}

	release()

	if err := <-written; err != nil {
		t.Fatalf("the write in flight: %v", err)
	}

	if err := <-closed; err != nil {
		t.Fatalf("close: %v", err)
	}

	again, err := Open(ctx, path, zerolog.Nop())
	if err != nil {
		t.Fatalf("open the store again: %v", err)
	}
	defer func() { _ = again.Close() }()

	var n int
	if err := again.DB.QueryRowContext(ctx, "SELECT count(*) FROM session WHERE id = 'in-flight'").Scan(&n); err != nil || n != 1 {
		t.Fatalf("the write in flight: %d rows, %v; want 1, committed before the close", n, err)
	}
}
