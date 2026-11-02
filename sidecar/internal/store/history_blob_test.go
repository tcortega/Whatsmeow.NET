package store

import (
	"context"
	"database/sql"
	"path/filepath"
	"testing"
)

// v4File makes a database the way #9's sidecar left it: steps 1 to 4 at version 4, with a session, an event and a
// send record.
func v4File(t *testing.T) string {
	t.Helper()

	path := v1File(t)

	db, err := sql.Open("sqlite", path+pragmas)
	if err != nil {
		t.Fatalf("open: %v", err)
	}
	defer func() { _ = db.Close() }()

	if err := migrate(context.Background(), db, []string{schemaV1, schemaV2, schemaV3, schemaV4}); err != nil {
		t.Fatalf("steps 1 to 4: %v", err)
	}

	for _, statement := range []string{
		"INSERT INTO outbox (session_id, payload) VALUES ('live', x'01')",
		"INSERT INTO sent_message (session_id, client_message_id) VALUES ('live', '3EB0000000000000000000')",
	} {
		if _, err := db.ExecContext(context.Background(), statement); err != nil {
			t.Fatalf("%s: %v", statement, err)
		}
	}

	return path
}

func TestAVersion4FileGetsTheHistoryQueueAndKeepsItsRows(t *testing.T) {
	t.Parallel()

	s, err := openT(t, v4File(t))
	if err != nil {
		t.Fatalf("Open: %v", err)
	}

	if got := version(t, s.DB); got != len(steps()) {
		t.Fatalf("version %d, want %d", got, len(steps()))
	}

	var events, sends int
	if err := s.DB.QueryRowContext(context.Background(), "SELECT count(*) FROM outbox WHERE session_id = 'live'").Scan(&events); err != nil || events != 1 {
		t.Fatalf("outbox rows %d, %v; want the 1 recorded", events, err)
	}

	if err := s.DB.QueryRowContext(context.Background(), "SELECT count(*) FROM sent_message WHERE session_id = 'live'").Scan(&sends); err != nil || sends != 1 {
		t.Fatalf("sent_message rows %d, %v; want the 1 recorded", sends, err)
	}

	if _, err := s.DB.ExecContext(context.Background(), "INSERT INTO history_blob (session_id, message_id, notification) VALUES ('live', 'N1', x'')"); err != nil {
		t.Fatalf("queue a notification: %v", err)
	}
}

func TestAQueuedNotificationIsQueuedOnceAndGoesWithItsSession(t *testing.T) {
	t.Parallel()

	s, err := openT(t, filepath.Join(t.TempDir(), "whatsmeow-net.db"))
	if err != nil {
		t.Fatalf("Open: %v", err)
	}

	ctx := context.Background()
	for _, statement := range []string{
		"INSERT INTO session (id) VALUES ('s1'), ('s2')",
		"INSERT INTO history_blob (session_id, message_id, notification) VALUES ('s1', 'N1', x''), ('s2', 'N1', x'')",
		// whatsmeow dispatches a notification again after a crash before its receipt (message.go:545-552).
		"INSERT INTO history_blob (session_id, message_id, notification) VALUES ('s1', 'N1', x'') ON CONFLICT DO NOTHING",
		"DELETE FROM session WHERE id = 's1'",
	} {
		if _, err := s.DB.ExecContext(ctx, statement); err != nil {
			t.Fatalf("%s: %v", statement, err)
		}
	}

	var left []string

	rows, err := s.DB.QueryContext(ctx, "SELECT session_id FROM history_blob")
	if err != nil {
		t.Fatalf("read the queue: %v", err)
	}
	defer func() { _ = rows.Close() }()

	for rows.Next() {
		var id string
		if err := rows.Scan(&id); err != nil {
			t.Fatalf("scan: %v", err)
		}

		left = append(left, id)
	}

	if err := rows.Err(); err != nil || len(left) != 1 || left[0] != "s2" {
		t.Fatalf("queued notifications of %v, %v; want only s2's", left, err)
	}
}
