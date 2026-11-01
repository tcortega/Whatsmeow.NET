package main

import (
	"os"
	"os/exec"
	"runtime"
	"strings"
	"syscall"
	"testing"
	"time"
)

// PR_SET_PDEATHSIG fires when the thread that forked the sidecar exits, not only when its parent process does
// (prctl(2) CAVEATS). .NET forks from a thread-pool thread, which retires after 20 s idle; the sidecar must outlive
// that thread.
func TestAThreadOfTheParentExitingDoesNotStopTheSidecar(t *testing.T) {
	t.Parallel()

	exit := make(chan struct{})
	s := startSidecarWith(t, func(cmd *exec.Cmd) error {
		started := make(chan error, 1)

		var fork func()
		fork = func() {
			runtime.LockOSThread()

			// The runtime never ends the main thread, it wedges it (runtime/proc.go mexit). So this goroutine keeps
			// the main thread, and another one, which cannot land on it now, forks.
			if syscall.Gettid() == os.Getpid() {
				defer runtime.UnlockOSThread()

				go fork()
				<-exit

				return
			}

			// Never unlocked: the runtime ends this thread when the goroutine returns.
			started <- cmd.Start()

			<-exit
		}

		go fork()

		return <-started
	})

	close(exit)
	s.waitForLog(t, "a parent-death signal came; the parent lives")
	s.ready(t)

	_ = s.stdin.Close()

	if code := s.exitCode(t); code != exitStopped {
		s.fatalf(t, "exit code = %d, want %d", code, exitStopped)
	}
}

// waitForLog waits until the sidecar's stderr holds want, and fails when it exits or 30 s pass first.
func (s *child) waitForLog(t *testing.T, want string) {
	t.Helper()

	deadline := time.After(30 * time.Second)

	for {
		s.mu.Lock()
		found := strings.Contains(s.stderr.String(), want)
		s.mu.Unlock()

		if found {
			return
		}

		select {
		case <-s.logged:
		case <-s.drained:
			s.mu.Lock()
			found = strings.Contains(s.stderr.String(), want)
			s.mu.Unlock()

			if !found {
				s.fatalf(t, "the sidecar exited without logging %s", want)
			}

			return
		case <-deadline:
			s.fatalf(t, "the sidecar logged no %s within 30 s", want)
		}
	}
}
