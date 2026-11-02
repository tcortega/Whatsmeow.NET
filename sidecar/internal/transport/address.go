package transport

import (
	"context"
	"errors"
	"fmt"
	"io/fs"
	"net"
	"os"
	"path/filepath"
	"strings"
)

// socketMode lets the socket's owner and group connect: a client in another container of the same pod reaches it
// through a shared group (Kubernetes fsGroup) or the same user. The token stays the real gate (ARCHITECTURE §10).
const socketMode = 0o660

// ListenAddress opens the standalone sidecar's listener (ADR 0046): "unix:<absolute path>", a Unix domain socket at
// that path, or "tcp:<host>:<port>". It returns the listener with the endpoint it serves, the bound address for TCP.
//
// The socket's directory is the operator's, often a shared volume, so it is left as it is; a socket a killed sidecar
// left at the path is removed, and anything else there is refused.
func ListenAddress(ctx context.Context, address string) (net.Listener, string, error) {
	network, rest, _ := strings.Cut(address, ":")

	switch network {
	case Unix:
		return listenUnixAt(ctx, rest)
	case "tcp":
		return listenTCP(ctx, rest)
	default:
		return nil, "", fmt.Errorf("listen address %q is neither unix:<absolute path> nor tcp:<host>:<port>", address)
	}
}

func listenUnixAt(ctx context.Context, path string) (net.Listener, string, error) {
	if !filepath.IsAbs(path) {
		return nil, "", fmt.Errorf("the unix listen address %q is not an absolute path", path)
	}

	info, err := os.Lstat(path)

	switch {
	case errors.Is(err, fs.ErrNotExist):
	case err != nil:
		return nil, "", fmt.Errorf("check %s: %w", path, err)
	case info.Mode().Type() != fs.ModeSocket:
		return nil, "", fmt.Errorf("%s exists and is not a socket (%s)", path, info.Mode().Type())
	default:
		if err := os.Remove(path); err != nil {
			return nil, "", fmt.Errorf("remove the stale socket: %w", err)
		}
	}

	lis, err := (&net.ListenConfig{}).Listen(ctx, "unix", path)
	if err != nil {
		return nil, "", fmt.Errorf("listen on %s: %w", path, err)
	}

	if err := os.Chmod(path, socketMode); err != nil {
		_ = lis.Close()

		return nil, "", fmt.Errorf("chmod %s: %w", path, err)
	}

	return lis, path, nil
}

func listenTCP(ctx context.Context, hostPort string) (net.Listener, string, error) {
	if _, port, err := net.SplitHostPort(hostPort); err != nil || port == "" {
		return nil, "", fmt.Errorf("the tcp listen address %q is not <host>:<port>", hostPort)
	}

	lis, err := (&net.ListenConfig{}).Listen(ctx, "tcp", hostPort)
	if err != nil {
		return nil, "", fmt.Errorf("listen on %s: %w", hostPort, err)
	}

	return lis, lis.Addr().String(), nil
}
