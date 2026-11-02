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
	"time"
)

// probeTimeout bounds the dial that tells a live socket at the path from one a killed sidecar left.
const probeTimeout = time.Second

// Address is a standalone sidecar's --listen (ADR 0046): a Unix domain socket's absolute path, or a TCP host and port.
type Address struct {
	// Network is Unix or "tcp".
	Network string
	// Endpoint is the socket's path, or host:port.
	Endpoint string
}

// ParseAddress reads "unix:<absolute path>" or "tcp:<host>:<port>". It checks the form only, so the sidecar refuses a
// malformed one before it opens anything.
func ParseAddress(address string) (Address, error) {
	network, rest, _ := strings.Cut(address, ":")

	switch network {
	case Unix:
		if !filepath.IsAbs(rest) {
			return Address{}, fmt.Errorf("the unix listen address %q is not an absolute path", rest)
		}
	case "tcp":
		if _, port, err := net.SplitHostPort(rest); err != nil || port == "" {
			return Address{}, fmt.Errorf("the tcp listen address %q is not <host>:<port>", rest)
		}
	default:
		return Address{}, fmt.Errorf("listen address %q is neither unix:<absolute path> nor tcp:<host>:<port>", address)
	}

	return Address{Network: network, Endpoint: rest}, nil
}

// ListenAddress opens the standalone sidecar's listener at listen (ParseAddress) and returns it with the endpoint it
// serves, the bound address for TCP.
//
// The socket's directory is the operator's, often a shared volume, so it is left as it is. A socket a killed sidecar
// left at the path is replaced; one a live sidecar serves, and anything that is not a socket, is refused. Closing the
// listener removes the socket only while the path is still this one's.
func ListenAddress(ctx context.Context, listen string) (net.Listener, string, error) {
	address, err := ParseAddress(listen)
	if err != nil {
		return nil, "", err
	}

	if address.Network == Unix {
		return listenUnixAt(ctx, address.Endpoint)
	}

	lis, err := (&net.ListenConfig{}).Listen(ctx, "tcp", address.Endpoint)
	if err != nil {
		return nil, "", fmt.Errorf("listen on %s: %w", address.Endpoint, err)
	}

	return lis, lis.Addr().String(), nil
}

func listenUnixAt(ctx context.Context, path string) (net.Listener, string, error) {
	stale, err := checkPath(ctx, path)
	if err != nil {
		return nil, "", err
	}

	lis, err := bindInPlace(ctx, path, stale)
	if err != nil {
		return nil, "", err
	}

	info, err := os.Lstat(path)
	if err != nil {
		_ = lis.Close()

		return nil, "", fmt.Errorf("check %s: %w", path, err)
	}

	return &ownedSocket{UnixListener: lis, path: path, info: info}, path, nil
}

// checkPath allows a free path and a stale socket, which it reports, and refuses a live socket and anything else.
func checkPath(ctx context.Context, path string) (bool, error) {
	info, err := os.Lstat(path)

	switch {
	case errors.Is(err, fs.ErrNotExist):
		return false, nil
	case err != nil:
		return false, fmt.Errorf("check %s: %w", path, err)
	case info.Mode().Type() != fs.ModeSocket:
		return false, fmt.Errorf("%s exists and is not a socket (%s)", path, info.Mode().Type())
	}

	ctx, cancel := context.WithTimeout(ctx, probeTimeout)
	defer cancel()

	conn, err := (&net.Dialer{}).DialContext(ctx, "unix", path)
	if err == nil {
		_ = conn.Close()

		return false, fmt.Errorf("%s is the socket of a sidecar that still runs", path)
	}

	if !refused(err) {
		return false, fmt.Errorf("check whether a sidecar still serves %s: %w", path, err)
	}

	return true, nil
}

// ownedSocket removes its socket on Close only while the path still names it: os.SameFile compares the inode.
type ownedSocket struct {
	*net.UnixListener
	path string
	info os.FileInfo
}

func (s *ownedSocket) Close() error {
	err := s.UnixListener.Close()

	if info, statErr := os.Lstat(s.path); statErr == nil && os.SameFile(info, s.info) {
		_ = os.Remove(s.path)
	}

	return err //nolint:wrapcheck // the listener's own Close
}
