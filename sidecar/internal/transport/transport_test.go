package transport

import (
	"context"
	"errors"
	"io/fs"
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

func TestListenNeverReusesAnEarlierLaunchsEndpoint(t *testing.T) {
	t.Parallel()

	dir := socketDir(t)

	// A killed sidecar leaves its socket behind: this one is never closed, so nothing removes it but the next Listen.
	killed, earlier, err := Listen(context.Background(), Unix, dir)
	if err != nil {
		t.Fatalf("listen: %v", err)
	}

	defer func() { _ = killed.Close() }()

	lis, endpoint, err := Listen(context.Background(), Unix, dir)
	if err != nil {
		t.Fatalf("listen again: %v", err)
	}

	defer func() { _ = lis.Close() }()

	if filepath.Dir(endpoint) != dir || endpoint == earlier {
		t.Fatalf("endpoint = %q, want a new one in %q, not %q", endpoint, dir, earlier)
	}

	// The earlier launch's socket is gone, so a client of that launch never reaches this one there.
	if _, err := os.Lstat(earlier); !errors.Is(err, fs.ErrNotExist) {
		t.Fatalf("the earlier socket %q is still there: %v", earlier, err)
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

	before, err := os.Stat(file)
	if err != nil {
		t.Fatalf("stat: %v", err)
	}

	if lis, _, err := Listen(context.Background(), Unix, file); err == nil {
		_ = lis.Close()
		t.Fatal("listen in a file succeeded")
	}

	// Compared with itself: Windows reports a writable file as 0666 whatever it was created with.
	if after, err := os.Stat(file); err != nil || after.Mode() != before.Mode() {
		t.Fatalf("the file changed: %v to %v, %v", before.Mode(), after.Mode(), err)
	}
}
