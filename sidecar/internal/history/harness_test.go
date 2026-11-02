package history

import (
	"bytes"
	"context"
	"errors"
	"fmt"
	"net"
	"path/filepath"
	"strings"
	"sync"
	"testing"
	"time"

	"github.com/rs/zerolog"
	"go.mau.fi/whatsmeow"
	"go.mau.fi/whatsmeow/proto/waAdv"
	"go.mau.fi/whatsmeow/proto/waCommon"
	"go.mau.fi/whatsmeow/proto/waE2E"
	"go.mau.fi/whatsmeow/proto/waHistorySync"
	"go.mau.fi/whatsmeow/proto/waWeb"
	"go.mau.fi/whatsmeow/store"
	"go.mau.fi/whatsmeow/types"
	"go.mau.fi/whatsmeow/types/events"
	"google.golang.org/genproto/googleapis/rpc/errdetails"
	"google.golang.org/grpc"
	"google.golang.org/grpc/credentials/insecure"
	"google.golang.org/grpc/metadata"
	"google.golang.org/grpc/status"
	"google.golang.org/grpc/test/bufconn"
	"google.golang.org/protobuf/proto"

	whatsmeowv1 "github.com/tcortega/Whatsmeow.NET/sidecar/gen/whatsmeow/v1"
	eventsslice "github.com/tcortega/Whatsmeow.NET/sidecar/internal/events"
	"github.com/tcortega/Whatsmeow.NET/sidecar/internal/outbox"
	"github.com/tcortega/Whatsmeow.NET/sidecar/internal/server"
	"github.com/tcortega/Whatsmeow.NET/sidecar/internal/session"
	sidecarstore "github.com/tcortega/Whatsmeow.NET/sidecar/internal/store"
)

const token = "00112233445566778899aabbccddeeff00112233445566778899aabbccddeeff"

// The account and the chats of the tests; the users are what no status or log line may carry.
const (
	own    = "5511000000000:1@s.whatsapp.net"
	direct = "5511987654321@s.whatsapp.net"
	group  = "120363000000000001@g.us"
	member = "5511912345678@s.whatsapp.net"
)

// stub stands in for whatsmeow behind the slice's interface; the session methods it never needs stay nil. It parses
// and builds with a real whatsmeow client that never connects, so what the slice gets is what whatsmeow makes.
type stub struct {
	session.Client

	real *whatsmeow.Client

	mu sync.Mutex
	// blobs is each direct path's blob; errs are what each download of a path returns in turn, before its blob.
	blobs map[string]*waHistorySync.HistorySync
	errs  map[string][]error
	// downloads and deletes are the direct paths downloaded and deleted, in order; entered gets each download as it
	// starts, and hold, when set, holds each download until closed or the call ends.
	downloads, deletes []string
	entered            chan string
	hold               chan struct{}
	// onDelete runs inside each delete, before it returns.
	onDelete func(directPath string)
	// sends are the messages sent; sendErr is what each returns.
	sends   []sent
	sendErr error
}

type sent struct {
	to      types.JID
	message *waE2E.Message
	extra   whatsmeow.SendRequestExtra
}

func (s *stub) DownloadHistorySync(ctx context.Context, notif *waE2E.HistorySyncNotification, _ bool) (*waHistorySync.HistorySync, error) {
	path := notif.GetDirectPath()

	s.mu.Lock()
	s.downloads = append(s.downloads, path)
	hold := s.hold
	s.mu.Unlock()

	if s.entered != nil {
		s.entered <- path
	}

	if hold != nil {
		select {
		case <-hold:
		case <-ctx.Done():
			return nil, fmt.Errorf("failed to download: %w", ctx.Err())
		}
	}

	s.mu.Lock()
	defer s.mu.Unlock()

	if errs := s.errs[path]; len(errs) > 0 {
		s.errs[path] = errs[1:]

		return nil, errs[0]
	}

	blob, ok := s.blobs[path]
	if !ok {
		return nil, fmt.Errorf("failed to download: %w", whatsmeow.ErrMediaDownloadFailedWith404)
	}

	return proto.CloneOf(blob), nil
}

