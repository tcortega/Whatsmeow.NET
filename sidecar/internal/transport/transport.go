// Package transport opens the sidecar's private listener: a Unix domain socket in a directory the client owns,
// or on Windows a named pipe (ARCHITECTURE §8.6, §10).
package transport

import (
	"context"
	"crypto/rand"
	"encoding/hex"
	"errors"
	"fmt"
	"io/fs"
	"net"
	"os"
	"path/filepath"
	"strings"
)

// Unix and Pipe are the --transport values.
const (
	Unix = "unix"
	Pipe = "pipe"
)

// socketPrefix starts every launch's socket name inside the socket directory, followed by a 64-bit random suffix:
// short, for the 104-byte sun_path on macOS.
const socketPrefix = "s-"

// Listen opens the listener for kind and returns it with the endpoint the client connects to.
//
// For Unix, socketDir is the directory the client created for this WhatsmeowSidecar and reuses across restarts
// (ADR 0017). Every launch makes it owner-only again (an explicit DACL on Windows, where chmod sets none), removes the
// sockets earlier launches left behind, and listens at a name of its own, so a client still holding an earlier launch's
// endpoint never reaches this one (ADR 0045).
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

	// Only a real directory is made owner-only: never what a symlink points at, never a file.
	info, err := os.Lstat(socketDir)
	if err != nil {
		return nil, "", fmt.Errorf("check --socket-dir: %w", err)
	}

	if !info.IsDir() {
		return nil, "", fmt.Errorf("--socket-dir %s is not a directory (%s)", socketDir, info.Mode().Type())
	}

	if err := ownerOnly(socketDir); err != nil {
		return nil, "", fmt.Errorf("make %s owner-only: %w", socketDir, err)
	}

	if err := removeEarlierSockets(socketDir); err != nil {
		return nil, "", err
	}

	suffix := make([]byte, 8)
	_, _ = rand.Read(suffix) // crypto/rand.Read never returns an error

	path := filepath.Join(socketDir, socketPrefix+hex.EncodeToString(suffix))

	lis, err := (&net.ListenConfig{}).Listen(ctx, "unix", path)
	if err != nil {
		return nil, "", fmt.Errorf("listen on %s: %w", path, err)
	}

	return lis, path, nil
}

// removeEarlierSockets removes the sockets earlier launches left in the directory: a killed sidecar never removes its own.
// Anything that is not a socket stays. On Windows a ReadDir entry reports an AF_UNIX socket as fs.ModeSocket from its
// reparse tag (Go 1.27 os/types_windows.go).
func removeEarlierSockets(socketDir string) error {
	entries, err := os.ReadDir(socketDir)
	if err != nil {
		return fmt.Errorf("read %s: %w", socketDir, err)
	}

	for _, entry := range entries {
		if !strings.HasPrefix(entry.Name(), socketPrefix) || entry.Type()&fs.ModeSocket == 0 {
			continue
		}

		err := os.Remove(filepath.Join(socketDir, entry.Name()))
		if err != nil && !errors.Is(err, fs.ErrNotExist) {
			return fmt.Errorf("remove an earlier socket: %w", err)
		}
	}

	return nil
}
