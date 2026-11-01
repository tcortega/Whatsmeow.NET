//go:build !windows

package transport

import (
	"fmt"
	"os"
)

func ownerOnly(dir string) error {
	if err := os.Chmod(dir, 0o700); err != nil { //nolint:gosec // G302: a directory needs its execute bit; 0700 is owner-only
		return fmt.Errorf("chmod: %w", err)
	}

	return nil
}
