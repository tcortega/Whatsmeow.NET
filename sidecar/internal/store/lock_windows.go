//go:build windows

package store

import (
	"errors"
	"fmt"
	"os"

	"golang.org/x/sys/windows"
)

// lockFile opens path and takes an exclusive lock on its first byte without waiting. Windows releases the lock
// when the handle is closed, also when the process dies, so a crashed sidecar never blocks its restart.
func lockFile(path string) (*os.File, error) {
	file, err := os.OpenFile(path, os.O_RDWR|os.O_CREATE, 0o600) //nolint:gosec // G304: the lock file next to the configured database
	if err != nil {
		return nil, fmt.Errorf("open: %w", err)
	}

	err = windows.LockFileEx(windows.Handle(file.Fd()), windows.LOCKFILE_EXCLUSIVE_LOCK|windows.LOCKFILE_FAIL_IMMEDIATELY, 0, 1, 0, &windows.Overlapped{})
	if err != nil {
		_ = file.Close()

		if errors.Is(err, windows.ERROR_LOCK_VIOLATION) {
			return nil, ErrInUse
		}

		return nil, fmt.Errorf("LockFileEx: %w", err)
	}

	return file, nil
}
