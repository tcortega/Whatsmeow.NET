package store

import (
	"bytes"
	"context"
	"database/sql"
	"fmt"
	"path/filepath"
	"testing"

	"github.com/rs/zerolog"
)

func version(t *testing.T, db *sql.DB) int {
	t.Helper()

	var v int
	if err := db.QueryRowContext(context.Background(), "PRAGMA user_version").Scan(&v); err != nil {
		t.Fatalf("read the version: %v", err)
	}

	return v
}

// v1File makes a database the way #6's sidecar left it: its schema, applied without a version, and a session.
func v1File(t *testing.T) string {
	t.Helper()

	path := filepath.Join(t.TempDir(), "whatsmeow-net.db")

	db, err := sql.Open("sqlite", path+pragmas)
	if err != nil {
		t.Fatalf("open: %v", err)
	}
	defer func() { _ = db.Close() }()

	if _, err := db.ExecContext(context.Background(), schemaV1); err != nil {
		t.Fatalf("the v1 schema: %v", err)
	}

	if _, err := db.ExecContext(context.Background(), "INSERT INTO session (id, jid, stop_state) VALUES ('live', '1555:1@s.whatsapp.net', x'01')"); err != nil {
		t.Fatalf("the session row: %v", err)
	}

	return path
}

func TestAFreshFileEndsAtTheLatestVersion(t *testing.T) {
	t.Parallel()

	s, err := openT(t, filepath.Join(t.TempDir(), "whatsmeow-net.db"))
	if err != nil {
		t.Fatalf("Open: %v", err)
	}

	if got := version(t, s.DB); got != len(steps()) {
		t.Fatalf("version %d, want %d", got, len(steps()))
	}

	if _, err := s.DB.ExecContext(context.Background(), "INSERT INTO session (id) VALUES ('s1'); INSERT INTO outbox (session_id, payload) VALUES ('s1', x'')"); err != nil {
		t.Fatalf("the latest tables: %v", err)
	}
}

func TestAVersion1FileUpgradesAndKeepsItsSessions(t *testing.T) {
	t.Parallel()

	s, err := openT(t, v1File(t))
	if err != nil {
		t.Fatalf("Open: %v", err)
	}

	if got := version(t, s.DB); got != len(steps()) {
		t.Fatalf("version %d, want %d", got, len(steps()))
	}

	var (
		jid     string
		stopped []byte
		lastSeq int
	)
	if err := s.DB.QueryRowContext(context.Background(), "SELECT jid, stop_state, last_seq FROM session WHERE id = 'live'").Scan(&jid, &stopped, &lastSeq); err != nil {
		t.Fatalf("the session after the upgrade: %v", err)
	}

	if jid != "1555:1@s.whatsapp.net" || !bytes.Equal(stopped, []byte{1}) || lastSeq != 0 {
		t.Fatalf("session jid %q, stop_state %v, last_seq %d; want it kept, with last_seq 0", jid, stopped, lastSeq)
	}
}

func TestReopeningAnUpToDateFileChangesNothing(t *testing.T) {
	t.Parallel()

	path := v1File(t)

	first, err := Open(context.Background(), path, zerolog.Nop())
	if err != nil {
		t.Fatalf("first Open: %v", err)
	}

	if _, err := first.DB.ExecContext(context.Background(), "INSERT INTO outbox (session_id, payload) VALUES ('live', x'')"); err != nil {
		t.Fatalf("record: %v", err)
	}

	if err := first.Close(); err != nil {
		t.Fatalf("Close: %v", err)
	}

	// A step that ran again would fail: ALTER TABLE ADD COLUMN and CREATE TABLE are not idempotent.
	again, err := openT(t, path)
	if err != nil {
		t.Fatalf("second Open: %v", err)
	}

	var events int
	if err := again.DB.QueryRowContext(context.Background(), "SELECT count(*) FROM outbox").Scan(&events); err != nil || events != 1 {
		t.Fatalf("outbox rows %d, %v; want the 1 recorded", events, err)
	}
}

