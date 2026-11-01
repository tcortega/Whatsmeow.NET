//go:build !windows

package main

import (
	"bufio"
	"context"
	"errors"
	"io"
	"os"
	"os/exec"
	"path/filepath"
	"strings"
	"sync"
	"syscall"
	"testing"
	"time"
)

const testToken = "00112233445566778899aabbccddeeff00112233445566778899aabbccddeeff"

// child is this test binary re-executed as whatsmeow-net, returned once it has logged "starting". Its whole
// stderr is kept, and every failure prints it.
type child struct {
	cmd     *exec.Cmd
	stdin   io.WriteCloser
	stdout  *bufio.Reader
	mu      sync.Mutex
	stderr  strings.Builder
	drained chan struct{}
}

// fatalf fails the test with the child's stderr so far.
func (s *child) fatalf(t *testing.T, format string, args ...any) {
	t.Helper()

	s.mu.Lock()
	stderr := s.stderr.String()
	s.mu.Unlock()

	t.Fatalf(format+"\nsidecar stderr:\n%s", append(args, stderr)...)
}

func startSidecar(t *testing.T, args ...string) *child {
	t.Helper()

	ctx, cancel := context.WithTimeout(context.Background(), 30*time.Second)
	t.Cleanup(cancel)

	dir, err := os.MkdirTemp("", "wmn")
	if err != nil {
		t.Fatalf("mkdir: %v", err)
	}

	t.Cleanup(func() { _ = os.RemoveAll(dir) })

	cmd := exec.CommandContext(ctx, os.Args[0], append([]string{"--socket-dir", dir, "--nonce", "n", "--database", filepath.Join(dir, "whatsmeow-net.db")}, args...)...) //nolint:gosec // G204, G702: this test binary itself, with the tests' constant arguments
	cmd.Env = append(os.Environ(), "WHATSMEOW_NET_TEST_RUN_MAIN=1")

	stdin, err := cmd.StdinPipe()
	if err != nil {
		t.Fatalf("stdin: %v", err)
	}

	stdout, err := cmd.StdoutPipe()
	if err != nil {
		t.Fatalf("stdout: %v", err)
	}

	stderr, err := cmd.StderrPipe()
	if err != nil {
		t.Fatalf("stderr: %v", err)
	}

	if err := cmd.Start(); err != nil {
		t.Fatalf("start: %v", err)
	}

	t.Cleanup(func() { _ = cmd.Process.Kill(); _ = cmd.Wait() })

	s := &child{cmd: cmd, stdin: stdin, stdout: bufio.NewReader(stdout), drained: make(chan struct{})}
	started := make(chan struct{})

	// "starting" is logged once the signal handling is in place, before the token is read.
	go func() {
		defer close(s.drained)

		lines := bufio.NewScanner(stderr)
		for lines.Scan() {
			s.mu.Lock()
			s.stderr.WriteString(lines.Text() + "\n")
			s.mu.Unlock()

			if strings.Contains(lines.Text(), `"message":"starting"`) {
				close(started)
			}
		}
	}()

	select {
	case <-started:
	case <-s.drained:
		s.fatalf(t, "the sidecar exited before it logged starting")
	}

	return s
}

func (s *child) exitCode(t *testing.T) int {
	t.Helper()

	<-s.drained // all of stderr is read before Wait closes the pipe

	err := s.cmd.Wait()

	var exit *exec.ExitError
	if errors.As(err, &exit) {
		return exit.ExitCode()
	}

	if err != nil {
		s.fatalf(t, "wait: %v", err)
	}

	return 0
}

func TestSigtermBeforeTheTokenStopsTheSidecar(t *testing.T) {
	t.Parallel()

	s := startSidecar(t)

	if err := s.cmd.Process.Signal(syscall.SIGTERM); err != nil {
		s.fatalf(t, "signal: %v", err)
	}

	if code := s.exitCode(t); code != exitStopped {
		s.fatalf(t, "exit code = %d, want %d", code, exitStopped)
	}
}

func TestSigintIsIgnored(t *testing.T) {
	t.Parallel()

	s := startSidecar(t)

	// A terminal Ctrl+C reaches the whole process group; the tethered sidecar must survive it.
	if err := s.cmd.Process.Signal(syscall.SIGINT); err != nil {
		s.fatalf(t, "signal: %v", err)
	}

	if _, err := io.WriteString(s.stdin, testToken+"\n"); err != nil {
		s.fatalf(t, "write the token: %v", err)
	}

	ready, err := s.stdout.ReadString('\n')
	if err != nil || !strings.Contains(ready, `"event":"ready"`) {
		s.fatalf(t, "ready line = %q, %v; SIGINT stopped the sidecar", ready, err)
	}

	_ = s.stdin.Close()

	if code := s.exitCode(t); code != exitStopped {
		s.fatalf(t, "exit code = %d, want %d", code, exitStopped)
	}
}
