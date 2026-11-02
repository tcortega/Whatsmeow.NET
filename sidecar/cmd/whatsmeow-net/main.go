// Command whatsmeow-net is the sidecar process (ARCHITECTURE §7.8).
//
// The client starts it with --database, --transport, --socket-dir, --nonce and --parent-pid, writes the per-launch
// token as the first line of stdin, and keeps stdin open: EOF on stdin, a Shutdown call, SIGTERM or, on Linux and
// macOS, its parent no longer being --parent-pid (the tether backup, even while another process keeps stdin open)
// stop it gracefully with exit code 0. SIGINT is ignored: the sidecar is always tethered, and a terminal Ctrl+C reaches the whole process
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
	wastore "go.mau.fi/whatsmeow/store"

	whatsmeowv1 "github.com/tcortega/Whatsmeow.NET/sidecar/gen/whatsmeow/v1"
	"github.com/tcortega/Whatsmeow.NET/sidecar/internal/chatstate"
	"github.com/tcortega/Whatsmeow.NET/sidecar/internal/contacts"
	eventsslice "github.com/tcortega/Whatsmeow.NET/sidecar/internal/events"
	"github.com/tcortega/Whatsmeow.NET/sidecar/internal/groups"
	wmlog "github.com/tcortega/Whatsmeow.NET/sidecar/internal/log"
	"github.com/tcortega/Whatsmeow.NET/sidecar/internal/media"
	"github.com/tcortega/Whatsmeow.NET/sidecar/internal/messaging"
	"github.com/tcortega/Whatsmeow.NET/sidecar/internal/outbox"
	"github.com/tcortega/Whatsmeow.NET/sidecar/internal/presence"
	"github.com/tcortega/Whatsmeow.NET/sidecar/internal/server"
	"github.com/tcortega/Whatsmeow.NET/sidecar/internal/session"
	"github.com/tcortega/Whatsmeow.NET/sidecar/internal/sidecar"
	"github.com/tcortega/Whatsmeow.NET/sidecar/internal/store"
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
	log := newLogger(stderr)

	flags := flag.NewFlagSet("whatsmeow-net", flag.ContinueOnError)
	flags.SetOutput(stderr)
	kind := flags.String("transport", transport.Unix, "unix or pipe (Windows only)")
	socketDir := flags.String("socket-dir", "", "the directory for the unix socket; the client owns it")
	nonce := flags.String("nonce", "", "echoed in the ready line")
	database := flags.String("database", "", "the SQLite database file; one whatsmeow-net at a time")
	mediaRoot := flags.String("media-root", "", "the absolute directory the sidecar reads and writes media paths under; empty: media only in chunks")
	fake := flags.Bool("fake-whatsapp", false, "test builds only (-tags fakewhatsapp): replace WhatsApp with a scripted fake and serve its control")
	parentPID := flags.Int("parent-pid", 0, "the client's process id: the sidecar stops once that is no longer its parent (Linux, macOS; on Windows the client's job object does this; default: its parent at start)")

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

	if *database == "" {
		log.Error().Msg("usage: whatsmeow-net --database <file> is required")

		return exitUsage
	}

	// SIGPIPE too: a dead parent takes the read ends of stdout and stderr along, and the graceful stop's first log
	// line would otherwise kill the sidecar (os/signal SIGPIPE).
	signal.Ignore(os.Interrupt, syscall.SIGPIPE)
	wmlog.SetupSignal(log)

	ctx, stop := signal.NotifyContext(context.Background(), syscall.SIGTERM)
	defer stop()

	parent := *parentPID
	if parent == 0 {
		parent = os.Getppid()
	}

	ctx, untether, err := tetherToParent(ctx, log, parent)
	if err != nil {
		log.Error().Err(err).Msg("tether to the parent failed")

		return exitFailed
	}
	defer untether()

	log.Info().Str("version", version).Str("transport", *kind).Msg("starting")

	if err := serve(ctx, log, options{*kind, *socketDir, *nonce, *database, *mediaRoot, *fake}, bufio.NewReader(stdin), stdout); err != nil {
		log.Error().Err(err).Msg("sidecar failed")

		return exitFailed
	}

	return exitStopped
}

// newLogger is the sidecar's one logger: JSON lines on stderr, at info level and above. Every component's logger,
// whatsmeow's and libsignal's included, derives from it, so none logs debug: whatsmeow's debug lines carry every node
// it sends (client.go:990), phone numbers in them, and each QR code with the device's ADV secret (qrchan.go:97,
// pair.go:125-130) (ARCHITECTURE §7.8).
func newLogger(stderr io.Writer) zerolog.Logger {
	return zerolog.New(stderr).Level(zerolog.InfoLevel).With().Timestamp().Logger()
}

// options are the flags serve needs.
type options struct {
	kind, socketDir, nonce, database, mediaRoot string
	fake                                        bool
}