func TestAFileFromANewerSidecarIsRefused(t *testing.T) {
	t.Parallel()

	path := v1File(t)

	db, err := sql.Open("sqlite", path+pragmas)
	if err != nil {
		t.Fatalf("open: %v", err)
	}

	if _, err := db.ExecContext(context.Background(), fmt.Sprintf("PRAGMA user_version = %d", len(steps())+1)); err != nil {
		t.Fatalf("set the version: %v", err)
	}

	_ = db.Close()

	if _, err := openT(t, path); err == nil {
		t.Fatal("Open accepted a database from a newer whatsmeow-net")
	}
}

func TestEveryConnectionWritesWithSynchronousFull(t *testing.T) {
	t.Parallel()

	s, err := openT(t, filepath.Join(t.TempDir(), "whatsmeow-net.db"))
	if err != nil {
		t.Fatalf("Open: %v", err)
	}

	// FULL is 2 (sqlite.org/pragma.html#pragma_synchronous): invariant 2 needs the commit to survive power loss. The
	// pragma is per connection, so three are held open at once and each is checked.
	for i := range 3 {
		conn, err := s.DB.Conn(context.Background())
		if err != nil {
			t.Fatalf("connection %d: %v", i, err)
		}
		defer func() { _ = conn.Close() }()

		var synchronous int
		if err := conn.QueryRowContext(context.Background(), "PRAGMA synchronous").Scan(&synchronous); err != nil || synchronous != 2 {
			t.Fatalf("connection %d: synchronous = %d, %v; want 2 (FULL)", i, synchronous, err)
		}
	}
}

func TestAFailingStepLeavesTheVersionAndTheSchemaAsTheyWere(t *testing.T) {
	t.Parallel()

	db, err := sql.Open("sqlite", v1File(t)+pragmas)
	if err != nil {
		t.Fatalf("open: %v", err)
	}
	defer func() { _ = db.Close() }()

	failing := []string{schemaV1, "CREATE TABLE half_done (a); SELECT no_such_column FROM session"}
	if err := migrate(context.Background(), db, failing); err == nil {
		t.Fatal("a failing step succeeded")
	}

	var tables int
	if err := db.QueryRowContext(context.Background(), "SELECT count(*) FROM sqlite_master WHERE name = 'half_done'").Scan(&tables); err != nil || tables != 0 {
		t.Fatalf("half_done tables %d, %v; want the step rolled back", tables, err)
	}

	if got := version(t, db); got != 0 {
		t.Fatalf("version %d, want 0 as it was", got)
	}
}

// v2File makes a database the way #8's sidecar left it: steps 1 and 2 at version 2, and a session.
func v2File(t *testing.T) string {
	t.Helper()

	path := v1File(t)

	db, err := sql.Open("sqlite", path+pragmas)
	if err != nil {
		t.Fatalf("open: %v", err)
	}
	defer func() { _ = db.Close() }()

	if err := migrate(context.Background(), db, []string{schemaV1, schemaV2}); err != nil {
		t.Fatalf("steps 1 and 2: %v", err)
	}

	return path
}

func TestAVersion2FileGetsTheMediaRetryTableAndKeepsItsSessions(t *testing.T) {
	t.Parallel()

	s, err := openT(t, v2File(t))
	if err != nil {
		t.Fatalf("Open: %v", err)
	}

	if got := version(t, s.DB); got != len(steps()) {
		t.Fatalf("version %d, want %d", got, len(steps()))
	}

	if _, err := s.DB.ExecContext(context.Background(), "INSERT INTO media_retry (session_id, message_id, chat, media) VALUES ('live', 'M1', 'c@s.whatsapp.net', x'')"); err != nil {
		t.Fatalf("a pending media retry of the kept session: %v", err)
	}

	// A session's pending media retries go with it.
	if _, err := s.DB.ExecContext(context.Background(), "DELETE FROM session WHERE id = 'live'"); err != nil {
		t.Fatalf("delete the session: %v", err)
	}

	var pending int
	if err := s.DB.QueryRowContext(context.Background(), "SELECT count(*) FROM media_retry").Scan(&pending); err != nil || pending != 0 {
		t.Fatalf("media_retry rows %d, %v; want none after the session was deleted", pending, err)
	}
}
