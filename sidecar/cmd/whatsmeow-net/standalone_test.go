package main

import (
	"bufio"
	"bytes"
	"context"
	"encoding/json"
	"errors"
	"io"
	"net"
	"os"
	"os/exec"
	"path/filepath"
	"strings"
	"sync"
	"testing"
	"time"

	"google.golang.org/grpc"
	"google.golang.org/grpc/codes"
	"google.golang.org/grpc/credentials/insecure"
	"google.golang.org/grpc/metadata"
	"google.golang.org/grpc/status"

	whatsmeowv1 "github.com/tcortega/Whatsmeow.NET/sidecar/gen/whatsmeow/v1"
)

// standaloneToken is the token the standalone tests write to the token file.
const standaloneToken = "ffeeddccbbaa99887766554433221100ffeeddccbbaa99887766554433221100" //nolint:gosec // G101: a test token

// standalone is this test binary re-executed as a standalone whatsmeow-net (ADR 0046), its stdin at EOF from the
// start, as a container's is.
type standalone struct {
	cmd    *exec.Cmd
	ready  readyLine
	mu     sync.Mutex
	stderr strings.Builder
	done   chan struct{}
}

func (s *standalone) fatalf(t *testing.T, format string, args ...any) {
	t.Helper()

	s.mu.Lock()
	stderr := s.stderr.String()
	s.mu.Unlock()

	t.Fatalf(format+"\nsidecar stderr:\n%s", append(args, stderr)...)
}

// shortDir is a directory with a short path: t.TempDir() names can pass the 104-byte sun_path limit on macOS.
func shortDir(t *testing.T) string {
	t.Helper()

	dir, err := os.MkdirTemp("", "wmn")
	if err != nil {
		t.Fatalf("mkdir: %v", err)
	}

	t.Cleanup(func() { _ = os.RemoveAll(dir) })

	return dir
}

// tokenFile writes content to a new token file and returns its path.
func tokenFile(t *testing.T, content string) string {
	t.Helper()

	path := filepath.Join(shortDir(t), "token")
	if err := os.WriteFile(path, []byte(content), 0o600); err != nil {
		t.Fatalf("write the token file: %v", err)
	}

	return path
}

// command is this test binary as whatsmeow-net with args, a database of its own, and stdin at EOF.
func command(t *testing.T, args ...string) *exec.Cmd {
	t.Helper()

	ctx, cancel := context.WithTimeout(context.Background(), 30*time.Second)
	t.Cleanup(cancel)

	cmd := exec.CommandContext(ctx, os.Args[0], append([]string{"--database", filepath.Join(shortDir(t), "whatsmeow-net.db")}, args...)...) //nolint:gosec // G204, G702: this test binary itself, with the tests' arguments
	cmd.Env = append(os.Environ(), "WHATSMEOW_NET_TEST_RUN_MAIN=1")

	return cmd
}

// startStandalone starts a standalone sidecar listening on listen with the standalone token, and waits for its ready line.
func startStandalone(t *testing.T, listen string, args ...string) *standalone {
	t.Helper()

	return startStandaloneCommand(t, command(t, append([]string{"--listen", listen, "--token-file", tokenFile(t, standaloneToken+"\n")}, args...)...))
}

func startStandaloneCommand(t *testing.T, cmd *exec.Cmd) *standalone {
	t.Helper()

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

	s := &standalone{cmd: cmd, done: make(chan struct{})}

	go func() {
		defer close(s.done)

		lines := bufio.NewScanner(stderr)
		for lines.Scan() {
			s.mu.Lock()
			s.stderr.WriteString(lines.Text() + "\n")
			s.mu.Unlock()
		}
	}()

	line, err := bufio.NewReader(stdout).ReadString('\n')
	if err != nil {
		s.fatalf(t, "read the ready line: %v", err)
	}

	if err := json.Unmarshal([]byte(line), &s.ready); err != nil || s.ready.Event != "ready" {
		s.fatalf(t, "ready line %q: %v", line, err)
	}

	return s
}

// getInfo calls GetInfo on the standalone sidecar with token.
func (s *standalone) getInfo(t *testing.T, token string) error {
	t.Helper()

	// A dialer of its own: a unix target URL cannot carry a Windows path.
	network := "tcp"
	if filepath.IsAbs(s.ready.Listen) {
		network = "unix"
	}

	conn, err := grpc.NewClient("passthrough:///sidecar",
		grpc.WithContextDialer(func(ctx context.Context, _ string) (net.Conn, error) {
			return (&net.Dialer{}).DialContext(ctx, network, s.ready.Listen)
		}),
		grpc.WithTransportCredentials(insecure.NewCredentials()))
	if err != nil {
		s.fatalf(t, "dial %s: %v", s.ready.Listen, err)
	}

	defer func() { _ = conn.Close() }()

	ctx, cancel := context.WithTimeout(metadata.AppendToOutgoingContext(context.Background(), "authorization", "Bearer "+token), 10*time.Second)
	defer cancel()

	_, err = whatsmeowv1.NewSidecarServiceClient(conn).GetInfo(ctx, &whatsmeowv1.GetInfoRequest{})

	return err //nolint:wrapcheck // the test reads the status
}

// serves fails unless the sidecar answers the standalone token and refuses another.
func (s *standalone) serves(t *testing.T) {
	t.Helper()

	if err := s.getInfo(t, standaloneToken); err != nil {
		s.fatalf(t, "GetInfo with the file's token: %v", err)
	}

	if err := s.getInfo(t, strings.Repeat("0", len(standaloneToken))); status.Code(err) != codes.Unauthenticated {
		s.fatalf(t, "GetInfo with another token = %v, want Unauthenticated", err)
	}
}

