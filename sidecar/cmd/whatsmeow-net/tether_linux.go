package main

import (
	"context"
	"fmt"
	"os"
	"os/signal"
	"runtime"
	"syscall"

	"github.com/rs/zerolog"
	"golang.org/x/sys/unix"
)

// parentDeathSignal is the signal PR_SET_PDEATHSIG sends. Not SIGTERM: it also comes when only the thread that forked
// the sidecar exits, and SIGTERM always stops it.
const parentDeathSignal = syscall.SIGUSR1

// tetherToParent is the Linux backup to the stdin tether (ARCHITECTURE §7.8): the returned context is cancelled with
// errParentExited once parent is no longer this process's parent. untether stops the watch and waits for it.
//
// PR_SET_PDEATHSIG signals when the thread that forked this process exits, the parent process's last thread included
// (prctl(2) CAVEATS; linux kernel/exit.c forget_original_parent), and .NET forks from a thread-pool thread that
// retires after 20 s idle. So every signal only wakes a check of getppid, which names the parent process, not the
// thread (kernel/sys.c getppid), and which the kernel has already updated when it sends the signal. The setting
// belongs to the calling thread, and a thread that exits takes it along (kernel/fork.c copy_process clears it for
// every new task), so the goroutine keeps its thread for its whole life. A parent gone before the prctl never
// signals, so the first check runs right after it.
func tetherToParent(ctx context.Context, log zerolog.Logger, parent int) (context.Context, func(), error) {
	ctx, orphan := context.WithCancelCause(ctx)
	deaths := make(chan os.Signal, 1)
	signal.Notify(deaths, parentDeathSignal)

	armed := make(chan error, 1)
	done := make(chan struct{})

	go func() {
		defer close(done)
		// A SIGUSR1 after Stop does nothing: without Notify the runtime drops it (runtime/sigtab_linux_generic.go,
		// signal_unix.go sighandler).
		defer signal.Stop(deaths)

		// Never unlocked: the runtime ends the thread with the goroutine, and the setting with it.
		runtime.LockOSThread()

		if err := unix.Prctl(unix.PR_SET_PDEATHSIG, uintptr(parentDeathSignal), 0, 0, 0); err != nil {
			armed <- fmt.Errorf("set the parent-death signal: %w", err)

			return
		}

		armed <- nil

		for os.Getppid() == parent {
			select {
			case <-ctx.Done():
				return
			case <-deaths:
			}

			if os.Getppid() == parent {
				log.Info().Int("parent_pid", parent).Msg("a parent-death signal came; the parent lives")
			}
		}

		log.Info().Int("parent_pid", parent).Int("ppid", os.Getppid()).Msg("the parent exited")
		orphan(errParentExited)
	}()

	if err := <-armed; err != nil {
		orphan(err)
		<-done

		return nil, nil, err
	}

	return ctx, func() { orphan(nil); <-done }, nil
}
