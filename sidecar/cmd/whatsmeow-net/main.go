// Command whatsmeow-net is the sidecar process (ARCHITECTURE §7.8).
//
// The client starts it with --database, --transport, --socket-dir, --nonce and --parent-pid, writes the per-launch
// token as the first line of stdin, and keeps stdin open: EOF on stdin, a Shutdown call, SIGTERM or, on Linux and
// macOS, its parent no longer being --parent-pid (the tether backup, even while another process keeps stdin open)
// stop it gracefully with exit code 0. SIGINT is ignored: the sidecar is always tethered, and a terminal Ctrl+C reaches the whole process
// group, so the client decides when it stops. Once it listens, it prints exactly one JSON ready line on stdout. Logs are JSON lines on
// stderr.
//
// Standalone (External mode, ADR 0046), an orchestrator starts it with --database, --listen and --token-file instead: the
// token comes from the file, stdin is never read, nothing tethers it to its parent, and SIGTERM, SIGINT or a Shutdown call
// stop it gracefully.
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
	"net"
	"os"
	"os/signal"
	"runtime/debug"
	"slices"
	"strings"
	"syscall"

	"github.com/rs/zerolog"
	"go.mau.fi/whatsmeow"
	wastore "go.mau.fi/whatsmeow/store"

	whatsmeowv1 "github.com/tcortega/Whatsmeow.NET/sidecar/gen/whatsmeow/v1"
	"github.com/tcortega/Whatsmeow.NET/sidecar/internal/chatstate"
	"github.com/tcortega/Whatsmeow.NET/sidecar/internal/contacts"
	eventsslice "github.com/tcortega/Whatsmeow.NET/sidecar/internal/events"
	"github.com/tcortega/Whatsmeow.NET/sidecar/internal/groups"
	historyslice "github.com/tcortega/Whatsmeow.NET/sidecar/internal/history"
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
	redactor := wmlog.NewRedactor()
	log := newLogger(stderr, redactor)

	flags := flag.NewFlagSet("whatsmeow-net", flag.ContinueOnError)
	flags.SetOutput(stderr)
	kind := flags.String("transport", transport.Unix, "unix or pipe (Windows only)")
	socketDir := flags.String("socket-dir", "", "the directory for the unix socket; the client owns it")
	nonce := flags.String("nonce", "", "echoed in the ready line")
	database := flags.String("database", "", "the SQLite database file; one whatsmeow-net at a time")
	mediaRoot := flags.String("media-root", "", "the absolute directory the sidecar reads and writes media paths under; empty: media only in chunks")
	fake := flags.Bool("fake-whatsapp", false, "test builds only (-tags fakewhatsapp): replace WhatsApp with a scripted fake and serve its control")
	parentPID := flags.Int("parent-pid", 0, "the client's process id: the sidecar stops once that is no longer its parent (Linux, macOS; on Windows the client's job object does this; default: its parent at start)")
	listen := flags.String("listen", "", "standalone: unix:<absolute path> or tcp:<host>:<port>, with --token-file; stdin is never read and nothing tethers the sidecar (ADR 0046)")
	tokenFile := flags.String("token-file", "", "standalone: the file that holds the token, 32 bytes as hex, with --listen")
	history := historyFlags(flags)
	sends := pacingFlags(flags)

	if err := flags.Parse(args); err != nil {
		return exitUsage
	}

	standalone := *listen != "" || *tokenFile != ""
	if flags.NArg() > 0 || (!standalone && *nonce == "") {
		log.Error().Msg("usage: whatsmeow-net --nonce <nonce> [--transport unix|pipe] [--socket-dir <dir>], or standalone: whatsmeow-net --listen <address> --token-file <file>")

		return exitUsage
	}

	if standalone && (*listen == "" || *tokenFile == "" || anySet(flags, "transport", "socket-dir", "nonce", "parent-pid")) {
		log.Error().Msg("usage: standalone, whatsmeow-net takes --listen and --token-file together, and none of --transport, --socket-dir, --nonce or --parent-pid")

		return exitUsage
	}

	if _, err := transport.ParseAddress(*listen); standalone && err != nil {
		log.Error().Err(err).Msg("usage: --listen is unix:<absolute path> or tcp:<host>:<port>")

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

	if err := sends.value().Check(); err != nil {
		log.Error().Err(err).Msg("usage: whatsmeow-net --send-interval and --send-burst")

		return exitUsage
	}

	// SIGPIPE too: a dead parent takes the read ends of stdout and stderr along, and the graceful stop's first log
	// line would otherwise kill the sidecar (os/signal SIGPIPE). SIGINT stops a standalone sidecar, which no client
	// stops; a tethered one ignores it, since a terminal Ctrl+C reaches the whole process group.
	signal.Ignore(syscall.SIGPIPE)

	stopSignals := []os.Signal{syscall.SIGTERM}
	if standalone {
		stopSignals = append(stopSignals, os.Interrupt)
	}

	if !standalone {
		signal.Ignore(os.Interrupt)
	}

	wmlog.SetupSignal(log)
	// Process-wide, so once, before any session pairs (ARCHITECTURE §7.2).
	history.apply(wastore.DeviceProps)

	ctx, stop := signal.NotifyContext(context.Background(), stopSignals...)
	defer stop()

	if !standalone {
		parent := *parentPID
		if parent == 0 {
			parent = os.Getppid()
		}

		var (
			untether func()
			err      error
		)

		ctx, untether, err = tetherToParent(ctx, log, parent)
		if err != nil {
			log.Error().Err(err).Msg("tether to the parent failed")

			return exitFailed
		}
		defer untether()
	}

	log.Info().Str("version", version).Str("transport", *kind).Str("listen", *listen).Msg("starting")

	opts := options{kind: *kind, socketDir: *socketDir, nonce: *nonce, database: *database, mediaRoot: *mediaRoot, listen: *listen, tokenFile: *tokenFile, fake: *fake, pacing: sends.value()}
	if err := serve(ctx, log, redactor, opts, bufio.NewReader(stdin), stdout); err != nil {
		log.Error().Err(err).Msg("sidecar failed")

		return exitFailed
	}

	return exitStopped
}

