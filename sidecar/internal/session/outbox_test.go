package session

import (
	"testing"

	"go.mau.fi/whatsmeow/types/events"
	"google.golang.org/protobuf/proto"

	whatsmeowv1 "github.com/tcortega/Whatsmeow.NET/sidecar/gen/whatsmeow/v1"
)

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
