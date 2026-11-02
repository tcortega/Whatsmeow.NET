//go:build !windows

package transport

import (
	"context"
	"errors"
	"fmt"
	"net"
	"os"
	"path/filepath"
	"syscall"
)

// socketMode lets the socket's owner and group connect: a client in another container of the same pod reaches it
// through a shared group (Kubernetes fsGroup) or the same user. The token stays the real gate (ARCHITECTURE §10).
const socketMode = 0o660

// bindInPlace binds the socket in a new 0700 directory beside path, gives it its mode there, where nobody can reach
// it, and renames it into place: no moment with the umask's mode, and a symlink swapped in at path is replaced, never
// followed. A rename replaces a stale socket atomically.
func bindInPlace(ctx context.Context, path string, _ bool) (*net.UnixListener, error) {
	private, err := os.MkdirTemp(filepath.Dir(path), ".whatsmeow-net-")
	if err != nil {
		return nil, fmt.Errorf("make a private directory beside %s: %w", path, err)
	}

	defer func() { _ = os.RemoveAll(private) }()

	bound := filepath.Join(private, "s")

	lis, err := (&net.ListenConfig{}).Listen(ctx, "unix", bound)
	if err != nil {
		return nil, fmt.Errorf("listen on %s: %w", path, err)
	}

	unix := lis.(*net.UnixListener) //nolint:forcetypeassert // a unix listen returns one
	// ownedSocket removes it, by its path, only while that is still this socket.
	unix.SetUnlinkOnClose(false)

	if err := os.Chmod(bound, socketMode); err != nil {
		_ = unix.Close()

		return nil, fmt.Errorf("chmod %s: %w", path, err)
	}

	if err := os.Rename(bound, path); err != nil {
		_ = unix.Close()

		return nil, fmt.Errorf("move the socket to %s: %w", path, err)
	}

	return unix, nil
}

// refused reports a dial to a socket nobody listens on: a killed sidecar's.
func refused(err error) bool {
	return errors.Is(err, syscall.ECONNREFUSED)
}