func (s *stub) DeleteMedia(_ context.Context, appInfo whatsmeow.MediaType, directPath string, _ []byte, _ string) error {
	if appInfo != whatsmeow.MediaHistory {
		return fmt.Errorf("deleted %q media", appInfo)
	}

	if s.onDelete != nil {
		s.onDelete(directPath)
	}

	s.mu.Lock()
	defer s.mu.Unlock()

	s.deletes = append(s.deletes, directPath)

	return nil
}

func (s *stub) ParseWebMessage(chat types.JID, webMsg *waWeb.WebMessageInfo) (*events.Message, error) {
	return s.real.ParseWebMessage(chat, webMsg) //nolint:wrapcheck // whatsmeow's own, as the slice gets it
}

func (s *stub) BuildHistorySyncRequest(last *types.MessageInfo, count int) *waE2E.Message {
	return s.real.BuildHistorySyncRequest(last, count)
}

func (s *stub) SendMessage(_ context.Context, to types.JID, message *waE2E.Message, extra ...whatsmeow.SendRequestExtra) (whatsmeow.SendResponse, error) {
	s.mu.Lock()
	defer s.mu.Unlock()

	s.sends = append(s.sends, sent{to: to, message: message, extra: extra[0]})

	return whatsmeow.SendResponse{}, s.sendErr
}

func (s *stub) downloaded() []string {
	s.mu.Lock()
	defer s.mu.Unlock()

	return append([]string(nil), s.downloads...)
}

func (s *stub) deleted() []string {
	s.mu.Lock()
	defer s.mu.Unlock()

	return append([]string(nil), s.deletes...)
}

// sessions stands in for the Sessions slice: every session the harness made, online with its stub until the test
// takes it offline.
type sessions struct {
	mu     sync.Mutex
	online map[string]*online
}

type online struct {
	client  session.Client
	device  *store.Device
	left    chan struct{}
	offline error
}

func (s *sessions) Online(id string) (session.Client, <-chan struct{}, error) {
	s.mu.Lock()
	defer s.mu.Unlock()

	o, ok := s.online[id]
	if !ok {
		return nil, nil, fmt.Errorf("session %q: %w", id, server.ErrSessionNotFound)
	}

	if o.offline != nil {
		return nil, nil, o.offline
	}

	return o.client, o.left, nil
}

func (s *sessions) Device(id string) (*store.Device, error) {
	s.mu.Lock()
	defer s.mu.Unlock()

	o, ok := s.online[id]
	if !ok {
		return nil, fmt.Errorf("session %q: %w", id, server.ErrSessionNotFound)
	}

	return o.device, nil
}

// leave takes the session offline, as leaving Connected and CaughtUp does.
func (s *sessions) leave(id string) {
	s.mu.Lock()
	defer s.mu.Unlock()

	o := s.online[id]
	o.offline = fmt.Errorf("session %q: %w", id, server.ErrNotConnected)
	close(o.left)
}

type harness struct {
	t        *testing.T
	db       *sidecarstore.Store
	box      *outbox.Outbox
	sessions *sessions
	stubs    map[string]*stub
	svc      *Service
	client   whatsmeowv1.HistoryServiceClient
	ctx      context.Context //nolint:containedctx // the test's bound
	// waits gets the session of each wait for the consumers (Service.onWait).
	waits chan string
	// queued gets the session of each wait for another session's download (Service.onQueue).
	queued chan string
	// idle gets the session each time its worker found nothing left to do (Service.onIdle).
	idle chan string
	stop func()
}

func start(t *testing.T, ids ...string) *harness {
	t.Helper()

	return startLogging(t, zerolog.Nop(), filepath.Join(t.TempDir(), "whatsmeow-net.db"), ids...)
}