func serve(ctx context.Context, log zerolog.Logger, opts options, stdin *bufio.Reader, stdout io.Writer) error {
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

	db, err := store.Open(ctx, opts.database, log)
	if err != nil {
		return fmt.Errorf("open the store: %w", err)
	}
	defer closeStore(log, db)

	lis, endpoint, err := transport.Listen(ctx, opts.kind, opts.socketDir)
	if err != nil {
		return fmt.Errorf("listen: %w", err)
	}

	srv := server.New(token, log)
	svc := sidecar.New(version, build, srv.Shutdown)
	whatsmeowv1.RegisterSidecarServiceServer(srv, svc)

	newClient := session.NewWhatsmeowClient(log)
	if opts.fake {
		newClient = serveFake(srv, log, opts.database)
	}

	box := outbox.New(db.DB)
	// Each slice that records whatsmeow events in the outbox passes its mapper (eventsslice.Mapper).
	events := eventsslice.New(box, log, contacts.Event, chatstate.Event, groups.Event, media.Mapper(db.DB, log), messaging.Receipt) //nolint:contextcheck // whatsmeow gives an event handler no context (events.Handler)
	whatsmeowv1.RegisterEventServiceServer(srv, events)

	// Closed after the sessions: its recoveries end with their sessions' clients.
	chats := chatstate.New(log)
	defer chats.Close()

	sessions, err := session.New(ctx, db.DB, db.Devices, box, recording(newClient, events, chats), log) //nolint:contextcheck // whatsmeow gives an event handler no context (events.Handler)
	if err != nil {
		_ = lis.Close()

		return fmt.Errorf("load the sessions: %w", err)
	}
	defer sessions.Close()

	whatsmeowv1.RegisterSessionServiceServer(srv, sessions)
	whatsmeowv1.RegisterContactServiceServer(srv, contacts.New(sessions))
	whatsmeowv1.RegisterPresenceServiceServer(srv, presence.New(sessions))
	whatsmeowv1.RegisterGroupServiceServer(srv, groups.New(sessions))

	// The sidecar's own temporary media files go next to the database it owns, not to a tmpfs /tmp (ARCHITECTURE §7.6).
	mediaSlice, err := media.New(sessions, db.DB, opts.mediaRoot, opts.database+".tmp", log)
	if err != nil {
		_ = lis.Close()

		return fmt.Errorf("media: %w", err)
	}
	defer closeMedia(log, mediaSlice)

	whatsmeowv1.RegisterMediaServiceServer(srv, mediaSlice)

	// Closed once srv.Stop returned, before the sessions. Until then queued sends keep going out, paced: Stop waits for
	// the calls in flight up to the stop grace, then grpc's Stop cancels every call's context (grpc v1.85.0
	// server.go:1738-1744, internal/transport/http2_server.go:1300-1321), so a send still queued fails having sent
	// nothing, and the one handed to whatsmeow ends as its context says. Close waits for that one, so its outcome is
	// recorded before the sessions disconnect and the store closes.
	messages := messaging.New(db.DB, sessions, log)
	defer messages.Close()

	whatsmeowv1.RegisterMessageServiceServer(srv, messages)

	chats.Use(sessions)
	whatsmeowv1.RegisterChatStateServiceServer(srv, chats)

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
		Event: "ready", Version: version, Protocol: sidecar.Protocol, Whatsmeow: svc.WhatsmeowVersion(), Listen: endpoint, Nonce: opts.nonce,
	})
	if err != nil {
		srv.Stop()

		return errors.Join(fmt.Errorf("write the ready line: %w", err), <-served)
	}

	var reason string

	select {
	case <-ctx.Done():
		reason = context.Cause(ctx).Error()
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

// recording gives every client the Events slice's handler, first, so each session's events go into the outbox
// (ARCHITECTURE §7.3), then the Chat state slice's, which only signals its recovery; the session slice's own handler
// comes after them.
func recording(newClient session.NewClient, events *eventsslice.Service, chats *chatstate.Service) session.NewClient {
	return func(sessionID string, device *wastore.Device) session.Client {
		client := newClient(sessionID, device)
		client.AddEventHandlerWithSuccessStatus(events.Handler(sessionID))
		client.AddEventHandlerWithSuccessStatus(chats.Handler(sessionID))

		return client
	}
}

// closeMedia releases the media root.
func closeMedia(log zerolog.Logger, svc *media.Service) {
	if err := svc.Close(); err != nil {
		log.Error().Err(err).Msg("closing the media slice failed")
	}
}

// closeStore closes the store after the sessions disconnected, and releases its lock.
func closeStore(log zerolog.Logger, db *store.Store) {
	if err := db.Close(); err != nil {
		log.Error().Err(err).Msg("closing the store failed")
	}
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
