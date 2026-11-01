package store

import (
	"context"
	"errors"
	"path/filepath"
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