// startLogging starts the slice on the database at path, logging to log, with a paired session for each id ("s1"
// without any).
func startLogging(t *testing.T, log zerolog.Logger, path string, ids ...string) *harness {
	t.Helper()

	if len(ids) == 0 {
		ids = []string{"s1"}
	}

	db, err := sidecarstore.Open(context.Background(), path, zerolog.Nop())
	if err != nil {
		t.Fatalf("open the store: %v", err)
	}

	// A bound for a broken slice, never a wait: every wait below ends on a condition.
	ctx, cancel := context.WithTimeout(context.Background(), 60*time.Second)
	t.Cleanup(cancel)

	h := &harness{
		t: t, db: db, box: outbox.New(db.DB), sessions: &sessions{online: map[string]*online{}}, stubs: map[string]*stub{},
		ctx: ctx, waits: make(chan string, 1024), queued: make(chan string, 16), idle: make(chan string, 1024),
	}

	for _, id := range ids {
		h.addSession(id)
	}

	h.svc = New(db.DB, h.box, eventsslice.MessageEvent, log)
	h.svc.Use(h.sessions)
	h.svc.retryAfter = func(time.Duration) <-chan time.Time {
		now := make(chan time.Time, 1)
		now <- time.Time{}

		return now
	}
	h.svc.onWait = func(id string) { h.waits <- id }
	h.svc.onQueue = func(id string) { h.queued <- id }
	h.svc.onIdle = func(id string) { h.idle <- id }

	srv := server.New(token, log)
	whatsmeowv1.RegisterHistoryServiceServer(srv, h.svc)

	lis := bufconn.Listen(1 << 20)
	served := make(chan error, 1)

	go func() { served <- srv.Serve(lis) }()

	conn, err := grpc.NewClient("passthrough:///bufnet",
		grpc.WithContextDialer(func(ctx context.Context, _ string) (net.Conn, error) { return lis.DialContext(ctx) }),
		grpc.WithTransportCredentials(insecure.NewCredentials()))
	if err != nil {
		t.Fatalf("dial: %v", err)
	}

	stopped := false
	stop := func() {
		if stopped {
			return
		}

		stopped = true

		_ = conn.Close()
		srv.Shutdown()
		srv.Stop()

		if err := <-served; err != nil {
			t.Errorf("serve: %v", err)
		}

		h.svc.Close()

		if err := db.Close(); err != nil {
			t.Errorf("close the store: %v", err)
		}
	}
	t.Cleanup(stop)

	h.client = whatsmeowv1.NewHistoryServiceClient(conn)
	h.ctx = metadata.AppendToOutgoingContext(ctx, "authorization", "Bearer "+token)
	h.stop = stop

	return h
}

// addSession makes a paired session with its own device and stub.
func (h *harness) addSession(id string) {
	h.t.Helper()

	device := h.db.Devices.NewDevice()
	jid := types.NewJID(strings.TrimPrefix(id, "s")+"5511000000", types.DefaultUserServer)
	if id == "s1" {
		jid = types.NewJID("5511000000000", types.DefaultUserServer)
	}

	jid.Device = 1
	device.ID = &jid
	device.Account = &waAdv.ADVSignedDeviceIdentity{
		Details: []byte{}, AccountSignature: make([]byte, 64), AccountSignatureKey: make([]byte, 32), DeviceSignature: make([]byte, 64),
	}

	if err := device.Save(context.Background()); err != nil {
		h.t.Fatalf("save the device: %v", err)
	}

	if _, err := h.db.DB.ExecContext(context.Background(), "INSERT INTO session (id, jid) VALUES (?, ?) ON CONFLICT DO NOTHING", id, jid.String()); err != nil {
		h.t.Fatalf("create session %s: %v", id, err)
	}

	s := &stub{real: whatsmeow.NewClient(device, nil), blobs: map[string]*waHistorySync.HistorySync{}, errs: map[string][]error{}}
	h.stubs[id] = s
	h.sessions.online[id] = &online{client: s, device: device, left: make(chan struct{})}
}

// stop ends the slice and closes the store, as a sidecar that stops does; the cleanup does it otherwise.
func (h *harness) stopNow() { h.stop() }

// handle runs the slice's handler on evt, as whatsmeow does for the session's client.
func (h *harness) handle(id string, evt any) bool {
	return h.svc.Handler(id)(evt)
}

