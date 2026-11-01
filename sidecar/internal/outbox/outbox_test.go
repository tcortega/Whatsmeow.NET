package outbox

import (
	"context"
	"database/sql"
	"errors"
	"fmt"
	"path/filepath"
	"slices"
	"sync"
	"testing"
	"time"

	"github.com/rs/zerolog"
	"google.golang.org/protobuf/proto"

	whatsmeowv1 "github.com/tcortega/Whatsmeow.NET/sidecar/gen/whatsmeow/v1"
	"github.com/tcortega/Whatsmeow.NET/sidecar/internal/server"
	sidecarstore "github.com/tcortega/Whatsmeow.NET/sidecar/internal/store"
)

// open returns an outbox on a real store with the sessions ids created, and a bound for a broken outbox: every wait
// below ends on a condition.
func open(t *testing.T, ids ...string) (*Outbox, *sql.DB, context.Context) {
	t.Helper()

	db, err := sidecarstore.Open(context.Background(), filepath.Join(t.TempDir(), "whatsmeow-net.db"), zerolog.Nop())
	if err != nil {
		t.Fatalf("open the store: %v", err)
	}

	t.Cleanup(func() { _ = db.Close() })

	ctx, cancel := context.WithTimeout(context.Background(), 30*time.Second)
	t.Cleanup(cancel)

	for _, id := range ids {
		if _, err := db.DB.ExecContext(ctx, "INSERT INTO session (id) VALUES (?)", id); err != nil {
			t.Fatalf("create session %s: %v", id, err)
		}
	}

	return New(db.DB), db.DB, ctx
}

// state is a ConnectionStateEvent, an event without a natural key.
func state() *whatsmeowv1.StreamEventsResponse {
	return &whatsmeowv1.StreamEventsResponse{Payload: &whatsmeowv1.StreamEventsResponse_ConnectionState{
		ConnectionState: &whatsmeowv1.ConnectionStateEvent{State: &whatsmeowv1.SessionState{
			Kind: &whatsmeowv1.SessionState_Connected_{Connected: &whatsmeowv1.SessionState_Connected{}},
		}},
	}}
}

func message(id string) *whatsmeowv1.StreamEventsResponse {
	return &whatsmeowv1.StreamEventsResponse{Payload: &whatsmeowv1.StreamEventsResponse_Message{
		Message: &whatsmeowv1.MessageEvent{Info: &whatsmeowv1.MessageInfo{Id: id}},
	}}
}

func record(ctx context.Context, t *testing.T, o *Outbox, session, key string, event *whatsmeowv1.StreamEventsResponse) {
	t.Helper()

	if _, err := o.Record(ctx, session, key, event); err != nil {
		t.Fatalf("Record: %v", err)
	}
}

func seqs(ctx context.Context, t *testing.T, o *Outbox, session string) []uint64 {
	t.Helper()

	events, err := o.Read(ctx, session, 0, 1000)
	if err != nil {
		t.Fatalf("Read: %v", err)
	}

	got := make([]uint64, 0, len(events))
	for _, e := range events {
		got = append(got, e.GetSeq())
	}

	return got
}

func highest(ctx context.Context, t *testing.T, o *Outbox, session string) uint64 {
	t.Helper()

	seq, err := o.Highest(ctx, session)
	if err != nil {
		t.Fatalf("Highest: %v", err)
	}

	return seq
}

func TestRecordAssignsIncreasingSeqsSharedAcrossSessions(t *testing.T) {
	t.Parallel()

	o, _, ctx := open(t, "s1", "s2")
	record(ctx, t, o, "s1", "", state())
	record(ctx, t, o, "s2", "", state())
	record(ctx, t, o, "s1", "", message("M1"))

	if got := seqs(ctx, t, o, "s1"); !slices.Equal(got, []uint64{1, 3}) {
		t.Fatalf("s1 seqs = %v, want [1 3]", got)
	}

	if got := seqs(ctx, t, o, "s2"); !slices.Equal(got, []uint64{2}) {
		t.Fatalf("s2 seqs = %v, want [2]", got)
	}

	events, _ := o.Read(ctx, "s1", 1, 10)
	if want := message("M1"); len(events) != 1 || !proto.Equal(events[0], func() *whatsmeowv1.StreamEventsResponse { want.Seq = 3; return want }()) {
		t.Fatalf("Read after 1 = %v, want M1 at seq 3", events)
	}
}

