package store

import (
	"context"
	"database/sql"
	"errors"
	"fmt"
	"slices"
	"sync"
	"time"
)

// ErrNestedWrite is a write asked for inside another one, which would wait for itself.
var ErrNestedWrite = errors.New("a write inside a write")

// errClosed is a write that asked once the store began to close.
var errClosed = errors.New("the store is closed")

const (
	// maxBatch is the most writes one commit holds (ADR 0068).
	maxBatch = 64
	// batchTime is how long a batch takes writes for: once its writes ran this long, it commits what it has.
	batchTime = 250 * time.Millisecond
)

// Writer is the gate every write of the sidecar takes, from BEGIN to COMMIT or ROLLBACK (ADR 0040). It hands the
// write lock over in arrival order: SQLite's busy handler lets a writer that just committed begin again before one
// that sleeps between tries, which starved a writer for 27 s on a loaded Windows disk (#89). whatsmeow's own store
// writes do not take it; they still wait in the busy handler.
//
// The writes that queue while a commit runs share the next one (ADR 0068): the writer handed the gate begins one
// transaction and runs its own write and the oldest waiting ones in it, each in a savepoint of its own, up to
// maxBatch writes or batchTime, then commits once. A restart's hundreds of writes then cost a few commits, not one
// each, so a send behind them waits for a few commits on a slow disk, not minutes (#176).
type Writer struct {
	db *sql.DB

	mu   sync.Mutex
	held bool
	// waiting is the writes waiting for the gate, oldest first.
	waiting []*write
	// running is closed when the batch in flight ends; nil while none runs.
	running chan struct{}
	closed  bool

	// now and beforeCommit are the tests' clock and disk (export_test.go).
	now          func() time.Time
	beforeCommit func()
}

func newWriter(db *sql.DB) *Writer { return &Writer{db: db, now: time.Now} }

// where a write is: waiting for the gate, handed it to lead the next batch, or taken into a batch.
type where int

const (
	waitingForTheGate where = iota
	leading
	taken
)

// write is one Tx: its caller's context and function, and its outcome once its batch ended.
type write struct {
	ctx   context.Context
	fn    func(context.Context, *sql.Tx) error
	where where
	// turn is closed when the write is handed the gate; done when its outcome is set, by the batch that took it.
	turn, done chan struct{}

	err        error
	panicked   bool
	panicValue any
}

// outcome is the write's result on its caller's goroutine: its error, or its function's panic again.
func (wr *write) outcome() error {
	if wr.panicked {
		panic(wr.panicValue)
	}

	return wr.err
}

// heldKey marks the context of a function Tx runs, so a write inside it fails instead of waiting for itself.
type heldKey struct{}

// Tx runs fn in a write transaction, after every write that asked earlier, and returns once the commit that holds it
// is durable, or with fn's error, the write undone. ctx bounds only the wait for the gate: once a batch took the
// write, fn runs on a context that never ends, and the write gets its batch's outcome (ADR 0068). fn's context carries
// ctx's values; a Tx or Exec on it fails with ErrNestedWrite. A panic in fn is raised again here.
func (w *Writer) Tx(ctx context.Context, fn func(context.Context, *sql.Tx) error) error {
	if ctx.Value(heldKey{}) == w {
		return fmt.Errorf("wait to write: %w", ErrNestedWrite)
	}

	// A write whose context already ended never begins: BEGIN waits for whatsmeow's writes, whatever ctx says.
	if err := ctx.Err(); err != nil {
		return fmt.Errorf("begin: %w", err)
	}

	wr := &write{ctx: ctx, fn: fn, turn: make(chan struct{}), done: make(chan struct{})}
	if !w.enqueue(wr) {
		lead, err := w.wait(wr)
		if err != nil {
			return err
		}

		if !lead {
			return wr.outcome()
		}
	}

	if err := w.lead(wr); err != nil { //nolint:contextcheck // each write of a batch runs on its own caller's context, which write carries
		return err
	}

	return wr.outcome()
}

// Exec runs one write statement, after every write that asked earlier, as Tx does.
func (w *Writer) Exec(ctx context.Context, query string, args ...any) (sql.Result, error) {
	var result sql.Result

	err := w.Tx(ctx, func(ctx context.Context, tx *sql.Tx) error {
		r, err := tx.ExecContext(ctx, query, args...)
		if err != nil {
			return fmt.Errorf("write: %w", err)
		}

		result = r

		return nil
	})
	if err != nil {
		return nil, err
	}

	return result, nil
}

// enqueue takes the gate for wr if it is free, and reports so; otherwise it queues wr behind the writes waiting.
func (w *Writer) enqueue(wr *write) bool {
	w.mu.Lock()
	defer w.mu.Unlock()

	if !w.held {
		w.held = true
		wr.where = leading

		return true
	}

	w.waiting = append(w.waiting, wr)

	return false
}

// wait waits until wr is handed the gate, and reports that it leads the next batch, or until a batch took it and
// ended. It leaves the queue when wr's context ends first; once a batch took wr, it waits for that batch.
func (w *Writer) wait(wr *write) (bool, error) {
	select {
	case <-wr.turn:
		return true, nil
	case <-wr.done:
		return false, nil
	case <-wr.ctx.Done():
	}

	w.mu.Lock()

	switch wr.where {
	case waitingForTheGate:
		w.waiting = slices.DeleteFunc(w.waiting, func(q *write) bool { return q == wr })
		w.mu.Unlock()

		return false, fmt.Errorf("wait to write: %w", wr.ctx.Err())
	case leading:
		// The gate was handed over as ctx ended: pass it on.
		w.releaseLocked()
		w.mu.Unlock()

		return false, fmt.Errorf("wait to write: %w", wr.ctx.Err())
	case taken:
	}

	w.mu.Unlock()
	<-wr.done

	return false, nil
}

