// Package outbox is the sidecar's durable event log (ARCHITECTURE §7.3): each session's events in seq order, the
// consumers' cursors, and a wake-up for the streams waiting on a session. Its tables are in the store's schema.
//
// A seq comes from SQLite inside the write transaction (AUTOINCREMENT), never from a counter in memory: with one
// writer at a time, a transaction that got a higher seq commits after every lower one, so a live reader never skips
// a seq (TestConcurrentRecordsNeverLetAReaderSkipASeq).
package outbox

import (
	"bytes"
	"context"
	"crypto/sha256"
	"database/sql"
	"encoding/hex"
	"errors"
	"fmt"
	"sync"

	"google.golang.org/protobuf/proto"

	whatsmeowv1 "github.com/tcortega/Whatsmeow.NET/sidecar/gen/whatsmeow/v1"
	"github.com/tcortega/Whatsmeow.NET/sidecar/internal/server"
)

// Outbox is the event log of every session in one database.
type Outbox struct {
	db    *sql.DB
	mu    sync.Mutex
	feeds map[string]*feed
}

// feed wakes the streams of one session. changed is closed and replaced on every record, acked on every ack; deleted
// is closed when the session is deleted.
type feed struct {
	changed chan struct{}
	acked   chan struct{}
	deleted chan struct{}
}

// New returns the outbox of db.
func New(db *sql.DB) *Outbox { return &Outbox{db: db, feeds: map[string]*feed{}} }

// Watch is one session's wake-up, taken before a read so a record in between is never missed.
type Watch struct {
	o *Outbox
	f *feed
}

// Watch returns the wake-up of an existing session, and its highest recorded seq. A session that does not exist gets
// none, so unknown ids leave nothing behind. The check and the wake-up are taken under the lock Delete takes after the
// session row is gone, so a deleted session either fails here or ends the returned watch.
func (o *Outbox) Watch(ctx context.Context, sessionID string) (Watch, uint64, error) {
	o.mu.Lock()
	defer o.mu.Unlock()

	recorded, err := lastSeq(ctx, o.db, sessionID)
	if err != nil {
		return Watch{}, 0, err
	}

	return Watch{o: o, f: o.feedLocked(sessionID)}, recorded, nil
}

// Changed is closed by the next record. Take it before reading, then wait on it.
func (w Watch) Changed() <-chan struct{} {
	w.o.mu.Lock()
	defer w.o.mu.Unlock()

	return w.f.changed
}

// Deleted is closed once the session is deleted.
func (w Watch) Deleted() <-chan struct{} { return w.f.deleted }

// Deleted is closed once the session with this id is deleted, without reading the database: for state a slice keeps
// in memory per session, which must not outlive it. After a delete, the id gets a new channel.
func (o *Outbox) Deleted(sessionID string) <-chan struct{} {
	o.mu.Lock()
	defer o.mu.Unlock()

	return o.feedLocked(sessionID).deleted
}

func (o *Outbox) feedLocked(sessionID string) *feed {
	f, ok := o.feeds[sessionID]
	if !ok {
		f = &feed{changed: make(chan struct{}), acked: make(chan struct{}), deleted: make(chan struct{})}
		o.feeds[sessionID] = f
	}

	return f
}

// wake wakes the session's streams; it never blocks.
func (o *Outbox) wake(sessionID string) {
	o.mu.Lock()
	defer o.mu.Unlock()

	f := o.feedLocked(sessionID)
	close(f.changed)
	f.changed = make(chan struct{})
}

// Delete ends the watches of a deleted session; its rows went with the session row (ON DELETE CASCADE). A later
// session with the same id gets a new watch.
func (o *Outbox) Delete(sessionID string) {
	o.mu.Lock()
	defer o.mu.Unlock()

	if f, ok := o.feeds[sessionID]; ok {
		close(f.deleted)
		delete(o.feeds, sessionID)
	}
}