func TestARepeatedKeyKeepsItsSeqAndAddsNoRow(t *testing.T) {
	t.Parallel()

	o, _, ctx := open(t, "s1")
	record(ctx, t, o, "s1", "m/c/s/M1", message("M1"))
	record(ctx, t, o, "s1", "m/c/s/M2", message("M2"))
	record(ctx, t, o, "s1", "m/c/s/M1", message("M1"))

	if got := seqs(ctx, t, o, "s1"); !slices.Equal(got, []uint64{1, 2}) || highest(ctx, t, o, "s1") != 2 {
		t.Fatalf("seqs = %v, highest %d; want [1 2] and 2", got, highest(ctx, t, o, "s1"))
	}
}

func TestEventsWithoutAKeyAreNeverMerged(t *testing.T) {
	t.Parallel()

	o, _, ctx := open(t, "s1")
	record(ctx, t, o, "s1", "", state())
	record(ctx, t, o, "s1", "", state())

	if got := seqs(ctx, t, o, "s1"); !slices.Equal(got, []uint64{1, 2}) {
		t.Fatalf("seqs = %v, want [1 2]", got)
	}
}

func TestASeqIsNeverReusedAfterTheNewestRowIsDeleted(t *testing.T) {
	t.Parallel()

	o, _, ctx := open(t, "s1")
	record(ctx, t, o, "s1", "", state())
	record(ctx, t, o, "s1", "", state())

	if err := o.Ack(ctx, "s1", "c1", 2); err != nil {
		t.Fatalf("Ack: %v", err)
	}

	if got := seqs(ctx, t, o, "s1"); len(got) != 0 {
		t.Fatalf("after the ack, seqs = %v, want none", got)
	}

	record(ctx, t, o, "s1", "", state())

	if got := seqs(ctx, t, o, "s1"); !slices.Equal(got, []uint64{3}) {
		t.Fatalf("seqs = %v, want [3]", got)
	}
}

func TestTheHighestSeqIsKeptAfterItsRowsAreDeleted(t *testing.T) {
	t.Parallel()

	o, _, ctx := open(t, "s1", "s2")
	record(ctx, t, o, "s1", "", state())
	record(ctx, t, o, "s2", "", state())

	if err := o.Ack(ctx, "s1", "c1", 1); err != nil {
		t.Fatalf("Ack: %v", err)
	}

	if got := highest(ctx, t, o, "s1"); got != 1 {
		t.Fatalf("highest = %d, want 1", got)
	}

	if got := highest(ctx, t, o, "s2"); got != 2 {
		t.Fatalf("s2 highest = %d, want 2", got)
	}
}

func TestHighestOfAnUnknownSessionIsSessionNotFound(t *testing.T) {
	t.Parallel()

	o, _, ctx := open(t)
	if _, err := o.Highest(ctx, "nope"); !errors.Is(err, server.ErrSessionNotFound) {
		t.Fatalf("Highest = %v, want %v", err, server.ErrSessionNotFound)
	}
}

func TestRecordWithRunsInTheSameTransaction(t *testing.T) {
	t.Parallel()

	o, db, ctx := open(t, "s1")
	failed := errors.New("the other write failed")

	_, err := o.Record(ctx, "s1", "", state(), func(tx *sql.Tx) error {
		if _, err := tx.ExecContext(ctx, "UPDATE session SET jid = 'x' WHERE id = 's1'"); err != nil {
			return fmt.Errorf("update: %w", err)
		}

		return failed
	})
	if !errors.Is(err, failed) {
		t.Fatalf("Record = %v, want %v", err, failed)
	}

	var jid sql.NullString
	if err := db.QueryRowContext(ctx, "SELECT jid FROM session WHERE id = 's1'").Scan(&jid); err != nil || jid.Valid {
		t.Fatalf("jid = %v, %v; want NULL: the update rolled back", jid, err)
	}

	if got := seqs(ctx, t, o, "s1"); len(got) != 0 || highest(ctx, t, o, "s1") != 0 {
		t.Fatalf("seqs = %v; want none: the event rolled back", got)
	}
}

