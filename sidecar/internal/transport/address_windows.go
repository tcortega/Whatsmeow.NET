package transport

import (
	"context"
	"errors"
	"fmt"
	"net"
	"os"

	"golang.org/x/sys/windows"
)

// bindAt binds the socket at path, after removing the dead one there, and returns the socket's file. Windows has no
// file mode for it to take: the socket file inherits its directory's ACL, which is the operator's.
func bindAt(ctx context.Context, path string, dead os.FileInfo) (*net.UnixListener, os.FileInfo, error) {
	if dead != nil {
		if err := os.Remove(path); err != nil {
			return nil, nil, fmt.Errorf("remove the stale socket: %w", err)
		}
	}

	lis, err := (&net.ListenConfig{}).Listen(ctx, "unix", path)
	if err != nil {
		return nil, nil, fmt.Errorf("listen on %s: %w", path, err)
	}

	unix := lis.(*net.UnixListener) //nolint:forcetypeassert // a unix listen returns one
	unix.SetUnlinkOnClose(false)

	info, err := os.Lstat(path)
	if err != nil {
		_ = unix.Close()

		return nil, nil, fmt.Errorf("check %s: %w", path, err)
	}

	return unix, info, nil
}

// refused reports a dial to a socket nobody listens on: a killed sidecar's.
func refused(err error) bool {
	return errors.Is(err, windows.WSAECONNREFUSED)
}