// lead runs the batch wr was handed the gate for, then hands the gate on. Its error is the batch's, before wr joined;
// wr's own outcome is set otherwise. The writes the batch took are told their outcomes only once its transaction
// ended and its connection is back in the pool.
func (w *Writer) lead(wr *write) error {
	w.mu.Lock()
	if w.closed {
		w.releaseLocked()
		w.mu.Unlock()

		return fmt.Errorf("begin: %w", errClosed)
	}

	running := make(chan struct{})
	w.running = running
	w.mu.Unlock()

	batch, err := w.batch(wr)

	w.mu.Lock()
	w.running = nil
	close(running)
	w.releaseLocked()
	w.mu.Unlock()

	for _, other := range batch {
		if other != wr {
			close(other.done)
		}
	}

	return err
}

// batch begins a transaction, runs wr's write and the oldest waiting ones in it, and commits. It returns the writes
// it took, each with its outcome set, or the error that ended it before wr joined.
func (w *Writer) batch(wr *write) ([]*write, error) {
	// Begun on a context that never ends: database/sql rolls back a transaction whose own context ended on a goroutine
	// of its own (Tx.awaitDone), which nobody waits for, and which still had the database file open as the store
	// closed (#124). So BEGIN waits for whatsmeow's writes up to the busy_timeout, whatever ctx says (ADR 0040).
	tx, err := w.db.BeginTx(context.WithoutCancel(wr.ctx), nil)
	if err != nil {
		return nil, fmt.Errorf("begin: %w", err)
	}

	// Also when a statement of the batch's own fails, before the gate is handed on: an open transaction would keep
	// SQLite's lock. After the commit it does nothing.
	defer func() { _ = tx.Rollback() }()

	// A write joins when the batch takes it, after BEGIN: one whose context ended meanwhile never runs.
	if err := wr.ctx.Err(); err != nil {
		return nil, fmt.Errorf("begin: %w", err)
	}

	wr.where = taken
	batch := []*write{wr}
	start := w.now()

	err = w.run(tx, wr)
	for err == nil && len(batch) < maxBatch && w.now().Sub(start) < batchTime {
		next := w.take()
		if next == nil {
			break
		}

		batch = append(batch, next)
		err = w.run(tx, next)
	}

	if err == nil {
		if w.beforeCommit != nil {
			w.beforeCommit()
		}

		if cerr := tx.Commit(); cerr != nil {
			err = fmt.Errorf("commit: %w", cerr)
		}
	}

	// The transaction is lost: no write of the batch is kept, and each that did not fail on its own gets why.
	if err != nil {
		for _, taken := range batch {
			if taken.err == nil && !taken.panicked {
				taken.err = err
			}
		}
	}

	return batch, nil
}

// take takes the oldest waiting write into the batch, or returns nil when none waits. One whose context ended is told
// so and skipped: it never joins.
func (w *Writer) take() *write {
	w.mu.Lock()
	defer w.mu.Unlock()

	for len(w.waiting) > 0 {
		wr := w.waiting[0]
		w.waiting = w.waiting[1:]
		wr.where = taken

		if err := wr.ctx.Err(); err != nil {
			wr.err = fmt.Errorf("wait to write: %w", err)
			close(wr.done)

			continue
		}

		return wr
	}

	return nil
}

// run runs wr's function in a savepoint of its own, so a failure or panic undoes wr's statements only, and sets wr's
// outcome. Its error is the transaction's, lost for the whole batch.
func (w *Writer) run(tx *sql.Tx, wr *write) error {
	ctx := context.WithValue(context.WithoutCancel(wr.ctx), heldKey{}, w)

	if _, err := tx.ExecContext(ctx, "SAVEPOINT write"); err != nil {
		return fmt.Errorf("savepoint: %w", err)
	}

	wr.panicked, wr.panicValue, wr.err = call(ctx, tx, wr.fn)

	if wr.err != nil || wr.panicked {
		if _, err := tx.ExecContext(ctx, "ROLLBACK TO write"); err != nil {
			return fmt.Errorf("undo a failed write: %w", err)
		}
	}

	if _, err := tx.ExecContext(ctx, "RELEASE write"); err != nil {
		return fmt.Errorf("release a write: %w", err)
	}

	return nil
}

// call runs fn, and returns its panic instead of raising it on the batch's goroutine.
func call(ctx context.Context, tx *sql.Tx, fn func(context.Context, *sql.Tx) error) (panicked bool, value any, err error) {
	defer func() {
		if r := recover(); r != nil {
			panicked, value = true, r
		}
	}()

	return false, nil, fn(ctx, tx)
}

// releaseLocked hands the gate to the oldest waiting write, to lead the next batch, or frees it. w.mu is held.
func (w *Writer) releaseLocked() {
	if len(w.waiting) == 0 {
		w.held = false

		return
	}

	next := w.waiting[0]
	w.waiting = w.waiting[1:]
	next.where = leading
	close(next.turn)
}

// close refuses the writes that would begin from now on and waits for the batch in flight, so the store never closes
// with a write's connection open (#124).
func (w *Writer) close() {
	w.mu.Lock()
	w.closed = true
	running := w.running
	w.mu.Unlock()

	if running != nil {
		<-running
	}
}
