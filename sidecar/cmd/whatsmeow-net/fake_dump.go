//go:build fakewhatsapp

package main

import (
	"context"
	"fmt"
	"os"
	"path/filepath"
	"runtime/pprof"
	"time"

	"github.com/rs/zerolog"
)

// dumpDirVariable names a directory in which a test build writes every goroutine's stack on request: a file
// dump-<pid> there asks, and goroutines-<pid>-<n>.txt answers. Windows has no SIGQUIT, so a hung test build is read
// this way (#176). Never in a release build (ADR 0019).
const dumpDirVariable = "WHATSMEOW_NET_TEST_DUMP_DIR"

// dumpOnRequest answers each request in dir until ctx ends, checking every interval.
func dumpOnRequest(ctx context.Context, dir string, interval time.Duration, log zerolog.Logger) {
	request := filepath.Join(dir, fmt.Sprintf("dump-%d", os.Getpid()))
	ticker := time.NewTicker(interval)
	defer ticker.Stop()

	for n := 1; ; {
		select {
		case <-ctx.Done():
			return
		case <-ticker.C:
		}

		if _, err := os.Stat(request); err != nil { //nolint:gosec // G703: the test's own directory
			continue
		}

		if err := dump(filepath.Join(dir, fmt.Sprintf("goroutines-%d-%d.txt", os.Getpid(), n))); err != nil {
			log.Warn().Err(err).Msg("the goroutine dump failed")
		}

		_ = os.Remove(request) //nolint:gosec // G703: the test's own directory
		n++
	}
}

func dump(path string) error {
	f, err := os.Create(path) //nolint:gosec // G304: the test's own directory
	if err != nil {
		return fmt.Errorf("goroutine dump: %w", err)
	}

	if err := pprof.Lookup("goroutine").WriteTo(f, 2); err != nil {
		_ = f.Close()

		return fmt.Errorf("goroutine dump: %w", err)
	}

	if err := f.Close(); err != nil {
		return fmt.Errorf("goroutine dump: %w", err)
	}

	return nil
}
