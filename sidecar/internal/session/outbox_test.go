package session

import (
	"context"
	"fmt"
	"slices"
	"strings"
	"sync"
	"testing"

	"github.com/rs/zerolog"
	"go.mau.fi/whatsmeow/store"
	"go.mau.fi/whatsmeow/types/events"
	"google.golang.org/protobuf/proto"
	"modernc.org/sqlite"

	whatsmeowv1 "github.com/tcortega/Whatsmeow.NET/sidecar/gen/whatsmeow/v1"
	"github.com/tcortega/Whatsmeow.NET/sidecar/internal/outbox"
	sidecarstore "github.com/tcortega/Whatsmeow.NET/sidecar/internal/store"
)

// countCommits counts the commits on the database at path from every connection opened after it, and returns the
// count so far. A test that calls it is not parallel: it adds a connection hook to the SQLite driver, which reads its
// hooks unlocked when it opens a connection, and Go runs the parallel tests only after the others.
func countCommits(t *testing.T, path string) func() int {
	t.Helper()

	var (
		mu      sync.Mutex
		commits int
	)

	sqlite.RegisterConnectionHook(func(conn sqlite.ExecQuerierContext, dsn string) error {
		if p, _, _ := strings.Cut(dsn, "?"); p != path {
			return nil
		}

		hooks, ok := conn.(sqlite.HookRegisterer)
		if !ok {
			return fmt.Errorf("the SQLite connection %T takes no hooks", conn)
		}

		hooks.RegisterCommitHook(func() int32 {
			mu.Lock()
			commits++
			mu.Unlock()

			return 0
		})

		return nil
	})

	return func() int {
		mu.Lock()
		defer mu.Unlock()

		return commits
	}
}

// recorded is the session's ConnectionStateEvents in the outbox, in seq order.
func (h *harness) recorded(id string) []*whatsmeowv1.SessionState {
	h.t.Helper()

	all, err := h.svc.box.Read(h.ctx, id, 0, 1000)
	if err != nil {
		h.t.Fatalf("read the outbox: %v", err)
	}

	states := make([]*whatsmeowv1.SessionState, 0, len(all))
	for _, e := range all {
		states = append(states, e.GetConnectionState().GetState())
	}

	return states
}

func (h *harness) wantRecorded(id string, want ...*whatsmeowv1.SessionState) {
	h.t.Helper()

	got := h.recorded(id)
	if len(got) != len(want) {
		h.t.Fatalf("session %q recorded %v, want %v", id, got, want)
	}

	for i := range got {
		if !proto.Equal(got[i], want[i]) {
			h.t.Fatalf("session %q recorded %v, want %v", id, got, want)
		}
	}
}

func TestEveryTransitionIsRecordedInOrderAsAConnectionStateEvent(t *testing.T) {
	t.Parallel()

	h := start(t)
	h.connected("s1")
	h.stub("s1").emit(&events.OfflineSyncCompleted{})
	h.stub("s1").emit(&events.Disconnected{})
	h.stub("s1").emit(&events.Connected{})

	if _, err := h.client.Disconnect(h.ctx, &whatsmeowv1.DisconnectRequest{SessionId: "s1"}); err != nil {
		t.Fatalf("Disconnect: %v", err)
	}

	// A late event that moves nothing records nothing.
	h.stub("s1").emit(&events.Connected{})

	h.wantRecorded("s1", disconnected(), connecting(), connected(), caughtUp(), reconnecting(), connected(), disconnected())
}

func TestARestartRecordsTheStateASessionComesBackIn(t *testing.T) {
	t.Parallel()

	h := start(t)
	h.create("created")
	h.connected("connected")
	h.connected("replaced")
	h.stub("replaced").emit(&events.StreamReplaced{})
	h.create("gone")
	h.link("gone", "1555:1@s.whatsapp.net")

	if _, err := h.db.DB.ExecContext(h.ctx, "DELETE FROM whatsmeow_device WHERE jid = '1555:1@s.whatsapp.net'"); err != nil {
		t.Fatalf("delete the device: %v", err)
	}

	h.stop()

	restarted := startOn(t, h.path)

	// A restart is a transition for a session it changes, and only for one.
	restarted.wantRecorded("created")
	restarted.wantRecorded("connected", disconnected(), connecting(), connected(), disconnected())
	restarted.wantRecorded("replaced", disconnected(), connecting(), connected(), replaced())
	restarted.wantRecorded("gone", disconnected(), loggedOut(0))
}

