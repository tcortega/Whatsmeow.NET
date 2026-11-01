//go:build !windows

package store

import (
	"errors"
	"fmt"
	"os"

	"golang.org/x/sys/unix"
)

// lockFile opens path and takes an exclusive lock on it without waiting. The kernel releases the lock when the
// file is closed, also when the process dies, so a crashed sidecar never blocks its restart.
func lockFile(path string) (*os.File, error) {
	file, err := os.OpenFile(path, os.O_RDWR|os.O_CREATE, 0o600) //nolint:gosec // G304: the lock file next to the configured database
	if err != nil {
		return nil, fmt.Errorf("open: %w", err)
	}

	if err := unix.Flock(int(file.Fd()), unix.LOCK_EX|unix.LOCK_NB); err != nil {
		_ = file.Close()

		if errors.Is(err, unix.EWOULDBLOCK) {
			return nil, ErrInUse
		}

		return nil, fmt.Errorf("flock: %w", err)
	}

	return file, nil
}
