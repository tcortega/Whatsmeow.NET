//go:build !windows

package transport

import (
	"context"
	"os"
	"path/filepath"
	"testing"
)

func TestListenMakesTheSocketDirectoryOwnerOnly(t *testing.T) {
	t.Parallel()

	dir := socketDir(t)
	if err := os.Chmod(dir, 0o755); err != nil { //nolint:gosec // G302: the looser mode Listen must tighten
		t.Fatalf("chmod: %v", err)
	}

	lis, _, err := Listen(context.Background(), Unix, dir)
	if err != nil {
		t.Fatalf("listen: %v", err)
	}

	defer func() { _ = lis.Close() }()

	info, err := os.Stat(dir)
	if err != nil {
		t.Fatalf("stat: %v", err)
	}

	if mode := info.Mode().Perm(); mode != 0o700 {
		t.Fatalf("mode = %o, want 700", mode)
	}
}

func TestListenRefusesASocketDirectoryThatIsASymlink(t *testing.T) {
	t.Parallel()

	target := socketDir(t)
	if err := os.Chmod(target, 0o755); err != nil { //nolint:gosec // G302: a mode Listen must leave alone
		t.Fatalf("chmod: %v", err)
	}

	link := filepath.Join(socketDir(t), "link")
	if err := os.Symlink(target, link); err != nil {
		t.Fatalf("symlink: %v", err)
	}

	if lis, _, err := Listen(context.Background(), Unix, link); err == nil {
		_ = lis.Close()
		t.Fatal("listen through a symlink succeeded")
	}

	info, err := os.Stat(target)
	if err != nil || info.Mode().Perm() != 0o755 {
		t.Fatalf("the symlink's target changed: %v, %v", info.Mode(), err)
	}
}