// Record writes event in one short transaction, together with the writes of with, and wakes the session's streams
// after the commit. An event whose key the session already has with the same content keeps its seq, and nothing new
// is written; an empty key never matches. differs reports a repeat of the key with other content: it is never
// overwritten, it is recorded beside, under the key and a hash of its content, because a duplicate is better than a
// loss, and the caller makes the collision visible. A true redispatch is byte for byte the same, also under the
// derived key.
func (o *Outbox) Record(ctx context.Context, sessionID, key string, event *whatsmeowv1.StreamEventsResponse, with ...func(*sql.Tx) error) (differs bool, err error) {
	// Deterministic, so a map field cannot make a redispatch look different.
	payload, err := proto.MarshalOptions{Deterministic: true}.Marshal(event)
	if err != nil {
		return false, fmt.Errorf("record an event of session %q: %w", sessionID, err)
	}

	tx, err := o.db.BeginTx(ctx, nil)
	if err != nil {
		return false, fmt.Errorf("record an event of session %q: %w", sessionID, err)
	}

	defer func() {
		if err != nil {
			_ = tx.Rollback()
		}
	}()

	inserted, err := insert(ctx, tx, sessionID, key, payload)
	if err != nil {
		return false, fmt.Errorf("record an event of session %q: %w", sessionID, err)
	}

	if !inserted {
		var recorded []byte
		if err = tx.QueryRowContext(ctx, "SELECT payload FROM outbox WHERE session_id = ? AND dedupe_key = ?", sessionID, key).Scan(&recorded); err != nil {
			return false, fmt.Errorf("record an event of session %q: %w", sessionID, err)
		}

		if differs = !bytes.Equal(recorded, payload); differs {
			sum := sha256.Sum256(payload)
			if inserted, err = insert(ctx, tx, sessionID, key+"\x00"+hex.EncodeToString(sum[:]), payload); err != nil {
				return false, fmt.Errorf("record an event of session %q: %w", sessionID, err)
			}
		}
	}

	for _, write := range with {
		if err = write(tx); err != nil {
			return false, err
		}
	}

	if err = tx.Commit(); err != nil {
		return false, fmt.Errorf("record an event of session %q: %w", sessionID, err)
	}

	if inserted {
		o.wake(sessionID)
	}

	return differs, nil
}

// insert adds one row under key unless the session has the key already, and moves the session's highest seq.
func insert(ctx context.Context, tx *sql.Tx, sessionID, key string, payload []byte) (bool, error) {
	var seq int64

	err := tx.QueryRowContext(ctx, `INSERT INTO outbox (session_id, dedupe_key, payload) VALUES (?, ?, ?)
		ON CONFLICT (session_id, dedupe_key) DO NOTHING RETURNING seq`,
		sessionID, sql.NullString{String: key, Valid: key != ""}, payload).Scan(&seq)
	if errors.Is(err, sql.ErrNoRows) {
		return false, nil
	}

	if err != nil {
		return false, fmt.Errorf("insert: %w", err)
	}

	if _, err := tx.ExecContext(ctx, "UPDATE session SET last_seq = ? WHERE id = ?", seq, sessionID); err != nil {
		return false, fmt.Errorf("move the highest seq: %w", err)
	}

	return true, nil
}

// Read returns at most limit of the session's events after the seq after, in seq order.
func (o *Outbox) Read(ctx context.Context, sessionID string, after uint64, limit int) ([]*whatsmeowv1.StreamEventsResponse, error) {
	rows, err := o.db.QueryContext(ctx, "SELECT seq, payload FROM outbox WHERE session_id = ? AND seq > ? ORDER BY seq LIMIT ?",
		sessionID, signed(after), limit)
	if err != nil {
		return nil, fmt.Errorf("read the events of session %q: %w", sessionID, err)
	}
	defer func() { _ = rows.Close() }()

	var events []*whatsmeowv1.StreamEventsResponse

	for rows.Next() {
		var (
			seq     int64
			payload []byte
		)

		if err := rows.Scan(&seq, &payload); err != nil {
			return nil, fmt.Errorf("read the events of session %q: %w", sessionID, err)
		}

		event := &whatsmeowv1.StreamEventsResponse{}
		if err := proto.Unmarshal(payload, event); err != nil {
			return nil, fmt.Errorf("read event %d of session %q: %w", seq, sessionID, err)
		}

		event.Seq = unsigned(seq)
		events = append(events, event)
	}

	if err := rows.Err(); err != nil {
		return nil, fmt.Errorf("read the events of session %q: %w", sessionID, err)
	}

	return events, nil
}

// Highest is the session's highest recorded seq, kept after its rows are deleted; 0 before its first event.
func (o *Outbox) Highest(ctx context.Context, sessionID string) (uint64, error) {
	return lastSeq(ctx, o.db, sessionID)
}

type querier interface {
	QueryRowContext(ctx context.Context, query string, args ...any) *sql.Row
}

