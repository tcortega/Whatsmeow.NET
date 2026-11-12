package store

import (
	"context"
	"errors"
	"fmt"
	"path/filepath"
	"strings"
	"testing"

	"github.com/rs/zerolog"
)

func openT(t *testing.T, path string) (*Store, error) {
	t.Helper()

	s, err := Open(context.Background(), path, zerolog.Nop())
	if err == nil {
		t.Cleanup(func() { _ = s.Close() })
	}

	return s, err
}

func TestOpenFailsWhileAnotherStoreHoldsTheFile(t *testing.T) {
	t.Parallel()

	path := filepath.Join(t.TempDir(), "whatsmeow-net.db")
	if _, err := openT(t, path); err != nil {
		t.Fatalf("first Open: %v", err)
	}

	if _, err := openT(t, path); !errors.Is(err, ErrInUse) {
		t.Fatalf("second Open = %v, want %v", err, ErrInUse)
	}
}

func TestCloseReleasesTheLock(t *testing.T) {
	t.Parallel()

	path := filepath.Join(t.TempDir(), "whatsmeow-net.db")

	first, err := Open(context.Background(), path, zerolog.Nop())
	if err != nil {
		t.Fatalf("first Open: %v", err)
	}

	if err := first.Close(); err != nil {
		t.Fatalf("Close: %v", err)
	}

	if _, err := openT(t, path); err != nil {
		t.Fatalf("Open after Close: %v", err)
	}
}

func TestOpenTurnsForeignKeysOnAndCreatesTheTables(t *testing.T) {
	t.Parallel()

	s, err := openT(t, filepath.Join(t.TempDir(), "whatsmeow-net.db"))
	if err != nil {
		t.Fatalf("Open: %v", err)
	}

	var foreignKeys bool
	if err := s.DB.QueryRowContext(context.Background(), "PRAGMA foreign_keys").Scan(&foreignKeys); err != nil || !foreignKeys {
		t.Fatalf("foreign_keys = %v, %v; want true", foreignKeys, err)
	}

	var sessions int
	if err := s.DB.QueryRowContext(context.Background(), "SELECT count(*) FROM session").Scan(&sessions); err != nil {
		t.Fatalf("the session table: %v", err)
	}
}

func TestOpenRefusesAPathWithAQuery(t *testing.T) {
	t.Parallel()

	if _, err := openT(t, filepath.Join(t.TempDir(), "a?mode=ro")); err == nil {
		t.Fatal("Open accepted a path with '?'")
	}
}

func TestTheBusyTimeoutIsTheOneSQLiteGets(t *testing.T) {
	t.Parallel()

	if want := fmt.Sprintf("busy_timeout(%d)", BusyTimeout.Milliseconds()); !strings.Contains(pragmas, want) {
		t.Fatalf("pragmas are %s, want %s", pragmas, want)
	}
}

func TestTheOutboxReadOfASessionIsARangeScanOfItsIndex(t *testing.T) {
	t.Parallel()

	s, err := openT(t, filepath.Join(t.TempDir(), "whatsmeow-net.db"))
	if err != nil {
		t.Fatalf("Open: %v", err)
	}

	// The query of outbox.Read, copied: without the index SQLite walks every row the session keeps and sorts them for
	// each batch.
	rows, err := s.DB.QueryContext(context.Background(),
		"EXPLAIN QUERY PLAN SELECT seq, payload FROM outbox WHERE session_id = ? AND seq > ? ORDER BY seq LIMIT ?", "s1", 0, 100)
	if err != nil {
		t.Fatalf("explain: %v", err)
	}
	defer func() { _ = rows.Close() }()

	var plan []string
	for rows.Next() {
		var (
			id, parent, notUsed int
			detail              string
		)
		if err := rows.Scan(&id, &parent, &notUsed, &detail); err != nil {
			t.Fatalf("scan the plan: %v", err)
		}

		plan = append(plan, detail)
	}

	if err := rows.Err(); err != nil {
		t.Fatalf("read the plan: %v", err)
	}

	joined := strings.Join(plan, "; ")
	if !strings.Contains(joined, "outbox_session_seq") || strings.Contains(joined, "TEMP B-TREE") {
		t.Fatalf("plan %q, want a search of outbox_session_seq with no temp b-tree", joined)
	}
}
