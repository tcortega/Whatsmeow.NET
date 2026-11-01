package main

import (
	"context"
	"os"
	"time"

	"github.com/rs/zerolog"
)

// parentPoll is how often the sidecar checks its parent on macOS.
const parentPoll = time.Second

// tetherToParent is the macOS backup to the stdin tether (ARCHITECTURE §7.8): the returned context is cancelled with
// errParentExited once parent is no longer this process's parent. A dead parent's children go to launchd (xnu
// kern_exit.c), so getppid changes; it is checked at once and then every parentPoll. untether stops the watch and
// waits for it.
func tetherToParent(ctx context.Context, log zerolog.Logger, parent int) (context.Context, func(), error) {
	ctx, orphan := context.WithCancelCause(ctx)
	done := make(chan struct{})

	go func() {
		defer close(done)

		tick := time.NewTicker(parentPoll)
		defer tick.Stop()

		for os.Getppid() == parent {
			select {
			case <-ctx.Done():
				return
			case <-tick.C:
			}
		}

		log.Info().Int("parent_pid", parent).Int("ppid", os.Getppid()).Msg("the parent exited")
		orphan(errParentExited)
	}()

	return ctx, func() { orphan(nil); <-done }, nil
}