// TestARestartRecordsEveryMovedSessionInOneCommit is not parallel: it counts commits (countCommits).
func TestARestartRecordsEveryMovedSessionInOneCommit(t *testing.T) {
	const moved = 20

	h := start(t)
	for i := range moved {
		h.connected(fmt.Sprintf("s%02d", i))
	}

	h.create("created")
	h.stop()

	counted := countCommits(t, h.path)

	db, err := sidecarstore.Open(context.Background(), h.path, zerolog.Nop())
	if err != nil {
		t.Fatalf("open the store: %v", err)
	}

	t.Cleanup(func() { _ = db.Close() })

	before := counted()

	svc, err := New(context.Background(), db.DB, db.Writer, db.Devices, outbox.New(db.DB, db.Writer), func(_ string, device *store.Device, _ func()) Client {
		return &stub{device: device}
	}, zerolog.Nop())
	if err != nil {
		t.Fatalf("New: %v", err)
	}

	t.Cleanup(svc.Close)

	// One sync of the database before the ready line, however many sessions the restart moved (#77).
	if got := counted() - before; got != 1 {
		t.Fatalf("the restart of %d moved sessions made %d commits, want 1", moved, got)
	}

	for i := range moved {
		events, err := svc.box.Read(context.Background(), fmt.Sprintf("s%02d", i), 0, 1000)
		if err != nil {
			t.Fatalf("read the outbox: %v", err)
		}

		if last := events[len(events)-1].GetConnectionState().GetState(); !proto.Equal(last, disconnected()) {
			t.Fatalf("session s%02d last recorded %v, want Disconnected", i, last)
		}
	}
}

func TestARestartThatFailsPartwayRecordsNoSession(t *testing.T) {
	t.Parallel()

	// Loaded in this order, so the moves of the first two are written before the third one's fails.
	h := start(t)
	h.create("a-gone")
	h.link("a-gone", "1555:1@s.whatsapp.net")
	h.connected("b-connected")
	h.connected("c-refused")

	if _, err := h.db.DB.ExecContext(h.ctx, "DELETE FROM whatsmeow_device WHERE jid = '1555:1@s.whatsapp.net'"); err != nil {
		t.Fatalf("delete the device: %v", err)
	}

	h.stop()

	db, err := sidecarstore.Open(context.Background(), h.path, zerolog.Nop())
	if err != nil {
		t.Fatalf("open the store: %v", err)
	}

	t.Cleanup(func() { _ = db.Close() })

	ctx := context.Background()
	before := rows(ctx, t, db)

	if _, err := db.DB.ExecContext(ctx, `CREATE TRIGGER refuse BEFORE UPDATE ON session WHEN NEW.id = 'c-refused'
		BEGIN SELECT RAISE(ABORT, 'refused'); END`); err != nil {
		t.Fatalf("create the trigger: %v", err)
	}

	_, err = New(ctx, db.DB, db.Writer, db.Devices, outbox.New(db.DB, db.Writer), func(_ string, device *store.Device, _ func()) Client {
		return &stub{device: device}
	}, zerolog.Nop())
	if err == nil || !strings.Contains(err.Error(), "refused") {
		t.Fatalf("New = %v, want the refused update", err)
	}

	// a-gone's cleared JID, b-connected's Disconnected and every outbox row went with the failed one.
	if after := rows(ctx, t, db); !slices.Equal(after, before) {
		t.Fatalf("the failed restart left\n%v\nwant\n%v", after, before)
	}
}

// rows is every session row and outbox row in the store, as text.
func rows(ctx context.Context, t *testing.T, db *sidecarstore.Store) []string {
	t.Helper()

	var all []string

	for _, query := range []string{
		"SELECT id || ' ' || quote(jid) || ' ' || quote(lid) || ' ' || quote(stop_state) || ' ' || last_seq FROM session ORDER BY id",
		"SELECT session_id || ' ' || seq || ' ' || quote(payload) FROM outbox ORDER BY seq",
	} {
		rows, err := db.DB.QueryContext(ctx, query)
		if err != nil {
			t.Fatalf("read the store: %v", err)
		}

		for rows.Next() {
			var row string
			if err := rows.Scan(&row); err != nil {
				t.Fatalf("read the store: %v", err)
			}

			all = append(all, row)
		}

		if err := rows.Err(); err != nil {
			t.Fatalf("read the store: %v", err)
		}

		_ = rows.Close()
	}

	return all
}

func TestDeleteSessionDeletesItsEventsAndCursors(t *testing.T) {
	t.Parallel()

	h := start(t)
	h.connected("s1")
	h.connected("s2")

	if _, err := h.svc.box.Cursor(h.ctx, "s1", "c1"); err != nil {
		t.Fatalf("Cursor: %v", err)
	}

	w, _, err := h.svc.box.Watch(h.ctx, "s1")
	if err != nil {
		t.Fatalf("Watch: %v", err)
	}

	if _, err := h.client.DeleteSession(h.ctx, &whatsmeowv1.DeleteSessionRequest{SessionId: "s1"}); err != nil {
		t.Fatalf("DeleteSession: %v", err)
	}

	var events, cursors int
	if err := h.db.DB.QueryRowContext(h.ctx,
		"SELECT (SELECT count(*) FROM outbox WHERE session_id = 's1'), (SELECT count(*) FROM outbox_cursor WHERE session_id = 's1')",
	).Scan(&events, &cursors); err != nil {
		t.Fatalf("count: %v", err)
	}

	if events != 0 || cursors != 0 {
		t.Fatalf("after the delete: %d events and %d cursors, want none", events, cursors)
	}

	select {
	case <-w.Deleted():
	default:
		t.Fatal("the session's streams were not ended")
	}

	h.wantRecorded("s2", disconnected(), connecting(), connected())
}
