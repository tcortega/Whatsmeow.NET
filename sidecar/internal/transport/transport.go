// Package transport opens the sidecar's private listener: a Unix domain socket in a directory the client owns,
// or on Windows a named pipe (ARCHITECTURE §8.6, §10).
package transport

import (
	"context"
	"errors"
	"fmt"
	"io/fs"
	"net"
	"os"
	"path/filepath"
)

// Unix and Pipe are the --transport values.
const (
	Unix = "unix"
	Pipe = "pipe"
)

// socketName is the socket's file name inside the socket directory; short, for the 104-byte sun_path on macOS.
const socketName = "s"

// Listen opens the listener for kind and returns it with the endpoint the client connects to.
//
// For Unix, socketDir is the directory the client created for this WhatsmeowSidecar and reuses across restarts
// (ADR 0017). Every launch makes it owner-only again (an explicit DACL on Windows, where chmod sets none) and
// removes the socket a killed sidecar left behind, which would otherwise fail the listen.
func Listen(ctx context.Context, kind, socketDir string) (net.Listener, string, error) {
	switch kind {
	case Unix:
		return listenUnix(ctx, socketDir)
	case Pipe:
		return listenPipe()
	default:
		return nil, "", fmt.Errorf("unknown transport %q", kind)
	}
}

func listenUnix(ctx context.Context, socketDir string) (net.Listener, string, error) {
	if socketDir == "" {
		return nil, "", errors.New("the unix transport needs --socket-dir")
	}

	if err := ownerOnly(socketDir); err != nil {
		return nil, "", fmt.Errorf("make %s owner-only: %w", socketDir, err)
	}

	path := filepath.Join(socketDir, socketName)
	if err := os.Remove(path); err != nil && !errors.Is(err, fs.ErrNotExist) {
		return nil, "", fmt.Errorf("remove the stale socket: %w", err)
	}

	lis, err := (&net.ListenConfig{}).Listen(ctx, "unix", path)
	if err != nil {
		return nil, "", fmt.Errorf("listen on %s: %w", path, err)
	}

	return lis, path, nil
}
