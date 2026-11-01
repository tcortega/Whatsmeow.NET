//go:build !windows

package store

import (
	"os"
	"path/filepath"
	"testing"
)

func TestOpenCreatesTheDatabaseOwnerOnly(t *testing.T) {
	t.Parallel()

	path := filepath.Join(t.TempDir(), "whatsmeow-net.db")
	if _, err := openT(t, path); err != nil {
		t.Fatalf("Open: %v", err)
	}

	for _, file := range []string{path, path + ".lock", path + "-wal"} {
		info, err := os.Stat(file)
		if err != nil {
			t.Fatalf("stat %s: %v", file, err)
		}

		if mode := info.Mode().Perm(); mode != 0o600 {
			t.Errorf("%s mode = %v, want 0600", filepath.Base(file), mode)
		}
	}
}