func TestRecordForAnUnknownSessionFails(t *testing.T) {
	t.Parallel()

	o, _, ctx := open(t)
	if _, err := o.Record(ctx, "nope", "", state()); err == nil {
		t.Fatal("Record for a session that does not exist succeeded")
	}
}

func TestAckMovesTheCursorAndDeletesOnlyWhatEveryConsumerAcked(t *testing.T) {
	t.Parallel()

	o, _, ctx := open(t, "s1")
	for range 4 {
		record(ctx, t, o, "s1", "", state())
	}

	// c2 exists from its first read, at 0: nothing may go yet.
	if _, err := o.Cursor(ctx, "s1", "c2"); err != nil {
		t.Fatalf("Cursor: %v", err)
	}

	if err := o.Ack(ctx, "s1", "c1", 3); err != nil {
		t.Fatalf("Ack: %v", err)
	}

	if got := seqs(ctx, t, o, "s1"); !slices.Equal(got, []uint64{1, 2, 3, 4}) {
		t.Fatalf("seqs = %v, want all four: c2 acked nothing", got)
	}

	if err := o.Ack(ctx, "s1", "c2", 2); err != nil {
		t.Fatalf("Ack: %v", err)
	}

	if got := seqs(ctx, t, o, "s1"); !slices.Equal(got, []uint64{3, 4}) {
		t.Fatalf("seqs = %v, want [3 4]", got)
	}

	// Never backwards.
	if err := o.Ack(ctx, "s1", "c1", 1); err != nil {
		t.Fatalf("Ack: %v", err)
	}

	if cursor, _ := o.Cursor(ctx, "s1", "c1"); cursor != 3 {
		t.Fatalf("c1 cursor = %d, want 3", cursor)
	}
}

func TestASessionWithNoConsumerKeepsEverything(t *testing.T) {
	t.Parallel()

	o, _, ctx := open(t, "s1", "s2")
	record(ctx, t, o, "s1", "", state())
	record(ctx, t, o, "s2", "", state())

	if err := o.Ack(ctx, "s2", "c1", 2); err != nil {
		t.Fatalf("Ack: %v", err)
	}

	if got := seqs(ctx, t, o, "s1"); !slices.Equal(got, []uint64{1}) {
		t.Fatalf("s1 seqs = %v, want [1]", got)
	}
}

func TestAckAboveTheHighestIsInvalidArgument(t *testing.T) {
	t.Parallel()

	o, _, ctx := open(t, "s1")
	record(ctx, t, o, "s1", "", state())

	if err := o.Ack(ctx, "s1", "c1", 2); !errors.Is(err, server.ErrInvalidArgument) {
		t.Fatalf("Ack = %v, want %v", err, server.ErrInvalidArgument)
	}

	if err := o.Ack(ctx, "nope", "c1", 0); !errors.Is(err, server.ErrSessionNotFound) {
		t.Fatalf("Ack of an unknown session = %v, want %v", err, server.ErrSessionNotFound)
	}
}

func TestRecordWakesTheWatchAndDeleteEndsIt(t *testing.T) {
	t.Parallel()

	o, _, ctx := open(t, "s1")
	w, _, err := o.Watch(ctx, "s1")
	if err != nil {
		t.Fatalf("Watch: %v", err)
	}
	changed := w.Changed()

	record(ctx, t, o, "s1", "", state())

	select {
	case <-changed:
	case <-ctx.Done():
		t.Fatal("the record did not wake the watch")
	}

	o.Delete("s1")

	select {
	case <-w.Deleted():
	case <-ctx.Done():
		t.Fatal("the delete did not end the watch")
	}

	again, _, err := o.Watch(ctx, "s1")
	if err != nil {
		t.Fatalf("Watch: %v", err)
	}

	select {
	case <-again.Deleted():
		t.Fatal("a watch after the delete is a new session's, and is not deleted")
	default:
	}
}

