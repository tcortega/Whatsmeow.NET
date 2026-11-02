package transport

import (
	"context"
	"errors"
	"fmt"
	"net"
	"os"

	"golang.org/x/sys/windows"
)

// bindInPlace binds the socket at path, after removing the stale one there. Windows has no file mode for it to take:
// the socket file inherits its directory's ACL, which is the operator's.
func bindInPlace(ctx context.Context, path string, stale bool) (*net.UnixListener, error) {
	if stale {
		if err := os.Remove(path); err != nil {
			return nil, fmt.Errorf("remove the stale socket: %w", err)
		}
	}

	lis, err := (&net.ListenConfig{}).Listen(ctx, "unix", path)
	if err != nil {
		return nil, fmt.Errorf("listen on %s: %w", path, err)
	}

	unix := lis.(*net.UnixListener) //nolint:forcetypeassert // a unix listen returns one
	unix.SetUnlinkOnClose(false)

	return unix, nil
}

// refused reports a dial to a socket nobody listens on: a killed sidecar's.
func refused(err error) bool {
	return errors.Is(err, windows.WSAECONNREFUSED)
}
