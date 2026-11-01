package transport

import (
	"context"
	"net"
	"os"
	"path/filepath"
	"testing"
)

// socketDir is a short directory: t.TempDir() names can pass the 104-byte sun_path limit on macOS.
func socketDir(t *testing.T) string {
	t.Helper()

	dir, err := os.MkdirTemp("", "wmn")
	if err != nil {
		t.Fatalf("mkdir: %v", err)
	}

	t.Cleanup(func() { _ = os.RemoveAll(dir) })

	return dir
}

func TestListenReplacesAStaleSocketFromACrashedRun(t *testing.T) {
	t.Parallel()

	dir := socketDir(t)
	stale := filepath.Join(dir, socketName)

	if err := os.WriteFile(stale, []byte("left by a killed sidecar"), 0o600); err != nil {
		t.Fatalf("write: %v", err)
	}

	lis, endpoint, err := Listen(context.Background(), Unix, dir)
	if err != nil {
		t.Fatalf("listen: %v", err)
	}

	defer func() { _ = lis.Close() }()

	if endpoint != stale {
		t.Fatalf("endpoint = %q, want %q", endpoint, stale)
	}

	accepted := make(chan error, 1)

	go func() {
		conn, err := lis.Accept()
		if err == nil {
			_ = conn.Close()
		}
		accepted <- err
	}()

	conn, err := (&net.Dialer{}).DialContext(context.Background(), "unix", endpoint)
	if err != nil {
		t.Fatalf("dial: %v", err)
	}

	_ = conn.Close()

	if err := <-accepted; err != nil {
		t.Fatalf("accept: %v", err)
	}
}

func TestListenRejectsAnUnknownTransport(t *testing.T) {
	t.Parallel()

	if _, _, err := Listen(context.Background(), "tcp", socketDir(t)); err == nil {
		t.Fatal("listen tcp succeeded")
	}
}

func TestListenRefusesASocketDirectoryThatIsAFile(t *testing.T) {
	t.Parallel()

	file := filepath.Join(socketDir(t), "file")
	if err := os.WriteFile(file, nil, 0o600); err != nil {
		t.Fatalf("write: %v", err)
	}

	if lis, _, err := Listen(context.Background(), Unix, file); err == nil {
		_ = lis.Close()
		t.Fatal("listen in a file succeeded")
	}

	if info, err := os.Stat(file); err != nil || info.Mode().Perm() != 0o600 {
		t.Fatalf("the file changed: %v, %v", info.Mode(), err)
	}
}
