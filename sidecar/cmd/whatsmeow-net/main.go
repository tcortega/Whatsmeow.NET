// Command whatsmeow-net is the sidecar process (ARCHITECTURE §7.8).
//
// The client starts it with --transport, --socket-dir and --nonce, writes the per-launch token as the first
// line of stdin, and keeps stdin open: EOF on stdin, a Shutdown call or SIGTERM stop it gracefully with exit
// code 0. SIGINT is ignored: the sidecar is always tethered, and a terminal Ctrl+C reaches the whole process
// group, so the client decides when it stops. Once it listens, it prints exactly one JSON ready line on stdout. Logs are JSON lines on
// stderr.
//
// A test build (-tags fakewhatsapp) started with --fake-whatsapp replaces WhatsApp with a scripted fake and serves its
// control service; a release build refuses the flag (ADR 0019).
package main

import (
	"bufio"
	"context"
	"encoding/hex"
	"encoding/json"
	"errors"
	"flag"
	"fmt"
	"io"
	"os"
	"os/signal"
	"runtime/debug"
	"strings"
	"syscall"

	"github.com/rs/zerolog"

	whatsmeowv1 "github.com/tcortega/Whatsmeow.NET/sidecar/gen/whatsmeow/v1"
	"github.com/tcortega/Whatsmeow.NET/sidecar/internal/server"
	"github.com/tcortega/Whatsmeow.NET/sidecar/internal/sidecar"
	"github.com/tcortega/Whatsmeow.NET/sidecar/internal/transport"
)

// version is the release version, equal to the client version (ARCHITECTURE §13 invariant 12).
// It is set at link time with -ldflags "-X main.version=<version>".
var version string

const (
	exitStopped = 0
	exitFailed  = 1
	exitUsage   = 2
	tokenBytes  = 32
)

// readyLine is the one line on stdout.
type readyLine struct {
	Event     string `json:"event"`
	Version   string `json:"version"`
	Protocol  int    `json:"protocol"`
	Whatsmeow string `json:"whatsmeow"`
	Listen    string `json:"listen"`
	Nonce     string `json:"nonce"`
}

func main() {
	os.Exit(run(os.Args[1:], os.Stdin, os.Stdout, os.Stderr))
}

func run(args []string, stdin io.Reader, stdout, stderr io.Writer) int {
	log := zerolog.New(stderr).With().Timestamp().Logger()

	flags := flag.NewFlagSet("whatsmeow-net", flag.ContinueOnError)
	flags.SetOutput(stderr)
	kind := flags.String("transport", transport.Unix, "unix or pipe (Windows only)")
	socketDir := flags.String("socket-dir", "", "the directory for the unix socket; the client owns it")
	nonce := flags.String("nonce", "", "echoed in the ready line")
	fake := flags.Bool("fake-whatsapp", false, "test builds only (-tags fakewhatsapp): replace WhatsApp with a scripted fake and serve its control")

	if err := flags.Parse(args); err != nil {
		return exitUsage
	}

	if *nonce == "" || flags.NArg() > 0 {
		log.Error().Msg("usage: whatsmeow-net --nonce <nonce> [--transport unix|pipe] [--socket-dir <dir>]")

		return exitUsage
	}

	// The fake never ships: a release build has none, and says so before it reads the token (ADR 0019).
	if *fake && !fakeBuild {
		log.Error().Msg("--fake-whatsapp needs a test build (-tags fakewhatsapp); this build never fakes WhatsApp")

		return exitUsage
	}

	signal.Ignore(os.Interrupt)

	ctx, stop := signal.NotifyContext(context.Background(), syscall.SIGTERM)
	defer stop()

	log.Info().Str("version", version).Str("transport", *kind).Msg("starting")

	if err := serve(ctx, log, *kind, *socketDir, *nonce, *fake, bufio.NewReader(stdin), stdout); err != nil {
		log.Error().Err(err).Msg("sidecar failed")

		return exitFailed
	}

	return exitStopped
}

func serve(ctx context.Context, log zerolog.Logger, kind, socketDir, nonce string, fake bool, stdin *bufio.Reader, stdout io.Writer) error {
	token, err := awaitToken(ctx, stdin)
	if err != nil {
		return err
	}

	if token == "" {
		log.Info().Msg("stopped before the token arrived")

		return nil
	}

	build, ok := debug.ReadBuildInfo()
	if !ok {
		return errors.New("the binary has no build info")
	}

	lis, endpoint, err := transport.Listen(ctx, kind, socketDir)
	if err != nil {
		return fmt.Errorf("listen: %w", err)
	}

	srv := server.New(token, log)
	svc := sidecar.New(version, build, srv.Shutdown)
	whatsmeowv1.RegisterSidecarServiceServer(srv, svc)

	if fake {
		serveFake(srv, log)
	}

	served := make(chan error, 1)

	go func() { served <- srv.Serve(lis) }()

	// The tether: stdin stays open for the sidecar's whole life, and EOF (the parent closed it or died) stops it.
	// The read cannot be cancelled; the goroutine ends with the process.
	tether := make(chan struct{})

	go func() {
		_, _ = io.Copy(io.Discard, stdin)
		close(tether)
	}()

	err = json.NewEncoder(stdout).Encode(readyLine{
		Event: "ready", Version: version, Protocol: sidecar.Protocol, Whatsmeow: svc.WhatsmeowVersion(), Listen: endpoint, Nonce: nonce,
	})
	if err != nil {
		srv.Stop()

		return errors.Join(fmt.Errorf("write the ready line: %w", err), <-served)
	}

	var reason string

	select {
	case <-ctx.Done():
		reason = "signal"
	case <-tether:
		reason = "stdin closed"
	case <-srv.ShuttingDown():
		reason = "Shutdown called"
	case err := <-served:
		return err
	}

	log.Info().Str("reason", reason).Msg("stopping")
	srv.Shutdown()
	srv.Stop()

	if err := <-served; err != nil {
		return err
	}

	log.Info().Msg("stopped")

	return nil
}

// awaitToken reads the token, or returns "" when ctx ends first: SIGTERM stops the sidecar before the token
// arrives too. The read itself cannot be interrupted; its goroutine ends with the process.
func awaitToken(ctx context.Context, stdin *bufio.Reader) (string, error) {
	type result struct {
		token string
		err   error
	}

	read := make(chan result, 1)

	go func() {
		token, err := readToken(stdin)
		read <- result{token, err}
	}()

	select {
	case <-ctx.Done():
		return "", nil
	case r := <-read:
		return r.token, r.err
	}
}

// readToken reads the first stdin line: the per-launch token, 32 bytes as hex.
func readToken(stdin *bufio.Reader) (string, error) {
	line, err := stdin.ReadString('\n')
	if err != nil {
		return "", fmt.Errorf("read the token from stdin: %w", err)
	}

	token := strings.TrimRight(line, "\r\n")

	raw, err := hex.DecodeString(token)
	if err != nil || len(raw) != tokenBytes {
		return "", fmt.Errorf("the token must be %d bytes as hex", tokenBytes)
	}

	return token, nil
}