// newLogger is the sidecar's one logger: JSON lines on stderr, at info level and above, each with no phone number or
// JID user part (wmlog.Writer). Every component's logger, whatsmeow's, its store's and libsignal's included, derives
// from it, so none logs debug: whatsmeow's debug lines carry every node it sends (client.go:990), phone numbers in
// them, and each QR code with the device's ADV secret (qrchan.go:97, pair.go:125-130); and none writes a number:
// whatsmeow logs other people's JIDs at warn and error (ARCHITECTURE §7.8, ADR 0039).
func newLogger(stderr io.Writer, redactor *wmlog.Redactor) zerolog.Logger {
	return zerolog.New(wmlog.Writer(stderr, redactor)).Level(zerolog.InfoLevel).With().Timestamp().Logger()
}

// options are the flags serve needs. A listen address makes the sidecar standalone (ADR 0046).
type options struct {
	kind, socketDir, nonce, database, mediaRoot string
	listen, tokenFile                           string
	fake                                        bool
	pacing                                      messaging.Pacing
}

// anySet reports whether any of the named flags was given.
func anySet(flags *flag.FlagSet, names ...string) bool {
	set := false

	flags.Visit(func(f *flag.Flag) { set = set || slices.Contains(names, f.Name) })

	return set
}

func serve(ctx context.Context, log zerolog.Logger, redactor *wmlog.Redactor, opts options, stdin *bufio.Reader, stdout io.Writer) error {
	standalone := opts.listen != ""

	token, err := tokenFor(ctx, opts, stdin)
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

	lis, endpoint, err := listenFor(ctx, opts)
	if err != nil {
		return fmt.Errorf("listen: %w", err)
	}

	srv := server.New(token, log, redactor)
	if standalone {
		// A rotated secret is followed without a restart: a refused token makes it read the file again (ADR 0046).
		srv.ReloadToken(func() (string, error) { return readTokenFile(opts.tokenFile) })
	}

	svc := sidecar.New(version, build, srv.Shutdown)
	whatsmeowv1.RegisterSidecarServiceServer(srv, svc)

	newClient, fail := session.NewWhatsmeowClient(log), failing(nil)
	if opts.fake {
		newClient, fail = serveFake(srv, log, opts.database)
	}

	box := outbox.New(db.DB, db.Writer)
	// Each slice that records whatsmeow events in the outbox passes its mapper (eventsslice.Mapper).
	events := eventsslice.New(box, log, contacts.Event, chatstate.Event, groups.Event, media.Mapper(db.DB, db.Writer, log), messaging.Receipt) //nolint:contextcheck // whatsmeow gives an event handler no context (events.Handler)
	whatsmeowv1.RegisterEventServiceServer(srv, events)

	// Closed after the sessions: its recoveries end with their sessions' clients.
	chats := chatstate.New(log)
	defer chats.Close()

	// Closed after the sessions too: its workers end with their sessions' clients.
	histories := historyslice.New(db.DB, db.Writer, box, eventsslice.HistoryMessage, log)
	defer histories.Close()

	sessions, err := session.New(ctx, db.DB, db.Writer, db.Devices, box, recording(newClient, fail, events, chats, histories), log) //nolint:contextcheck // whatsmeow gives an event handler no context (events.Handler)
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
	mediaSlice, err := media.New(sessions, db.DB, db.Writer, opts.mediaRoot, opts.database+".tmp", log)
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
	messages := messaging.New(db.DB, db.Writer, sessions, log, opts.pacing)
	defer messages.Close()

	whatsmeowv1.RegisterMessageServiceServer(srv, messages)

	chats.Use(sessions)
	whatsmeowv1.RegisterChatStateServiceServer(srv, chats)

	histories.Use(sessions)
	whatsmeowv1.RegisterHistoryServiceServer(srv, histories)

	served := make(chan error, 1)

	go func() { served <- srv.Serve(lis) }()

	// The tether: stdin stays open for the sidecar's whole life, and EOF (the parent closed it or died) stops it.
	// The read cannot be cancelled; the goroutine ends with the process. A standalone sidecar never reads stdin, so
	// its tether never closes.
	tether := make(chan struct{})

	if !standalone {
		go func() {
			_, _ = io.Copy(io.Discard, stdin)
			close(tether)
		}()
	}

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

// failing wraps a session's Events handler so it fails as a failed outbox write would: the fake mode's
// withhold_first_answer (fake_on.go). nil in a release build and outside fake mode.
type failing func(sessionID string, h whatsmeow.EventHandlerWithSuccessStatus) whatsmeow.EventHandlerWithSuccessStatus

// recording gives every client the Events slice's handler, first, so each session's events go into the outbox
// (ARCHITECTURE §7.3), then the Chat state slice's, which only signals its recovery, then the History slice's, which
// queues history sync notifications and starts the session's history worker; the session slice's own handler comes
// after them. Each one that withholds an answer wakes the session's redelivery (ADR 0044). In fake mode, fail wraps
// the Events handler.
func recording(newClient session.NewClient, fail failing, events *eventsslice.Service, chats *chatstate.Service, histories *historyslice.Service) session.NewClient {
	return func(sessionID string, device *wastore.Device, withheld func()) session.Client {
		client := newClient(sessionID, device, withheld)

		record := events.Handler(sessionID, device)
		if fail != nil {
			record = fail(sessionID, record)
		}

		for _, handler := range []whatsmeow.EventHandlerWithSuccessStatus{record, chats.Handler(sessionID), histories.Handler(sessionID)} {
			client.AddEventHandlerWithSuccessStatus(redelivering(handler, withheld))
		}

		return client
	}
}

// redelivering is handler, which calls withheld each time it withholds an answer: whatsmeow skips the handlers after
// one that failed (client.go:1008-1011), so the session slice's own never sees it. withheld never waits.
func redelivering(handler whatsmeow.EventHandlerWithSuccessStatus, withheld func()) whatsmeow.EventHandlerWithSuccessStatus {
	return func(evt any) bool {
		if handler(evt) {
			return true
		}

		withheld()

		return false
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

// tokenFor reads the token from the token file when the sidecar is standalone, else from stdin.
func tokenFor(ctx context.Context, opts options, stdin *bufio.Reader) (string, error) {
	if opts.listen != "" {
		return readTokenFile(opts.tokenFile)
	}

	return awaitToken(ctx, stdin)
}

// readToken reads the first stdin line: the per-launch token, 32 bytes as hex.
func readToken(stdin *bufio.Reader) (string, error) {
	line, err := stdin.ReadString('\n')
	if err != nil {
		return "", fmt.Errorf("read the token from stdin: %w", err)
	}

	return checkToken(line)
}

// readTokenFile reads a standalone sidecar's token from its file: one line, 32 bytes as hex, the final newline optional
// (a secret mount may have none). An error never carries what the file holds.
func readTokenFile(path string) (string, error) {
	content, err := os.ReadFile(path) //nolint:gosec // G304: the operator's own --token-file
	if err != nil {
		return "", fmt.Errorf("read the token file: %w", err)
	}

	return checkToken(string(content))
}

// checkToken returns line without its line ending when that is 32 bytes as hex.
func checkToken(line string) (string, error) {
	token := strings.TrimSuffix(strings.TrimSuffix(line, "\n"), "\r")

	raw, err := hex.DecodeString(token)
	if err != nil || len(raw) != tokenBytes {
		return "", fmt.Errorf("the token must be %d bytes as hex", tokenBytes)
	}

	return token, nil
}

// listenFor opens the standalone sidecar's listen address, or the tethered one's private socket or pipe.
func listenFor(ctx context.Context, opts options) (net.Listener, string, error) {
	if opts.listen != "" {
		return transport.ListenAddress(ctx, opts.listen) //nolint:wrapcheck // serve wraps it
	}

	return transport.Listen(ctx, opts.kind, opts.socketDir) //nolint:wrapcheck // serve wraps it
}
