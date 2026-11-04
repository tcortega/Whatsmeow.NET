//go:build !windows

package transport

import (
	"context"
	"net"
	"os"
	"path/filepath"
	"strings"
	"sync"
	"syscall"
	"testing"
)

// unixPaths returns paths `--listen unix:<path>` accepts and ones it refuses here.
func unixPaths() (absolute, notAbsolute []string) {
	return []string{"/abs/s"}, []string{"relative/s"}
}

// rounds of each race: one round's two listens may not overlap, so a test that wants them to runs many.
const rounds = 50

// listenTogether starts two listens on path at the same moment and returns the ones that succeeded.
func listenTogether(t *testing.T, path string) []net.Listener {
	t.Helper()

	var (
		start sync.WaitGroup
		done  sync.WaitGroup
		mu    sync.Mutex
		won   []net.Listener
	)

	start.Add(1)

	for range 2 {
		done.Go(func() {
			start.Wait()

			lis, _, err := ListenAddress(context.Background(), "unix:"+path)
			if err != nil {
				return
			}

			mu.Lock()
			won = append(won, lis)
			mu.Unlock()
		})
	}

	start.Done()
	done.Wait()

	return won
}

// checkOneWon checks that exactly one listen has the path, and that once the loser returned, which is its exit, the
// path still reaches the winner and nothing of the loser is left beside it.
func checkOneWon(t *testing.T, round int, dir string, won []net.Listener) {
	t.Helper()

	defer func() {
		for _, lis := range won {
			_ = lis.Close()
		}
	}()

	if len(won) != 1 {
		t.Fatalf("round %d: %d listens succeeded, want exactly one", round, len(won))
	}

	entries, err := os.ReadDir(dir)
	if err != nil || len(entries) != 1 || entries[0].Name() != "s" {
		t.Fatalf("round %d: the directory holds %v, %v; want only the socket", round, entries, err)
	}

	dial(t, won[0], "unix", filepath.Join(dir, "s"))
}

// Two sidecars an orchestrator starts at once on one free path: one has it, the other fails, and it never replaced
// the first one's socket, so the first one's clients reach the sidecar that answers them.
func TestListenAddressGivesAFreePathToOneOfTwoSidecarsStartedTogether(t *testing.T) {
	t.Parallel()

	for round := range rounds {
		dir := socketDir(t)
		checkOneWon(t, round, dir, listenTogether(t, filepath.Join(dir, "s")))
	}
}

// The same with a killed sidecar's socket at the path: both find it dead, and only one replaces it.
func TestListenAddressGivesAStaleSocketToOneOfTwoSidecarsStartedTogether(t *testing.T) {
	t.Parallel()

	for round := range rounds {
		dir := socketDir(t)
		path := filepath.Join(dir, "s")
		leaveStaleSocket(t, path)
		checkOneWon(t, round, dir, listenTogether(t, path))
	}
}

// leaveStaleSocket binds a socket at path and leaves it there with nobody listening, as a killed sidecar does.
func leaveStaleSocket(t *testing.T, path string) {
	t.Helper()

	stale, err := (&net.ListenConfig{}).Listen(context.Background(), "unix", path)
	if err != nil {
		t.Fatalf("listen: %v", err)
	}

	stale.(*net.UnixListener).SetUnlinkOnClose(false) //nolint:forcetypeassert // a unix listen returns one
	_ = stale.Close()
}

// sunPath is the longest path a Unix socket binds at: sun_path less its final NUL.
const sunPath = len(syscall.RawSockaddrUnix{}.Path) - 1

// dirOfLength makes a directory whose path is exactly length bytes long.
func dirOfLength(t *testing.T, length int) string {
	t.Helper()

	base := socketDir(t)

	pad := length - len(base) - 1
	if pad < 1 {
		t.Fatalf("the temporary directory %s is too long for a %d-byte directory", base, length)
	}

	dir := filepath.Join(base, strings.Repeat("d", pad))
	if err := os.Mkdir(dir, 0o700); err != nil {
		t.Fatalf("mkdir: %v", err)
	}

	return dir
}

// The socket is bound in a directory beside the path first, so the bound path is longer than the path: by little
// enough that a path 14 bytes shorter than the limit binds.
func TestListenAddressBindsAPathCloseToTheLimit(t *testing.T) {
	t.Parallel()

	path := filepath.Join(dirOfLength(t, sunPath-16), "s")

	lis, _, err := ListenAddress(context.Background(), "unix:"+path)
	if err != nil {
		t.Fatalf("listen on a %d-byte path, the limit %d: %v", len(path), sunPath, err)
	}

	defer func() { _ = lis.Close() }()

	dial(t, lis, "unix", path)
}

// A path that fits, but whose bound path beside it does not, fails saying so, and names both.
func TestListenAddressSaysWhenTheBoundPathIsTooLong(t *testing.T) {
	t.Parallel()

	dir := dirOfLength(t, sunPath-3)
	path := filepath.Join(dir, "s")

	lis, _, err := ListenAddress(context.Background(), "unix:"+path)
	if err == nil {
		_ = lis.Close()
		t.Fatalf("listen on %s succeeded", path)
	}

	message := err.Error()
	if !strings.Contains(message, "too long") || !strings.Contains(message, path+" ") || !strings.Contains(message, dir+"/.") {
		t.Fatalf("error = %q, want one that says the path is too long and names %s and the path it is bound at first", message, path)
	}

	if entries, err := os.ReadDir(dir); err != nil || len(entries) != 0 {
		t.Fatalf("the directory holds %v, %v; want nothing", entries, err)
	}
}
