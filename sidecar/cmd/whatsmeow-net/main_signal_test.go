//go:build !windows

package main

import (
	"bufio"
	"context"
	"errors"
	"io"
	"os"
	"os/exec"
	"strings"
	"syscall"
	"testing"
	"time"
)

const testToken = "00112233445566778899aabbccddeeff00112233445566778899aabbccddeeff"

// child is this test binary re-executed as whatsmeow-net, stopped once it has logged "starting".
type child struct {
	cmd    *exec.Cmd
	stdin  io.WriteCloser
	stdout *bufio.Reader
}

func startSidecar(t *testing.T) *child {
	t.Helper()

	ctx, cancel := context.WithTimeout(context.Background(), 30*time.Second)
	t.Cleanup(cancel)

	dir, err := os.MkdirTemp("", "wmn")
	if err != nil {
		t.Fatalf("mkdir: %v", err)
	}

	t.Cleanup(func() { _ = os.RemoveAll(dir) })

	cmd := exec.CommandContext(ctx, os.Args[0], "--socket-dir", dir, "--nonce", "n") //nolint:gosec // G204, G702: this test binary itself, with constant arguments
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

	// "starting" is logged once the signal handling is in place, before the token is read.
	lines := bufio.NewScanner(stderr)
	for lines.Scan() {
		if strings.Contains(lines.Text(), `"message":"starting"`) {
			break
		}
	}

	go func() { _, _ = io.Copy(io.Discard, stderr) }()

	return &child{cmd: cmd, stdin: stdin, stdout: bufio.NewReader(stdout)}
}

func (s *child) exitCode(t *testing.T) int {
	t.Helper()

	err := s.cmd.Wait()

	var exit *exec.ExitError
	if errors.As(err, &exit) {
		return exit.ExitCode()
	}

	if err != nil {
		t.Fatalf("wait: %v", err)
	}

	return 0
}

func TestSigtermBeforeTheTokenStopsTheSidecar(t *testing.T) {
	t.Parallel()

	s := startSidecar(t)

	if err := s.cmd.Process.Signal(syscall.SIGTERM); err != nil {
		t.Fatalf("signal: %v", err)
	}

	if code := s.exitCode(t); code != exitStopped {
		t.Fatalf("exit code = %d, want %d", code, exitStopped)
	}
}

func TestSigintIsIgnored(t *testing.T) {
	t.Parallel()

	s := startSidecar(t)

	// A terminal Ctrl+C reaches the whole process group; the tethered sidecar must survive it.
	if err := s.cmd.Process.Signal(syscall.SIGINT); err != nil {
		t.Fatalf("signal: %v", err)
	}

	if _, err := io.WriteString(s.stdin, testToken+"\n"); err != nil {
		t.Fatalf("write the token: %v", err)
	}

	ready, err := s.stdout.ReadString('\n')
	if err != nil || !strings.Contains(ready, `"event":"ready"`) {
		t.Fatalf("ready line = %q, %v; SIGINT stopped the sidecar", ready, err)
	}

	_ = s.stdin.Close()

	if code := s.exitCode(t); code != exitStopped {
		t.Fatalf("exit code = %d, want %d", code, exitStopped)
	}
}