// A live reader must never skip a seq, also when writers commit concurrently: seqs come from the write transaction.
func TestConcurrentRecordsNeverLetAReaderSkipASeq(t *testing.T) {
	t.Parallel()

	const writers, each = 4, 50

	o, _, ctx := open(t, "s1")
	w, _, err := o.Watch(ctx, "s1")
	if err != nil {
		t.Fatalf("Watch: %v", err)
	}

	var wg sync.WaitGroup
	for range writers {
		wg.Go(func() {
			for range each {
				if _, err := o.Record(ctx, "s1", "", state()); err != nil {
					t.Errorf("Record: %v", err)

					return
				}
			}
		})
	}

	var got []uint64

	var after uint64
	for len(got) < writers*each {
		changed := w.Changed()

		events, err := o.Read(ctx, "s1", after, 100)
		if err != nil {
			t.Fatalf("Read: %v", err)
		}

		for _, e := range events {
			got = append(got, e.GetSeq())
			after = e.GetSeq()
		}

		if len(events) > 0 {
			continue
		}

		select {
		case <-changed:
		case <-ctx.Done():
			t.Fatalf("read %d of %d events, then nothing more came", len(got), writers*each)
		}
	}

	wg.Wait()

	if all := seqs(ctx, t, o, "s1"); !slices.Equal(got, all) {
		t.Fatalf("the live reader got %d seqs, the outbox has %d; they differ", len(got), len(all))
	}
}

func TestARepeatedKeyWithOtherContentIsReportedAndRecordedBeside(t *testing.T) {
	t.Parallel()

	o, _, ctx := open(t, "s1")

	if differs, err := o.Record(ctx, "s1", "k", message("M1")); err != nil || differs {
		t.Fatalf("first Record = %v, %v; want false, nil", differs, err)
	}

	if differs, err := o.Record(ctx, "s1", "k", message("M1")); err != nil || differs {
		t.Fatalf("an identical repeat = %v, %v; want false, nil", differs, err)
	}

	// A duplicate is better than a loss: the other content gets a row of its own, and the first stays as it was.
	if differs, err := o.Record(ctx, "s1", "k", message("M2")); err != nil || !differs {
		t.Fatalf("a repeat with other content = %v, %v; want true, nil", differs, err)
	}

	// A redispatch of that content is byte for byte the same, so it keeps its own row's seq.
	if _, err := o.Record(ctx, "s1", "k", message("M2")); err != nil {
		t.Fatalf("its redispatch: %v", err)
	}

	events, _ := o.Read(ctx, "s1", 0, 10)
	if len(events) != 2 || events[0].GetMessage().GetInfo().GetId() != "M1" || events[1].GetMessage().GetInfo().GetId() != "M2" {
		t.Fatalf("the outbox has %v; want M1, then M2 once", events)
	}
}

func TestWatchingAnUnknownSessionFailsAndKeepsNoWakeUp(t *testing.T) {
	t.Parallel()

	o, _, ctx := open(t, "s1")
	record(ctx, t, o, "s1", "", state())

	for range 3 {
		if _, _, err := o.Watch(ctx, "nope"); !errors.Is(err, server.ErrSessionNotFound) {
			t.Fatalf("Watch = %v, want %v", err, server.ErrSessionNotFound)
		}
	}

	_, recorded, err := o.Watch(ctx, "s1")
	if err != nil || recorded != 1 {
		t.Fatalf("Watch = %d, %v; want the highest seq, 1", recorded, err)
	}

	o.mu.Lock()
	defer o.mu.Unlock()

	if _, ok := o.feeds["nope"]; ok || len(o.feeds) != 1 {
		t.Fatalf("wake-ups for %d sessions, want only s1's", len(o.feeds))
	}
}
