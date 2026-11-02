//go:build linux || darwin

package main

import (
	"bufio"
	"encoding/json"
	"os"
	"strconv"
	"strings"
	"syscall"
	"testing"
	"time"
)

// Standalone, the orchestrator stops the sidecar: docker stop sends SIGTERM, and Ctrl+C in a terminal SIGINT.
func TestStandaloneStopsGracefullyOnSIGTERMAndSIGINT(t *testing.T) {
	t.Parallel()

	for _, signal := range []syscall.Signal{syscall.SIGTERM, syscall.SIGINT} {
		s := startStandalone(t, "tcp:127.0.0.1:0")

		if err := s.cmd.Process.Signal(signal); err != nil {
			s.fatalf(t, "signal: %v", err)
		}

		<-s.done

		if err := s.cmd.Wait(); err != nil {
			s.fatalf(t, "%v: wait = %v, want exit code 0", signal, err)
		}

		if !strings.Contains(s.stderr.String(), `"message":"stopped"`) {
			s.fatalf(t, "%v: no graceful stop", signal)
		}
	}
}

// Standalone, nothing tethers the sidecar to the process that started it, a shell or an init that exits.
func TestStandaloneOutlivesTheProcessThatStartedIt(t *testing.T) {
	t.Parallel()

	cmd := command(t, "--listen", "tcp:127.0.0.1:0", "--token-file", tokenFile(t, standaloneToken))
	cmd.Env = append(cmd.Env, runParentEnv+"=1")

	stdout, err := cmd.StdoutPipe()
	if err != nil {
		t.Fatalf("stdout: %v", err)
	}

	if err := cmd.Start(); err != nil {
		t.Fatalf("start: %v", err)
	}

	t.Cleanup(func() { _ = cmd.Process.Kill(); _ = cmd.Wait() })

	lines := bufio.NewReader(stdout)

	line, err := lines.ReadString('\n')
	if err != nil {
		t.Fatalf("read the sidecar's process id: %v", err)
	}

	sidecar, err := strconv.Atoi(strings.TrimSpace(line))
	if err != nil {
		t.Fatalf("the parent printed %q, not a process id", line)
	}

	t.Cleanup(func() { _ = syscall.Kill(sidecar, syscall.SIGKILL) })

	s := &standalone{cmd: cmd, done: make(chan struct{})}

	line, err = lines.ReadString('\n')
	if err != nil || json.Unmarshal([]byte(line), &s.ready) != nil {
		t.Fatalf("ready line %q: %v", line, err)
	}

	if err := cmd.Process.Kill(); err != nil {
		t.Fatalf("kill the parent: %v", err)
	}

	_ = cmd.Wait()

	// Longer than the macOS parent poll (tether_darwin.go) and the Linux parent-death signal.
	time.Sleep(3 * parentCheck)

	if err := s.getInfo(t, standaloneToken); err != nil {
		t.Fatalf("GetInfo after the parent exited: %v", err)
	}

	if process, err := os.FindProcess(sidecar); err != nil || process.Signal(syscall.Signal(0)) != nil {
		t.Fatalf("the sidecar is gone")
	}
}

// parentCheck is how long the tether takes to see its parent gone, at most.
const parentCheck = time.Second