// Stdin is at EOF from the start, as a container's is without -i: a tethered sidecar would read no token, and one
// that read EOF as the tether would stop. A standalone one takes the token from the file and keeps serving.
func TestStandaloneServesOverTCPWithTheTokenFromTheFile(t *testing.T) {
	t.Parallel()

	s := startStandalone(t, "tcp:127.0.0.1:0")

	if s.ready.Nonce != "" || strings.HasSuffix(s.ready.Listen, ":0") {
		s.fatalf(t, "ready line = %+v, want no nonce and the bound address", s.ready)
	}

	// Time for a stdin tether to act on the EOF it read at once.
	time.Sleep(time.Second)

	s.serves(t)
}

func TestStandaloneServesOverAUnixSocketAtThePath(t *testing.T) {
	t.Parallel()

	path := filepath.Join(shortDir(t), "whatsmeow-net.sock")
	s := startStandalone(t, "unix:"+path)

	if s.ready.Listen != path {
		s.fatalf(t, "listen = %q, want %q", s.ready.Listen, path)
	}

	time.Sleep(time.Second)

	s.serves(t)
}

// The file may come from a secret mount, with or without a final newline; anything else is refused before the
// sidecar listens, and its content is never logged.
func TestTheTokenFileMustHoldOneToken(t *testing.T) {
	t.Parallel()

	for _, content := range []string{standaloneToken, standaloneToken + "\r\n"} {
		s := startStandaloneCommand(t, command(t, "--listen", "tcp:127.0.0.1:0", "--token-file", tokenFile(t, content)))
		s.serves(t)
	}

	for _, content := range []string{"", "\n", "not-a-token-0123456789", standaloneToken[:62] + "\n", standaloneToken + "\n" + standaloneToken + "\n"} {
		cmd := command(t, "--listen", "tcp:127.0.0.1:0", "--token-file", tokenFile(t, content))

		var stderr bytes.Buffer
		cmd.Stderr = &stderr

		err := cmd.Run()

		var exit *exec.ExitError
		if !errors.As(err, &exit) || exit.ExitCode() != exitFailed {
			t.Fatalf("token file %q: exit = %v, want %d\n%s", content, err, exitFailed, stderr.String())
		}

		if strings.Contains(stderr.String(), "not-a-token") || strings.Contains(stderr.String(), standaloneToken[:62]) {
			t.Fatalf("the log carries the token file's content: %s", stderr.String())
		}
	}
}

func TestAMissingTokenFileFailsTheStart(t *testing.T) {
	t.Parallel()

	cmd := command(t, "--listen", "tcp:127.0.0.1:0", "--token-file", filepath.Join(shortDir(t), "missing"))
	cmd.Stderr = io.Discard

	var exit *exec.ExitError
	if err := cmd.Run(); !errors.As(err, &exit) || exit.ExitCode() != exitFailed {
		t.Fatalf("exit = %v, want %d", err, exitFailed)
	}
}

// The standalone flags and the embedded ones are two modes: a mix, or a --listen that is not an address, is refused
// before anything starts, the database included.
func TestStandaloneRefusesTheEmbeddedFlagsAndAMalformedAddress(t *testing.T) {
	t.Parallel()

	token := tokenFile(t, standaloneToken)

	for _, args := range [][]string{
		{"--listen", "tcp:127.0.0.1:0"},
		{"--token-file", token, "--nonce", "n"},
		{"--listen", "tcp:127.0.0.1:0", "--token-file", token, "--nonce", "n"},
		{"--listen", "tcp:127.0.0.1:0", "--token-file", token, "--socket-dir", os.TempDir()},
		{"--listen", "tcp:127.0.0.1:0", "--token-file", token, "--transport", "unix"},
		{"--listen", "tcp:127.0.0.1:0", "--token-file", token, "--parent-pid", "1"},
		{"--listen", "tcp:no-port", "--token-file", token},
		{"--listen", "unix:relative/s", "--token-file", token},
	} {
		database := filepath.Join(shortDir(t), "whatsmeow-net.db")
		cmd := command(t, append([]string{"--database", database}, args...)...)

		var stderr bytes.Buffer
		cmd.Stderr = &stderr

		var exit *exec.ExitError
		if err := cmd.Run(); !errors.As(err, &exit) || exit.ExitCode() != exitUsage {
			t.Fatalf("%v: exit = %v, want %d\n%s", args, err, exitUsage, stderr.String())
		}

		if _, err := os.Stat(database); !errors.Is(err, os.ErrNotExist) {
			t.Fatalf("%v: the database was created: %v", args, err)
		}
	}
}

// An orchestrator rotates the secret while the sidecar runs: the file's new token is accepted, the old one no longer.
func TestStandaloneFollowsARotatedTokenFile(t *testing.T) {
	t.Parallel()

	path := tokenFile(t, standaloneToken+"\n")
	s := startStandaloneCommand(t, command(t, "--listen", "tcp:127.0.0.1:0", "--token-file", path))

	next := strings.Repeat("ab", len(standaloneToken)/2)
	if err := os.WriteFile(path, []byte(next+"\n"), 0o600); err != nil {
		t.Fatalf("rotate: %v", err)
	}

	if err := s.getInfo(t, next); err != nil {
		s.fatalf(t, "GetInfo with the rotated token: %v", err)
	}

	if err := s.getInfo(t, standaloneToken); status.Code(err) != codes.Unauthenticated {
		s.fatalf(t, "GetInfo with the old token = %v, want Unauthenticated", err)
	}
}
