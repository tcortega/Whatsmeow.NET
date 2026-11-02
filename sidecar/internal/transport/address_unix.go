//go:build !windows

package transport

import (
	"context"
	"errors"
	"fmt"
	"io/fs"
	"net"
	"os"
	"path/filepath"
	"syscall"
)

// socketMode lets the socket's owner and group connect: a client in another container of the same pod reaches it
// through a shared group (Kubernetes fsGroup) or the same user. The token stays the real gate (ARCHITECTURE §10).
const socketMode = 0o660

// privatePattern names the directory beside the path the socket is bound in: os.MkdirTemp adds up to 10 digits, so
// the bound path is at most 13 bytes longer than the path's directory and a slash ("/.<digits>/s"). Only the dot
// stays of a longer name, to keep it out of a listing; os.MkdirTemp makes the name unique.
const privatePattern = "."

// maxSocketPath is the longest path a Unix socket binds at: sun_path less its final NUL, 103 bytes on macOS and 107 on
// Linux (Go 1.27.3 syscall_bsd.go:191, syscall_linux.go:550-560).
const maxSocketPath = len(syscall.RawSockaddrUnix{}.Path) - 1

// bindAt binds the socket in a new 0700 directory beside path and gives it its mode there, where nobody can reach it,
// then links it into place: link(2) fails when anything is at path, so a socket another sidecar put there since
// checkPath, or a file the operator did, is never replaced. A dead socket checkPath found is moved aside first, and
// only while it is still that one. It returns the socket's own file, recorded before it is reachable at path, which
// ownedSocket removes on exit only while path still names it.
func bindAt(ctx context.Context, path string, dead os.FileInfo) (*net.UnixListener, os.FileInfo, error) {
	private, err := os.MkdirTemp(filepath.Dir(path), privatePattern)
	if err != nil {
		return nil, nil, fmt.Errorf("make a private directory beside %s: %w", path, err)
	}

	var keep bool

	defer func() {
		if !keep {
			_ = os.RemoveAll(private)
		}
	}()

	bound := filepath.Join(private, "s")
	if len(path) > maxSocketPath || len(bound) > maxSocketPath {
		return nil, nil, fmt.Errorf("the socket path %s (%d bytes) is too long: the socket is bound first at %s (%d bytes), "+
			"and a Unix socket's path takes at most %d bytes here; choose a shorter one", path, len(path), bound, len(bound), maxSocketPath)
	}

	lis, err := (&net.ListenConfig{}).Listen(ctx, "unix", bound)
	if err != nil {
		return nil, nil, fmt.Errorf("listen on %s: %w", path, err)
	}

	unix := lis.(*net.UnixListener) //nolint:forcetypeassert // a unix listen returns one
	// ownedSocket removes it, by its path, only while that is still this socket.
	unix.SetUnlinkOnClose(false)

	info, keep, err := place(bound, path, private, dead)
	if err != nil {
		_ = unix.Close()

		return nil, nil, err
	}

	return unix, info, nil
}

// place gives the bound socket its mode and links it to path, after moving the dead socket there aside. keep says the
// private directory holds what was at path, which was not the dead socket and could not be put back, so it must stay.
func place(bound, path, private string, dead os.FileInfo) (info os.FileInfo, keep bool, err error) {
	if err := os.Chmod(bound, socketMode); err != nil {
		return nil, false, fmt.Errorf("chmod %s: %w", path, err)
	}

	info, err = os.Lstat(bound)
	if err != nil {
		return nil, false, fmt.Errorf("check %s: %w", bound, err)
	}

	if dead != nil {
		if keep, err := moveAside(path, filepath.Join(private, "dead"), dead); err != nil {
			return nil, keep, err
		}
	}

	err = os.Link(bound, path)
	if errors.Is(err, fs.ErrExist) {
		return nil, false, fmt.Errorf("%s was taken while this sidecar started, by another sidecar or the operator: %w", path, err)
	}

	if err != nil {
		return nil, false, fmt.Errorf("move the socket to %s: %w", path, err)
	}

	return info, false, nil
}

// moveAside renames the dead socket at path to aside. When what it moved is not that socket, another sidecar or the
// operator replaced it meanwhile: it goes back, and the start is refused; keep says it could not, so it is at aside.
// A path already empty is fine: the link that follows tells who has it.
func moveAside(path, aside string, dead os.FileInfo) (keep bool, err error) {
	err = os.Rename(path, aside)
	if errors.Is(err, fs.ErrNotExist) {
		return false, nil
	}

	if err != nil {
		return false, fmt.Errorf("move the dead socket at %s aside: %w", path, err)
	}

	if moved, err := os.Lstat(aside); err == nil && os.SameFile(moved, dead) {
		return false, nil
	}

	if err := os.Rename(aside, path); err != nil {
		return true, fmt.Errorf("%s changed while this sidecar started, and what was there is now at %s: %w", path, aside, err)
	}

	return false, fmt.Errorf("%s changed while this sidecar started: another sidecar or the operator replaced the dead socket", path)
}

// refused reports a dial to a socket nobody listens on: a killed sidecar's.
func refused(err error) bool {
	return errors.Is(err, syscall.ECONNREFUSED)
}
