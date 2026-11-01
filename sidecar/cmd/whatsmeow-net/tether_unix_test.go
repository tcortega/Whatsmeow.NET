//go:build linux || darwin

package main

import (
	"errors"
	"fmt"
	"io"
	"os"
	"os/exec"
	"strconv"
	"strings"
	"syscall"
	"testing"
	"time"
)

// ready writes the token and reads the ready line.
func (s *child) ready(t *testing.T) {
	t.Helper()

	if _, err := io.WriteString(s.stdin, testToken+"\n"); err != nil {
		s.fatalf(t, "write the token: %v", err)
	}

	ready, err := s.stdout.ReadString('\n')
	if err != nil || !strings.Contains(ready, `"event":"ready"`) {
		s.fatalf(t, "ready line = %q, %v", ready, err)
	}
}

// The parent dies hard while another process (this test) keeps the sidecar's stdin open, so EOF never comes: the
// backup to the stdin tether stops the sidecar (ARCHITECTURE §13 invariant 9).
func TestTheSidecarStopsWhenItsParentDiesWithStdinOpen(t *testing.T) {
	t.Parallel()

	s := startSidecarWith(t, func(cmd *exec.Cmd) error {
		cmd.Env = append(cmd.Env, runParentEnv+"=1")

		return cmd.Start()
	})

	line, err := s.stdout.ReadString('\n')
	if err != nil {
		s.fatalf(t, "read the sidecar's process id: %v", err)
	}

	sidecar, err := strconv.Atoi(strings.TrimSpace(line))
	if err != nil {
		s.fatalf(t, "the parent printed %q, not a process id", line)
	}

	t.Cleanup(func() { _ = syscall.Kill(sidecar, syscall.SIGKILL) })

	s.ready(t)

	parent := s.cmd.Process.Pid
	if err := s.cmd.Process.Kill(); err != nil {
		s.fatalf(t, "kill the parent: %v", err)
	}

	// stderr ends only when the sidecar, its last writer once the parent is dead, has exited.
	select {
	case <-s.drained:
	case <-time.After(30 * time.Second):
		s.fatalf(t, "the sidecar outlived its parent by 30 s")
	}

	s.mu.Lock()
	stderr := s.stderr.String()
	s.mu.Unlock()

	for _, want := range []string{fmt.Sprintf(`"parent_pid":%d`, parent), `"message":"the parent exited"`, `"reason":"the parent exited"`, `"message":"stopped"`} {
		if !strings.Contains(stderr, want) {
			s.fatalf(t, "stderr has no %s", want)
		}
	}
}

// The parent can die before the backup is armed: a sidecar whose parent is not the --parent-pid it was given stops
// at once, before the token.
func TestTheSidecarStopsWhenItStartsWithAnotherParent(t *testing.T) {
	t.Parallel()

	gone := os.Getppid() // the go test driver: alive, and not the sidecar's parent
	s := startSidecar(t, "--parent-pid", strconv.Itoa(gone))

	if code := s.exitCode(t); code != exitStopped {
		s.fatalf(t, "exit code = %d, want %d", code, exitStopped)
	}

	s.mu.Lock()
	stderr := s.stderr.String()
	s.mu.Unlock()

	if want := fmt.Sprintf(`"parent_pid":%d`, gone); !strings.Contains(stderr, want) || !strings.Contains(stderr, `"message":"the parent exited"`) {
		s.fatalf(t, "stderr has no %q and %q", want, "the parent exited")
	}
}

// A dead parent takes the read end of stderr with it; the graceful stop still finishes instead of dying of SIGPIPE
// on its first log line.
func TestABrokenStderrStillStopsGracefully(t *testing.T) {
	t.Parallel()

	s := startSidecar(t)
	s.ready(t)

	if err := s.stderrPipe.Close(); err != nil {
		s.fatalf(t, "close stderr: %v", err)
	}

	<-s.drained
	_ = s.stdin.Close()

	err := s.cmd.Wait()

	var exit *exec.ExitError
	if errors.As(err, &exit) {
		t.Fatalf("the sidecar ended with %v, want exit code %d", exit.ProcessState, exitStopped)
	}

	if err != nil {
		t.Fatalf("wait: %v", err)
	}
}
