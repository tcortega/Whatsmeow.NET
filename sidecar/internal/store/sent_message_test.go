package store

import (
	"context"
	"database/sql"
	"path/filepath"
	"testing"
)

// v3File makes a database the way #28's sidecar left it: steps 1 to 3 at version 3, with a session, an event and a
// pending media retry.
func v3File(t *testing.T) string {
	t.Helper()

	path := v1File(t)

	db, err := sql.Open("sqlite", path+pragmas)
	if err != nil {
		t.Fatalf("open: %v", err)
	}
	defer func() { _ = db.Close() }()

	if err := migrate(context.Background(), db, []string{schemaV1, schemaV2, schemaV3}); err != nil {
		t.Fatalf("steps 1 to 3: %v", err)
	}

	for _, statement := range []string{
		"INSERT INTO outbox (session_id, payload) VALUES ('live', x'01')",
		"INSERT INTO media_retry (session_id, message_id, chat, media) VALUES ('live', 'M1', 'c@s.whatsapp.net', x'')",
	} {
		if _, err := db.ExecContext(context.Background(), statement); err != nil {
			t.Fatalf("%s: %v", statement, err)
		}
	}

	return path
}

func TestAVersion3FileGetsTheSendRecordsAndKeepsItsRows(t *testing.T) {
	t.Parallel()

	s, err := openT(t, v3File(t))
	if err != nil {
		t.Fatalf("Open: %v", err)
	}

	if got := version(t, s.DB); got != len(steps()) {
		t.Fatalf("version %d, want %d", got, len(steps()))
	}

	var events, retries int
	if err := s.DB.QueryRowContext(context.Background(), "SELECT count(*) FROM outbox WHERE session_id = 'live'").Scan(&events); err != nil || events != 1 {
		t.Fatalf("outbox rows %d, %v; want the 1 recorded", events, err)
	}

	if err := s.DB.QueryRowContext(context.Background(), "SELECT count(*) FROM media_retry WHERE session_id = 'live'").Scan(&retries); err != nil || retries != 1 {
		t.Fatalf("media_retry rows %d, %v; want the 1 recorded", retries, err)
	}

	if _, err := s.DB.ExecContext(context.Background(), "INSERT INTO sent_message (session_id, client_message_id) VALUES ('live', '3EB0000000000000000000')"); err != nil {
		t.Fatalf("record a send: %v", err)
	}
}

func TestDeletingASessionDeletesItsSendRecords(t *testing.T) {
	t.Parallel()

	s, err := openT(t, filepath.Join(t.TempDir(), "whatsmeow-net.db"))
	if err != nil {
		t.Fatalf("Open: %v", err)
	}

	ctx := context.Background()
	for _, statement := range []string{
		"INSERT INTO session (id) VALUES ('s1'), ('s2')",
		"INSERT INTO sent_message (session_id, client_message_id) VALUES ('s1', 'A'), ('s2', 'A')",
		"DELETE FROM session WHERE id = 's1'",
	} {
		if _, err := s.DB.ExecContext(ctx, statement); err != nil {
			t.Fatalf("%s: %v", statement, err)
		}
	}

	var left []string

	rows, err := s.DB.QueryContext(ctx, "SELECT session_id FROM sent_message")
	if err != nil {
		t.Fatalf("read the send records: %v", err)
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
		t.Fatalf("send records of %v, %v; want only s2's", left, err)
	}
}