// notify delivers the notification of blob at directPath, with id, from the account's phone.
func (h *harness) notify(id, messageID, directPath string, blob *waHistorySync.HistorySync) {
	h.t.Helper()

	h.stubs[id].mu.Lock()
	h.stubs[id].blobs[directPath] = blob
	h.stubs[id].mu.Unlock()

	if !h.handle(id, notification(messageID, directPath, blob)) {
		h.t.Fatalf("the handler withheld the answer to %s", messageID)
	}
}

// caughtUp delivers OfflineSyncCompleted.
func (h *harness) caughtUp(id string) {
	h.t.Helper()

	if !h.handle(id, &events.OfflineSyncCompleted{}) {
		h.t.Fatal("the handler failed OfflineSyncCompleted")
	}
}

// read returns the session's next events after the seq after, waiting until there are n.
func (h *harness) read(id string, after uint64, n int) []*whatsmeowv1.StreamEventsResponse {
	h.t.Helper()

	for {
		watch, _, err := h.box.Watch(h.ctx, id)
		if err != nil {
			h.t.Fatalf("watch: %v", err)
		}

		changed := watch.Changed()

		got, err := h.box.Read(h.ctx, id, after, 10_000)
		if err != nil {
			h.t.Fatalf("read: %v", err)
		}

		if len(got) >= n {
			return got[:n]
		}

		select {
		case <-changed:
		case <-h.ctx.Done():
			h.t.Fatalf("only %d of %d events were recorded", len(got), n)
		}
	}
}

// waitedFor waits until the session's worker waits for the consumers.
func (h *harness) waitedFor(id string) {
	h.t.Helper()

	for {
		select {
		case got := <-h.waits:
			if got == id {
				return
			}
		case <-h.ctx.Done():
			h.t.Fatalf("session %s never waited for its consumers", id)
		}
	}
}

// idleFor waits until the session's worker found nothing left to do.
func (h *harness) idleFor(id string) {
	h.t.Helper()

	for {
		select {
		case got := <-h.idle:
			if got == id {
				return
			}
		case <-h.ctx.Done():
			h.t.Fatalf("session %s's worker never went idle", id)
		}
	}
}

// running reports whether the session has a worker.
func (h *harness) running(id string) bool {
	h.svc.mu.Lock()
	defer h.svc.mu.Unlock()

	return h.svc.workers[id] != nil
}

// ended waits until the session has no worker.
func (h *harness) ended(id string) {
	h.t.Helper()

	h.svc.mu.Lock()
	w := h.svc.workers[id]
	h.svc.mu.Unlock()

	if w == nil {
		return
	}

	select {
	case <-w.done:
	case <-h.ctx.Done():
		h.t.Fatalf("session %s's worker never ended", id)
	}
}

// reason is the ErrorInfo reason of a call's error; "" for none.
func reason(err error) string {
	st, ok := status.FromError(err)
	if !ok {
		return ""
	}

	for _, d := range st.Details() {
		if info, ok := d.(*errdetails.ErrorInfo); ok {
			return info.GetReason()
		}
	}

	return ""
}

// queueRow is the session's queued notification rows: message id, batches done, recorded, attempts.
func (h *harness) queueRows(id string) []string {
	h.t.Helper()

	rows, err := h.db.DB.QueryContext(h.ctx, "SELECT message_id, batches_done, recorded, attempts FROM history_blob WHERE session_id = ? ORDER BY seq", id)
	if err != nil {
		h.t.Fatalf("read the queue: %v", err)
	}
	defer func() { _ = rows.Close() }()

	var got []string

	for rows.Next() {
		var (
			messageID                string
			done, recorded, attempts int
		)

		if err := rows.Scan(&messageID, &done, &recorded, &attempts); err != nil {
			h.t.Fatalf("scan: %v", err)
		}

		got = append(got, fmt.Sprintf("%s %d %d %d", messageID, done, recorded, attempts))
	}

	if err := rows.Err(); err != nil {
		h.t.Fatalf("read the queue: %v", err)
	}

	return got
}

