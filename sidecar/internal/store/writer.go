package store

import (
	"context"
	"database/sql"
	"errors"
	"fmt"
	"slices"
	"sync"
)

// ErrNestedWrite is a write asked for inside another one, which would wait for itself.
var ErrNestedWrite = errors.New("a write inside a write")

// Writer is the gate every write of the sidecar takes, from BEGIN to COMMIT or ROLLBACK (ADR 0040). It hands the
// write lock over in arrival order: SQLite's busy handler lets a writer that just committed begin again before one
// that sleeps between tries, which starved a writer for 27 s on a loaded Windows disk (#89). whatsmeow's own store
// writes do not take it; they still wait in the busy handler.
type Writer struct {
	db *sql.DB

	mu   sync.Mutex
	held bool
	// waiting is the writers waiting for the gate, oldest first; release closes the first one's channel.
	waiting []chan struct{}
}

func newWriter(db *sql.DB) *Writer { return &Writer{db: db} }

// heldKey marks the context of a function Tx runs, so a write inside it fails instead of waiting for itself.
type heldKey struct{}

// Tx runs fn in a write transaction, after every write that asked earlier, and commits unless fn fails. fn gets
// the transaction's context; a Tx or Exec on it fails with ErrNestedWrite.
func (w *Writer) Tx(ctx context.Context, fn func(context.Context, *sql.Tx) error) error {
	if err := w.acquire(ctx); err != nil {
		return err
	}
	defer w.release()

	tx, err := w.db.BeginTx(ctx, nil)
	if err != nil {
		return fmt.Errorf("begin: %w", err)
	}

	// Also when fn fails or panics, before the gate is handed on: an open transaction would keep SQLite's lock. After
	// the commit it does nothing.
	defer func() { _ = tx.Rollback() }()

	if err := fn(context.WithValue(ctx, heldKey{}, w), tx); err != nil {
		return err
	}

	if err := tx.Commit(); err != nil {
		return fmt.Errorf("commit: %w", err)
	}

	return nil
}

// Exec runs one write statement, after every write that asked earlier.
func (w *Writer) Exec(ctx context.Context, query string, args ...any) (sql.Result, error) {
	if err := w.acquire(ctx); err != nil {
		return nil, err
	}
	defer w.release()

	result, err := w.db.ExecContext(ctx, query, args...)
	if err != nil {
		return nil, fmt.Errorf("write: %w", err)
	}

	return result, nil
}

// acquire waits for the gate, behind every writer that asked earlier, until ctx ends.
func (w *Writer) acquire(ctx context.Context) error {
	if ctx.Value(heldKey{}) == w {
		return fmt.Errorf("wait to write: %w", ErrNestedWrite)
	}

	return w.wait(ctx, w.enqueue())
}

// enqueue takes the gate if it is free, and returns nil; otherwise it queues behind the writers waiting, and returns
// the channel release closes when it is this one's turn.
func (w *Writer) enqueue() chan struct{} {
	w.mu.Lock()
	defer w.mu.Unlock()

	if !w.held {
		w.held = true

		return nil
	}

	turn := make(chan struct{})
	w.waiting = append(w.waiting, turn)

	return turn
}

// wait waits for turn, or leaves the queue when ctx ends first.
func (w *Writer) wait(ctx context.Context, turn chan struct{}) error {
	if turn == nil {
		return nil
	}

	select {
	case <-turn:
		return nil
	case <-ctx.Done():
	}

	w.mu.Lock()
	defer w.mu.Unlock()

	if i := slices.Index(w.waiting, turn); i >= 0 {
		w.waiting = slices.Delete(w.waiting, i, i+1)

		return fmt.Errorf("wait to write: %w", ctx.Err())
	}

	// The gate was handed over as ctx ended: pass it on.
	w.releaseLocked()

	return fmt.Errorf("wait to write: %w", ctx.Err())
}

// release hands the gate to the oldest waiter, or frees it.
func (w *Writer) release() {
	w.mu.Lock()
	defer w.mu.Unlock()

	w.releaseLocked()
}

func (w *Writer) releaseLocked() {
	if len(w.waiting) == 0 {
		w.held = false

		return
	}

	close(w.waiting[0])
	w.waiting = slices.Delete(w.waiting, 0, 1)
}
