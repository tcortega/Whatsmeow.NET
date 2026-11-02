package transport

import (
	"context"
	"io/fs"
	"net"
	"os"
	"path/filepath"
	"runtime"
	"strings"
	"testing"
)

// dial connects to endpoint over network and accepts the connection on lis.
func dial(t *testing.T, lis net.Listener, network, endpoint string) {
	t.Helper()

	accepted := make(chan error, 1)

	go func() {
		conn, err := lis.Accept()
		if err == nil {
			_ = conn.Close()
		}
		accepted <- err
	}()

	conn, err := (&net.Dialer{}).DialContext(context.Background(), network, endpoint)
	if err != nil {
		t.Fatalf("dial %s %s: %v", network, endpoint, err)
	}

	_ = conn.Close()

	if err := <-accepted; err != nil {
		t.Fatalf("accept: %v", err)
	}
}

func TestListenAddressServesAUnixSocketAtThePath(t *testing.T) {
	t.Parallel()

	path := filepath.Join(socketDir(t), "whatsmeow-net.sock")

	lis, endpoint, err := ListenAddress(context.Background(), "unix:"+path)
	if err != nil {
		t.Fatalf("listen: %v", err)
	}

	defer func() { _ = lis.Close() }()

	if endpoint != path {
		t.Fatalf("endpoint = %q, want %q", endpoint, path)
	}

	dial(t, lis, "unix", path)
}

func TestListenAddressServesTCP(t *testing.T) {
	t.Parallel()

	lis, endpoint, err := ListenAddress(context.Background(), "tcp:127.0.0.1:0")
	if err != nil {
		t.Fatalf("listen: %v", err)
	}

	defer func() { _ = lis.Close() }()

	if endpoint != lis.Addr().String() || strings.HasSuffix(endpoint, ":0") {
		t.Fatalf("endpoint = %q, want the bound address %q", endpoint, lis.Addr())
	}

	dial(t, lis, "tcp", endpoint)
}

// A killed sidecar leaves its socket on the shared volume; the next one replaces it.
func TestListenAddressReplacesAStaleSocket(t *testing.T) {
	t.Parallel()

	path := filepath.Join(socketDir(t), "s")

	stale, err := (&net.ListenConfig{}).Listen(context.Background(), "unix", path)
	if err != nil {
		t.Fatalf("listen: %v", err)
	}

	// Left behind, as by a killed process: closing would unlink it.
	stale.(*net.UnixListener).SetUnlinkOnClose(false)
	_ = stale.Close()

	if _, err := os.Lstat(path); err != nil {
		t.Fatalf("the stale socket is gone already: %v", err)
	}

	lis, _, err := ListenAddress(context.Background(), "unix:"+path)
	if err != nil {
		t.Fatalf("listen over the stale socket: %v", err)
	}

	defer func() { _ = lis.Close() }()

	dial(t, lis, "unix", path)
}

// The path is the operator's, on a volume of theirs: only a socket is ever removed.
func TestListenAddressNeverRemovesAFileThatIsNotASocket(t *testing.T) {
	t.Parallel()

	path := filepath.Join(socketDir(t), "s")
	if err := os.WriteFile(path, []byte("the operator's"), 0o600); err != nil {
		t.Fatalf("write: %v", err)
	}

	if lis, _, err := ListenAddress(context.Background(), "unix:"+path); err == nil {
		_ = lis.Close()
		t.Fatal("listen over a regular file succeeded")
	}

	if content, err := os.ReadFile(path); err != nil || string(content) != "the operator's" { //nolint:gosec // G304: the test's own file
		t.Fatalf("the file changed: %q, %v", content, err)
	}
}

func TestListenAddressRefusesWhatItCannotParse(t *testing.T) {
	t.Parallel()

	for _, address := range []string{"", "unix:", "unix:relative/s", "tcp:", "tcp:no-port", "pipe:x", "/abs/s", "127.0.0.1:7000"} {
		if lis, _, err := ListenAddress(context.Background(), address); err == nil {
			_ = lis.Close()
			t.Errorf("listen %q succeeded", address)
		}
	}
}

func TestTheUnixSocketIsForItsOwnerAndGroupOnly(t *testing.T) {
	t.Parallel()

	if runtime.GOOS == "windows" {
		t.Skip("Windows has no Unix file modes")
	}

	path := filepath.Join(socketDir(t), "s")

	lis, _, err := ListenAddress(context.Background(), "unix:"+path)
	if err != nil {
		t.Fatalf("listen: %v", err)
	}

	defer func() { _ = lis.Close() }()

	info, err := os.Lstat(path)
	if err != nil {
		t.Fatalf("lstat: %v", err)
	}

	if info.Mode().Type() != fs.ModeSocket || info.Mode().Perm() != 0o660 {
		t.Fatalf("mode = %v, want a socket with 0660", info.Mode())
	}
}