// notification is the protocol message the account's phone sends for a blob (message.go:884-907), as whatsmeow
// dispatches it.
func notification(id, directPath string, blob *waHistorySync.HistorySync) *events.Message {
	chat := types.NewJID("5511000000000", types.DefaultUserServer)

	return &events.Message{
		Info: types.MessageInfo{
			MessageSource: types.MessageSource{Chat: chat, Sender: chat, IsFromMe: true},
			ID:            id, Category: "peer", Timestamp: time.Unix(1_700_000_000, 0),
		},
		Message: &waE2E.Message{ProtocolMessage: &waE2E.ProtocolMessage{
			Type: waE2E.ProtocolMessage_HISTORY_SYNC_NOTIFICATION.Enum(),
			HistorySyncNotification: &waE2E.HistorySyncNotification{
				DirectPath: proto.String(directPath), FileEncSHA256: []byte{1, 2, 3}, EncHandle: proto.String("handle"),
				SyncType: waE2E.HistorySyncType(blob.GetSyncType()).Enum(), ChunkOrder: proto.Uint32(blob.GetChunkOrder()),
				Progress: proto.Uint32(blob.GetProgress()),
			},
		}},
	}
}

// texts is a conversation of n text messages from the chat's other user, ids prefix1 to prefixn.
func texts(chat, prefix string, n int) *waHistorySync.Conversation {
	conv := &waHistorySync.Conversation{ID: proto.String(chat)}
	for i := 1; i <= n; i++ {
		conv.Messages = append(conv.Messages, &waHistorySync.HistorySyncMsg{Message: webMessage(chat, fmt.Sprintf("%s%d", prefix, i), false, "", fmt.Sprintf("text %s%d", prefix, i))})
	}

	return conv
}

func webMessage(chat, id string, fromMe bool, participant, text string) *waWeb.WebMessageInfo {
	msg := &waWeb.WebMessageInfo{
		Key:              &waCommon.MessageKey{RemoteJID: proto.String(chat), FromMe: proto.Bool(fromMe), ID: proto.String(id)},
		Message:          &waE2E.Message{Conversation: proto.String(text)},
		MessageTimestamp: proto.Uint64(1_600_000_000),
	}
	if participant != "" {
		msg.Key.Participant = proto.String(participant)
	}

	return msg
}

func blob(syncType waHistorySync.HistorySync_HistorySyncType, conversations ...*waHistorySync.Conversation) *waHistorySync.HistorySync {
	return &waHistorySync.HistorySync{SyncType: syncType.Enum(), ChunkOrder: proto.Uint32(1), Progress: proto.Uint32(50), Conversations: conversations}
}

// messageIDs is the ids of the history messages of events, in order, and the progress events among them.
func messageIDs(t *testing.T, got []*whatsmeowv1.StreamEventsResponse) (ids []string, progress []*whatsmeowv1.HistorySyncProgressEvent) {
	t.Helper()

	for _, e := range got {
		switch {
		case e.GetHistoryMessages() != nil:
			for _, m := range e.GetHistoryMessages().GetMessages() {
				ids = append(ids, m.GetInfo().GetId())
			}
		case e.GetHistoryProgress() != nil:
			progress = append(progress, e.GetHistoryProgress())
		default:
			t.Fatalf("an event that is not history: %v", e)
		}
	}

	return ids, progress
}

func seqOf(events []*whatsmeowv1.StreamEventsResponse) uint64 { return events[len(events)-1].GetSeq() }

// logged is a log the slice writes to, read after the slice stopped.
type logged struct {
	mu  sync.Mutex
	buf bytes.Buffer
}

func (l *logged) Write(p []byte) (int, error) {
	l.mu.Lock()
	defer l.mu.Unlock()

	return l.buf.Write(p) //nolint:wrapcheck // a bytes.Buffer
}

func (l *logged) String() string {
	l.mu.Lock()
	defer l.mu.Unlock()

	return l.buf.String()
}

// networkError is a download that failed on the network after whatsmeow's own retries.
func networkError() error {
	return fmt.Errorf("failed to download: %w", &net.OpError{Op: "read", Net: "tcp", Err: errors.New("connection reset by peer")})
}