func lastSeq(ctx context.Context, q querier, sessionID string) (uint64, error) {
	var seq int64

	err := q.QueryRowContext(ctx, "SELECT last_seq FROM session WHERE id = ?", sessionID).Scan(&seq)
	if errors.Is(err, sql.ErrNoRows) {
		return 0, fmt.Errorf("session %q: %w", sessionID, server.ErrSessionNotFound)
	}

	if err != nil {
		return 0, fmt.Errorf("the highest seq of session %q: %w", sessionID, err)
	}

	return unsigned(seq), nil
}

// Cursor is the highest seq the consumer acked, creating its cursor at 0. The session must exist.
func (o *Outbox) Cursor(ctx context.Context, sessionID, consumer string) (uint64, error) {
	if _, err := o.db.ExecContext(ctx, "INSERT INTO outbox_cursor (session_id, consumer, acked) VALUES (?, ?, 0) ON CONFLICT DO NOTHING",
		sessionID, consumer); err != nil {
		return 0, fmt.Errorf("the cursor of %q on session %q: %w", consumer, sessionID, err)
	}

	var acked int64
	if err := o.db.QueryRowContext(ctx, "SELECT acked FROM outbox_cursor WHERE session_id = ? AND consumer = ?", sessionID, consumer).Scan(&acked); err != nil {
		return 0, fmt.Errorf("the cursor of %q on session %q: %w", consumer, sessionID, err)
	}

	return unsigned(acked), nil
}

// Ack moves the consumer's cursor to upTo, never backwards, and deletes the session's events that every one of its
// consumers acked. upTo above the session's highest recorded seq is an invalid argument and changes nothing.
func (o *Outbox) Ack(ctx context.Context, sessionID, consumer string, upTo uint64) (err error) {
	tx, err := o.db.BeginTx(ctx, nil)
	if err != nil {
		return fmt.Errorf("ack session %q: %w", sessionID, err)
	}

	defer func() {
		if err != nil {
			_ = tx.Rollback()
		}
	}()

	recorded, err := lastSeq(ctx, tx, sessionID)
	if err != nil {
		return err
	}

	if upTo > recorded {
		return fmt.Errorf("ack session %q: %w", sessionID, server.InvalidArgument("up_to_seq", "%d is above the session's highest recorded seq %d", upTo, recorded))
	}

	if _, err = tx.ExecContext(ctx, `INSERT INTO outbox_cursor (session_id, consumer, acked) VALUES (?, ?, ?)
		ON CONFLICT (session_id, consumer) DO UPDATE SET acked = max(acked, excluded.acked)`, sessionID, consumer, signed(upTo)); err != nil {
		return fmt.Errorf("ack session %q: %w", sessionID, err)
	}

	if _, err = tx.ExecContext(ctx, `DELETE FROM outbox WHERE session_id = ?
		AND seq <= (SELECT min(acked) FROM outbox_cursor WHERE session_id = ?)`, sessionID, sessionID); err != nil {
		return fmt.Errorf("ack session %q: %w", sessionID, err)
	}

	if err = tx.Commit(); err != nil {
		return fmt.Errorf("ack session %q: %w", sessionID, err)
	}

	o.mu.Lock()
	f := o.feedLocked(sessionID)
	close(f.acked)
	f.acked = make(chan struct{})
	o.mu.Unlock()

	return nil
}

// Acked is closed by the session's next ack, which may have deleted events: for a slice that waits until the
// consumers took what it recorded (Pending). Take it before reading Pending, then wait on it.
func (o *Outbox) Acked(sessionID string) <-chan struct{} {
	o.mu.Lock()
	defer o.mu.Unlock()

	return o.feedLocked(sessionID).acked
}

// Pending is how many of the session's events whose natural key starts with prefix are still kept: not yet acked by
// every consumer of the session, or, with no consumer, all of them. It reads a range of the (session_id, dedupe_key)
// index; prefix must not end in the byte 0xff.
func (o *Outbox) Pending(ctx context.Context, sessionID, prefix string) (int, error) {
	end := []byte(prefix)
	end[len(end)-1]++

	var n int
	if err := o.db.QueryRowContext(ctx, "SELECT count(*) FROM outbox WHERE session_id = ? AND dedupe_key >= ? AND dedupe_key < ?",
		sessionID, prefix, string(end)).Scan(&n); err != nil {
		return 0, fmt.Errorf("the pending events of session %q: %w", sessionID, err)
	}

	return n, nil
}

// SQLite integers are signed; a seq is positive and far below 2^63.
func signed(v uint64) int64 { return int64(v) } //nolint:gosec // G115: a seq is far below 2^63

func unsigned(v int64) uint64 { return uint64(v) } //nolint:gosec // G115: a seq is never negative
