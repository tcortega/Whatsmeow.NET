// Package history is the History sync slice (ARCHITECTURE §7.7, ADR 0035): the account's history, which WhatsApp sends
// after pairing in blobs, and more on request, delivered as history events that never delay live traffic.
//
// whatsmeow runs with ManualHistorySyncDownload (§7.2), so it deletes nothing itself. The slice's event handler queues
// each notification in history_blob before whatsmeow answers it, and one worker per session delivers the queue once
// the session's offline sync completed and the session is online: it downloads a blob, parses its messages as
// whatsmeow parses a live one, and records them in batches small enough for one gRPC message, then a progress event,
// and only then deletes the blob from WhatsApp's server. A blob half done when the sidecar stops is processed again
// from its next message.
//
// Live traffic keeps priority: the worker keeps at most window history events of a session in the outbox that its
// consumers have not all acked, so a live message is never recorded behind more than that, and the .NET client runs
// every history event in one lane of its own.
//
// Chat JIDs are other people's personal data: nothing here logs them, and no error this package returns carries one,
// whatsmeow's text included.
package history

import (
	"compress/flate"
	"compress/zlib"
	"context"
	"crypto/rand"
	"database/sql"
	"encoding/hex"
	"errors"
	"fmt"
	"math"
	"regexp"
	"runtime/debug"
	"strconv"
	"strings"
	"sync"
	"time"

	"github.com/rs/zerolog"
	"go.mau.fi/whatsmeow"
	"go.mau.fi/whatsmeow/proto/waE2E"
	"go.mau.fi/whatsmeow/proto/waHistorySync"
	"go.mau.fi/whatsmeow/proto/waWeb"
	"go.mau.fi/whatsmeow/store"
	"go.mau.fi/whatsmeow/types"
	waevents "go.mau.fi/whatsmeow/types/events"
	"google.golang.org/protobuf/proto"

	whatsmeowv1 "github.com/tcortega/Whatsmeow.NET/sidecar/gen/whatsmeow/v1"
	"github.com/tcortega/Whatsmeow.NET/sidecar/internal/outbox"
	"github.com/tcortega/Whatsmeow.NET/sidecar/internal/server"
	"github.com/tcortega/Whatsmeow.NET/sidecar/internal/session"
	sidecarstore "github.com/tcortega/Whatsmeow.NET/sidecar/internal/store"
)

const (
	// window is how many of a session's history events the worker keeps in the outbox before every consumer acked
	// them: a live message is recorded behind at most this many. With no consumer, or one that stopped acking, the
	// history waits there [observed: TestTheWorkerKeepsAtMostAWindowOfHistoryUntilTheConsumersAck].
	window = 4
	// maxBatchMessages and maxBatchBytes bound a batch: at most this many messages, and at most this many bytes as one
	// StreamEventsResponse, unless it is one message alone, a poll with its votes [inferred: a trade between the window's memory in .NET and
	// the events per ack; ADR 0035 records the drain rate].
	maxBatchMessages = 256
	maxBatchBytes    = 1 << 20
	// maxEventBytes is the most one message alone may take as an event: below the 16 MiB both sides of the socket
	// accept (ARCHITECTURE §8.6), with room for the seq and the framing. A larger message is skipped and counted.
	maxEventBytes = 15 << 20
	// attempts is how many downloads of a blob may fail while the session is online before it is reported
	// unavailable; retryDelay is the wait between them (mautrix-whatsapp waits a minute after a failed one,
	// pkg/connector/backfill.go historySyncLoop).
	attempts   = 3
	retryDelay = time.Minute
	// downloadTimeout bounds one download, as the Chat state slice bounds a full sync: whatsmeow's media client has no
	// timeout of its own, and one download runs at a time across the sessions [inferred: no measurement of a large
	// blob's download time; five minutes is far above a blob at the size limits' defaults on a slow link].
	downloadTimeout = 5 * time.Minute
	// upTimeout bounds how long a worker started by the offline sync waits for its session to come online:
	// whatsmeow dispatches Connected on its own goroutine after two IQs (connectionevents.go:183-205), each bounded by
	// its 75 s request timeout, while the offline ib comes on the node queue, so OfflineSyncCompleted can come first.
	upTimeout = 3 * time.Minute
	// minBackoff and maxBackoff bound the wait before a worker delivers its queue again after a failure that is
	// neither a download's nor offline: a store error. It doubles from minBackoff, and starts there again after a
	// delivery that succeeded.
	minBackoff = time.Second
	maxBackoff = time.Minute
	// prefix starts the outbox key of every history event: a blob's batch is prefix, its notification's id, 0, the
	// blob index of its first message; its progress event is prefix, the id, 0, "p".
	prefix = "h\x00"
)

// idPattern is the id whatsmeow makes for a message (send.go GenerateMessageID): 3EB0 and 18 uppercase hex digits.
var idPattern = regexp.MustCompile(`^3EB0[0-9A-F]{18}$`)

// Client is what the slice needs of a whatsmeow client. *whatsmeow.Client and the fake WhatsApp's client both
// satisfy it (ADR 0019).
type Client interface {
	DownloadHistorySync(ctx context.Context, notif *waE2E.HistorySyncNotification, synchronousStorage bool) (*waHistorySync.HistorySync, error)
	DeleteMedia(ctx context.Context, appInfo whatsmeow.MediaType, directPath string, encFileHash []byte, encHandle string) error
	ParseWebMessage(chatJID types.JID, webMsg *waWeb.WebMessageInfo) (*waevents.Message, error)
	BuildHistorySyncRequest(lastKnownMessageInfo *types.MessageInfo, count int) *waE2E.Message
	SendMessage(ctx context.Context, to types.JID, message *waE2E.Message, extra ...whatsmeow.SendRequestExtra) (whatsmeow.SendResponse, error)
	IsConnected() bool
}

var _ Client = (*whatsmeow.Client)(nil)

// Sessions is what the slice needs of the Sessions slice: a session's client while it is online, a channel closed
// once it is, and its device for the account's own JID.
type Sessions interface {
	Online(id string) (session.Client, <-chan struct{}, error)
	Up(id string) (<-chan struct{}, error)
	Device(id string) (*store.Device, error)
}

// MessageEvent maps a whatsmeow message of the session whose device is account to the contract's MessageEvents: the
// Events slice's mapping of a live one, then a poll's votes (events.HistoryMessage), so a history message carries what
// a live one does.
type MessageEvent func(e *waevents.Message, account *store.Device) []*whatsmeowv1.MessageEvent

// Service implements whatsmeowv1.HistoryServiceServer, the whatsmeow event handler that queues notifications, and the
// sessions' workers.
type Service struct {
	whatsmeowv1.UnimplementedHistoryServiceServer

	db *sql.DB
	w  *sidecarstore.Writer
	// queueTimeout bounds the handler's writes (Handler).
	queueTimeout time.Duration
	box          *outbox.Outbox
	log          zerolog.Logger
	toEvent      MessageEvent
	sessions     Sessions
	// download lets one download, parse and cut into batches run at a time across the sessions: a blob is held whole
	// in memory (DownloadHistorySync, message.go:783-825).
	download chan struct{}
	// after is the clock of every wait but a download's bound: between attempts, the backoff, and upTimeout.
	after func(time.Duration) <-chan time.Time
	// downloadTimeout bounds each download; a field so a test can stall one.
	downloadTimeout time.Duration
	// onWait, onQueue, onIdle and onOffline tell a test that a worker waits for its consumers, waits for another
	// session's download, found nothing left to do, or stopped because its client went offline.
	onWait, onQueue, onIdle, onOffline func(sessionID string)

	// ctx ends the workers at Close.
	ctx    context.Context //nolint:containedctx // the workers' lifetime, ended by Close
	cancel context.CancelFunc
	mu     sync.Mutex
	// workers is each session's worker while it runs.
	workers map[string]*worker
	running sync.WaitGroup
}

// worker is one session's worker: wake has room for one signal, done is closed once it ended.
type worker struct {
	wake chan struct{}
	done chan struct{}
	// online is the channel of the connection the worker delivers on, nil while it waits for the session to come
	// online; ended is set once it gave up waiting. Both under Service.mu.
	online <-chan struct{}
	ended  bool
}

// New returns the slice on the database, which it writes through w, and the outbox. toEvent is the Events slice's
// message mapping. Use gives it the sessions before any client connects.
func New(db *sql.DB, w *sidecarstore.Writer, box *outbox.Outbox, toEvent MessageEvent, log zerolog.Logger) *Service {
	ctx, cancel := context.WithCancel(context.Background())

	return &Service{
		db: db, w: w, queueTimeout: sidecarstore.BusyTimeout, box: box, log: log, toEvent: toEvent, download: make(chan struct{}, 1), after: time.After, downloadTimeout: downloadTimeout,
		onWait: func(string) {}, onQueue: func(string) {}, onIdle: func(string) {}, onOffline: func(string) {},
		ctx: ctx, cancel: cancel, workers: map[string]*worker{},
	}
}

// Use gives the slice the sessions. The Sessions slice makes the clients, whose handler is this slice's (Handler), so
// it comes after; it is called once, before any client connects.
func (svc *Service) Use(sessions Sessions) { svc.sessions = sessions }

// Close ends the workers and waits for them. cmd/whatsmeow-net calls it after the sessions closed.
func (svc *Service) Close() {
	svc.mu.Lock()
	svc.cancel()
	svc.mu.Unlock()

	svc.running.Wait()
}

// Handler is the slice's whatsmeow event handler for a session's client (ARCHITECTURE §7.3). A history sync
// notification from the account's own phone is queued in one short transaction, and the session's worker woken
// without waiting; it returns true only after the commit, so whatsmeow answers the notification, a decrypted message,
// with its delivery receipt only then (message.go:459-497), and false when the write fails or anything panics, with
// its own recover (client.go:1001-1015). whatsmeow sends the hist_sync receipt on its own goroutine before any handler
// runs, whatever the handler returns (message.go:908-915, 1181-1187). OfflineSyncCompleted starts the session's
// worker, or wakes the one this connection has. LoggedOut and PairSuccess take the queue of the account the session
// had: a new pairing is a new account, whose blobs come anew. It never downloads, and never waits for .NET, the
// network or media.
func (svc *Service) Handler(sessionID string) whatsmeow.EventHandlerWithSuccessStatus {
	return func(evt any) (ok bool) {
		defer func() {
			if r := recover(); r != nil {
				// The panic's type only: its value may carry a JID.
				svc.log.Error().Str("session_id", sessionID).Str("panic", fmt.Sprintf("%T", r)).Msgf("queueing a %T panicked; its answer is withheld", evt)

				ok = false
			}
		}()

		switch e := evt.(type) {
		case *waevents.Message:
			notif := e.Message.GetProtocolMessage().GetHistorySyncNotification()
			// whatsmeow acts only on the account's own (message.go:897-899).
			if notif == nil || !e.Info.IsFromMe {
				return true
			}

			if err := svc.queue(sessionID, e.Info.ID, notif); err != nil {
				svc.log.Error().Err(err).Str("session_id", sessionID).Msg("queueing a history sync notification failed; its answer is withheld")

				return false
			}

			svc.wake(sessionID)
		case *waevents.OfflineSyncCompleted:
			svc.start(sessionID)
		case *waevents.LoggedOut, *waevents.PairSuccess:
			// A connection event, answered by nothing (session.attach), so the result changes nothing.
			if err := svc.clear(sessionID); err != nil {
				svc.log.Error().Err(err).Str("session_id", sessionID).Msgf("taking the old account's history queue at %T failed", evt)
			}
		}

		return true
	}
}

// queue writes the notification to the session's queue, once: whatsmeow dispatches it again from its decrypted-event
// buffer after a crash before its answer (message.go:545-552).
func (svc *Service) queue(sessionID, messageID string, notif *waE2E.HistorySyncNotification) error {
	data, err := proto.Marshal(notif)
	if err != nil {
		return fmt.Errorf("queue a notification: %w", err)
	}

	// whatsmeow gives the handler no context. The write gives up after the busy_timeout, as the Events slice's does,
	// and the answer is withheld (ADR 0040).
	ctx, cancel := context.WithTimeout(context.Background(), svc.queueTimeout)
	defer cancel()

	if _, err := svc.w.Exec(ctx, `INSERT INTO history_blob (session_id, message_id, notification) VALUES (?, ?, ?)
		ON CONFLICT (session_id, message_id) DO NOTHING`, sessionID, messageID, data); err != nil {
		return fmt.Errorf("queue a notification: %w", err)
	}

	return nil
}

// clear takes the session's queue, in one short transaction.
func (svc *Service) clear(sessionID string) error {
	ctx, cancel := context.WithTimeout(context.Background(), svc.queueTimeout)
	defer cancel()

	if _, err := svc.w.Exec(ctx, "DELETE FROM history_blob WHERE session_id = ?", sessionID); err != nil {
		return fmt.Errorf("clear the queue: %w", err)
	}

	return nil
}

// wake wakes the session's worker, if it runs, without waiting. A session that has none is not caught up yet: its
// notification waits in the queue for the offline sync.
func (svc *Service) wake(sessionID string) {
	svc.mu.Lock()
	defer svc.mu.Unlock()

	if w, ok := svc.workers[sessionID]; ok {
		signal(w.wake)
	}
}

func signal(wake chan struct{}) {
	select {
	case wake <- struct{}{}:
	default:
	}
}

// start starts the session's worker, once its previous one ended; it runs while the session is online. A worker of
// this connection, or one still waiting for the session to come online, is only woken: whatsmeow dispatches
// OfflineSyncCompleted for each offline ib it gets (connectionevents.go:85-88).
func (svc *Service) start(sessionID string) {
	svc.mu.Lock()
	defer svc.mu.Unlock()

	if svc.ctx.Err() != nil {
		return
	}

	previous := svc.workers[sessionID]
	if previous != nil && !previous.ended && !closed(previous.online) {
		signal(previous.wake)

		return
	}

	w := &worker{wake: make(chan struct{}, 1), done: make(chan struct{})}
	svc.workers[sessionID] = w

	svc.running.Add(1)

	go func() {
		defer svc.running.Done()
		defer svc.forget(sessionID, w)

		if previous != nil {
			<-previous.done
		}

		svc.work(sessionID, w)
	}()
}

// closed reports whether c is closed; a nil channel is not.
func closed(c <-chan struct{}) bool {
	if c == nil {
		return false
	}

	select {
	case <-c:
		return true
	default:
		return false
	}
}

// forget removes the session's worker once it ended, unless a newer one took its place.
func (svc *Service) forget(sessionID string, w *worker) {
	svc.mu.Lock()
	w.ended = true
	if svc.workers[sessionID] == w {
		delete(svc.workers, sessionID)
	}
	svc.mu.Unlock()

	close(w.done)
}

// work delivers the session's queue while the session is online: at once, then each time it is woken, it takes the
// blobs in the order they arrived. A failure that is neither a download's nor offline is tried again after a backoff.
// It ends once the session leaves Connected and CaughtUp or is deleted (session.Online's channel), at Close, and when
// the session does not come online within upTimeout of the offline sync.
func (svc *Service) work(sessionID string, w *worker) {
	client, online, ok := svc.connect(sessionID, w)
	if !ok {
		return
	}

	ctx, cancel := context.WithCancel(svc.ctx)
	defer cancel()

	go func() {
		select {
		case <-online:
			cancel()
		case <-ctx.Done():
		}
	}()

	backoff := minBackoff

	for {
		err := svc.drain(ctx, sessionID, client, online)

		switch {
		case ctx.Err() != nil:
			return
		case errors.Is(err, errOffline):
			// The connection is going: the session leaves online, which ends the worker, or a wake tries again.
			svc.onOffline(sessionID)
		case err != nil:
			svc.log.Error().Err(err).Str("session_id", sessionID).Str("backoff", backoff.String()).Msg("delivering the history failed; it is tried again after the backoff")

			select {
			case <-svc.after(backoff):
			case <-ctx.Done():
				return
			}

			backoff = min(2*backoff, maxBackoff)

			continue
		default:
			backoff = minBackoff
			svc.onIdle(sessionID)
		}

		select {
		case <-ctx.Done():
			return
		case <-w.wake:
		}
	}
}

// connect is the session's client and its online channel once the session is online, waiting for that at most
// upTimeout, on the session's Up channel, never polling. A session that fails otherwise, or does not come up in time,
// gets no worker; the next offline sync starts one.
func (svc *Service) connect(sessionID string, w *worker) (Client, <-chan struct{}, bool) {
	// Asked for only once the session is not online: the clock's timer is not made for nothing.
	var expired <-chan time.Time

	for {
		c, online, err := svc.sessions.Online(sessionID)
		if err == nil {
			client, ok := c.(Client)
			if !ok {
				svc.log.Error().Str("session_id", sessionID).Msgf("its client %T downloads no history; nothing delivers it", c)

				return nil, nil, false
			}

			svc.mu.Lock()
			w.online = online
			svc.mu.Unlock()

			return client, online, true
		}

		if !errors.Is(err, server.ErrNotConnected) {
			return nil, nil, false
		}

		up, err := svc.sessions.Up(sessionID)
		if err != nil {
			return nil, nil, false
		}

		if expired == nil {
			expired = svc.after(upTimeout)
		}

		select {
		case <-up:
		case <-expired:
			// The session may have come up as the wait ran out: select picks either of two ready cases.
			if closed(up) {
				continue
			}

			if svc.giveUp(w) {
				svc.log.Debug().Str("session_id", sessionID).Msg("the session did not come online after its offline sync; its history waits for the next")

				return nil, nil, false
			}

			expired = svc.after(upTimeout)
		case <-svc.ctx.Done():
			return nil, nil, false
		}
	}
}

// giveUp ends a worker that waited upTimeout for its session, unless it was woken meanwhile: then it waits again.
func (svc *Service) giveUp(w *worker) bool {
	svc.mu.Lock()
	defer svc.mu.Unlock()

	select {
	case <-w.wake:
		return false
	default:
		w.ended = true

		return true
	}
}

// errOffline is a download that failed because the session is not connected: the next connection's worker tries
// again, without counting it.
var errOffline = errors.New("the session went offline")

// queued is one blob of the queue. notif is nil when the queued notification does not parse.
type queued struct {
	seq                 int64
	messageID           string
	notif               *waE2E.HistorySyncNotification
	messagesDone, tries int
	recorded            bool
}

// drain delivers the session's queue until it is empty.
func (svc *Service) drain(ctx context.Context, sessionID string, client Client, online <-chan struct{}) error {
	for {
		next, ok, err := svc.next(ctx, sessionID)
		if err != nil || !ok {
			return err
		}

		err = svc.deliverSafely(ctx, sessionID, client, online, &next)

		switch {
		case errors.Is(err, errGone):
			// A logout, a pairing or the session's delete took the queue meanwhile: nothing of the blob is recorded on.
			svc.log.Debug().Str("session_id", sessionID).Msg("a history sync blob left the queue while it was delivered; it is not recorded further")
		case err != nil:
			return err
		}
	}
}

// errGone is a queue update that found the blob's row gone: the transaction is rolled back.
var errGone = errors.New("the blob left the queue")

// next is the session's oldest queued blob.
func (svc *Service) next(ctx context.Context, sessionID string) (queued, bool, error) {
	var (
		q        queued
		data     []byte
		recorded int
	)

	err := svc.db.QueryRowContext(ctx, `SELECT seq, message_id, notification, messages_done, recorded, attempts FROM history_blob
		WHERE session_id = ? ORDER BY seq LIMIT 1`, sessionID).Scan(&q.seq, &q.messageID, &data, &q.messagesDone, &recorded, &q.tries)
	if errors.Is(err, sql.ErrNoRows) {
		return q, false, nil
	}

	if err != nil {
		return q, false, fmt.Errorf("read the queue: %w", err)
	}

	q.recorded = recorded == 1

	notif := &waE2E.HistorySyncNotification{}
	if proto.Unmarshal(data, notif) == nil {
		q.notif = notif
	}

	return q, true, nil
}

// deliverSafely is deliver, and a panic in it reported as the blob being invalid: the worker goes on with the next
// blob, and the sidecar keeps running. The panic's value is not logged, only its type: it may carry a JID.
func (svc *Service) deliverSafely(ctx context.Context, sessionID string, client Client, online <-chan struct{}, q *queued) (err error) {
	defer func() {
		if r := recover(); r != nil {
			svc.log.Error().Str("session_id", sessionID).Str("panic", fmt.Sprintf("%T", r)).Str("stack", string(debug.Stack())).
				Msg("delivering a history sync blob panicked; it is reported invalid and the next goes on")

			err = svc.drop(ctx, sessionID, q, whatsmeowv1.HistorySyncFailure_HISTORY_SYNC_FAILURE_INVALID)
		}
	}()

	err = svc.deliver(ctx, sessionID, client, online, q)
	// An event that does not marshal never will: reported, not tried again for ever.
	if errors.Is(err, proto.Error) {
		svc.log.Error().Str("session_id", sessionID).Msg("a history event of a blob cannot be marshalled; the blob is reported invalid and the next goes on")

		return svc.drop(ctx, sessionID, q, whatsmeowv1.HistorySyncFailure_HISTORY_SYNC_FAILURE_INVALID)
	}

	return err
}

// deliver delivers one blob: its batches from the first message not recorded yet, its progress event, then the delete
// from WhatsApp's server. A blob that cannot be downloaded any more is reported and leaves the queue; one whose
// download failed otherwise is tried again after retryDelay, attempts times in all.
func (svc *Service) deliver(ctx context.Context, sessionID string, client Client, online <-chan struct{}, q *queued) error {
	switch {
	case q.notif == nil:
		svc.log.Warn().Str("session_id", sessionID).Msg("a queued history sync notification does not parse; it is reported invalid")

		return svc.drop(ctx, sessionID, q, whatsmeowv1.HistorySyncFailure_HISTORY_SYNC_FAILURE_INVALID)
	case q.recorded:
		return svc.finish(ctx, sessionID, client, q)
	case blobless(q.notif):
		// Nothing to download or delete: the notification is all there is (download.go:222-223).
		progress := &whatsmeowv1.HistorySyncProgressEvent{
			SyncType: syncType(int32(q.notif.GetSyncType())), ChunkOrder: q.notif.GetChunkOrder(), Progress: q.notif.GetProgress(),
		}

		return svc.record(ctx, sessionID, prefix+q.messageID+"\x00p", progressEvent(progress), "DELETE FROM history_blob WHERE seq = ?", q.seq)
	}

	d, err := svc.fetch(ctx, sessionID, client, q)
	if err != nil {
		return svc.failed(ctx, sessionID, client, online, q, err)
	}

	for _, b := range d.batches {
		key := prefix + q.messageID + "\x00" + strconv.Itoa(b.first)
		event := &whatsmeowv1.StreamEventsResponse{Payload: &whatsmeowv1.StreamEventsResponse_HistoryMessages{HistoryMessages: b.event}}

		if err := svc.record(ctx, sessionID, key, event, "UPDATE history_blob SET messages_done = ? WHERE seq = ?", b.end, q.seq); err != nil {
			return err
		}
	}

	if err := svc.record(ctx, sessionID, prefix+q.messageID+"\x00p", progressEvent(d.progress), "UPDATE history_blob SET recorded = 1 WHERE seq = ?", q.seq); err != nil {
		return err
	}

	q.recorded = true

	return svc.finish(ctx, sessionID, client, q)
}

// blobless is a notification that says there is no history, and carries no blob to download.
func blobless(notif *waE2E.HistorySyncNotification) bool {
	none := notif.GetSyncType() == waE2E.HistorySyncType_NO_HISTORY || notif.GetSyncType() == waE2E.HistorySyncType_MESSAGE_ACCESS_STATUS

	return none && notif.GetDirectPath() == "" && notif.InitialHistBootstrapInlinePayload == nil
}

// drop reports the blob given up on with failure, and takes it from the queue in the same transaction; one whose
// progress event is recorded already only leaves the queue.
func (svc *Service) drop(ctx context.Context, sessionID string, q *queued, failure whatsmeowv1.HistorySyncFailure) error {
	if q.recorded {
		if _, err := svc.w.Exec(ctx, "DELETE FROM history_blob WHERE seq = ?", q.seq); err != nil {
			return fmt.Errorf("take a blob from the queue: %w", err)
		}

		return nil
	}

	progress := &whatsmeowv1.HistorySyncProgressEvent{
		SyncType: whatsmeowv1.HistorySyncType_HISTORY_SYNC_TYPE_UNSPECIFIED, ChunkOrder: q.notif.GetChunkOrder(), Progress: q.notif.GetProgress(), Failure: failure,
	}
	if q.notif != nil {
		progress.SyncType = syncType(int32(q.notif.GetSyncType()))
	}

	return svc.record(ctx, sessionID, prefix+q.messageID+"\x00p", progressEvent(progress), "DELETE FROM history_blob WHERE seq = ?", q.seq)
}

// delivery is a blob's batches from its first message not done, and its progress event.
type delivery struct {
	batches  []batched
	progress *whatsmeowv1.HistorySyncProgressEvent
}

// fetch downloads the blob, parses it and cuts its batches, one blob at a time across the sessions, so at most one
// blob is held whole, compressed, decompressed and parsed, at once; what a session keeps while it waits for its
// consumers is its batches only. synchronousStorage, so what whatsmeow stores from the blob (message secrets, LID
// mappings, push names) is stored before its messages are delivered. The download is bounded by downloadTimeout.
func (svc *Service) fetch(ctx context.Context, sessionID string, client Client, q *queued) (delivery, error) {
	select {
	case svc.download <- struct{}{}:
	default:
		svc.onQueue(sessionID)

		select {
		case svc.download <- struct{}{}:
		case <-ctx.Done():
			return delivery{}, fmt.Errorf("wait for another session's download: %w", ctx.Err())
		}
	}

	defer func() { <-svc.download }()

	downloading, cancel := context.WithTimeout(ctx, svc.downloadTimeout)
	blob, err := client.DownloadHistorySync(downloading, q.notif, true)

	cancel()

	if err != nil {
		return delivery{}, fmt.Errorf("download a history sync blob: %w", err)
	}

	// The device names the account in the messages' targets, as in a live message's.
	account, err := svc.sessions.Device(sessionID)
	if err != nil {
		return delivery{}, fmt.Errorf("the account's device: %w", err)
	}

	st := syncType(int32(blob.GetSyncType()))
	messages, skipped := svc.parse(client, account, blob)
	batches, delivered, oversized := batch(st, messages, q.messagesDone)

	return delivery{
		batches: batches,
		progress: &whatsmeowv1.HistorySyncProgressEvent{
			SyncType: st, ChunkOrder: blob.GetChunkOrder(), Progress: blob.GetProgress(),
			MessageCount: count(delivered), SkippedCount: count(skipped + oversized),
		},
	}, nil
}

// failed handles a download that failed: offline is not counted and stops the worker until it is woken or the session
// leaves online, a blob that cannot be downloaded any more is reported, and anything else is counted and tried again.
// A failure while the session leaves online, or after the socket is gone, is offline: the socket goes before whatsmeow
// dispatches Disconnected.
func (svc *Service) failed(ctx context.Context, sessionID string, client Client, online <-chan struct{}, q *queued, err error) error {
	failure := classify(err)

	switch {
	case ctx.Err() != nil || errors.Is(err, whatsmeow.ErrNotConnected) || closed(online) || !client.IsConnected():
		return errOffline
	case failure == whatsmeowv1.HistorySyncFailure_HISTORY_SYNC_FAILURE_UNSPECIFIED && q.tries+1 < attempts:
		if _, err := svc.w.Exec(ctx, "UPDATE history_blob SET attempts = attempts + 1 WHERE seq = ?", q.seq); err != nil {
			return fmt.Errorf("count a failed download: %w", err)
		}

		svc.log.Warn().Str("session_id", sessionID).Int("attempt", q.tries+1).Msg("downloading a history sync blob failed; it is tried again in a minute")

		select {
		case <-svc.after(retryDelay):
			return nil
		case <-ctx.Done():
			return fmt.Errorf("wait to try again: %w", ctx.Err())
		}
	case failure == whatsmeowv1.HistorySyncFailure_HISTORY_SYNC_FAILURE_UNSPECIFIED:
		failure = whatsmeowv1.HistorySyncFailure_HISTORY_SYNC_FAILURE_UNAVAILABLE
	}

	svc.log.Warn().Str("session_id", sessionID).Str("failure", failure.String()).Msg("a history sync blob could not be downloaded; it is reported and the next goes on")

	return svc.drop(ctx, sessionID, q, failure)
}

// classify is why a blob can no longer be downloaded, or unspecified for a failure that may pass. It reads whatsmeow's
// sentinels only, never its text, which carries the media URL with its signed query.
func classify(err error) whatsmeowv1.HistorySyncFailure {
	switch {
	case errors.Is(err, whatsmeow.ErrMediaDownloadFailedWith403), errors.Is(err, whatsmeow.ErrMediaDownloadFailedWith404),
		errors.Is(err, whatsmeow.ErrMediaDownloadFailedWith410):
		return whatsmeowv1.HistorySyncFailure_HISTORY_SYNC_FAILURE_EXPIRED
	case errors.Is(err, whatsmeow.ErrNoURLPresent), errors.Is(err, whatsmeow.ErrTooShortFile), errors.Is(err, whatsmeow.ErrInvalidMediaHMAC),
		errors.Is(err, whatsmeow.ErrInvalidMediaEncSHA256), errors.Is(err, whatsmeow.ErrInvalidMediaSHA256), corrupt(err):
		return whatsmeowv1.HistorySyncFailure_HISTORY_SYNC_FAILURE_INVALID
	default:
		return whatsmeowv1.HistorySyncFailure_HISTORY_SYNC_FAILURE_UNSPECIFIED
	}
}

// corrupt is a blob that does not decompress or parse (DownloadHistorySync, message.go:791-798).
func corrupt(err error) bool {
	var flateErr flate.CorruptInputError

	return errors.Is(err, zlib.ErrHeader) || errors.Is(err, zlib.ErrChecksum) || errors.Is(err, zlib.ErrDictionary) ||
		errors.As(err, &flateErr) || errors.Is(err, proto.Error)
}

// record waits for room in the session's window, then records event under key, with the queue's update in the same
// transaction. The update must change the blob's one row: when the row is gone, the transaction is rolled back and
// record fails with errGone.
func (svc *Service) record(ctx context.Context, sessionID, key string, event *whatsmeowv1.StreamEventsResponse, update string, args ...any) error {
	if err := svc.room(ctx, sessionID); err != nil {
		return err
	}

	differs, err := svc.box.Record(ctx, sessionID, key, event, func(ctx context.Context, tx *sql.Tx) error {
		result, err := tx.ExecContext(ctx, update, args...)
		if err != nil {
			return fmt.Errorf("update the queue: %w", err)
		}

		if n, err := result.RowsAffected(); err != nil || n != 1 {
			return errors.Join(errGone, err)
		}

		return nil
	})
	if err != nil {
		return fmt.Errorf("record history: %w", err)
	}

	if differs {
		svc.log.Warn().Str("session_id", sessionID).Msg("a history event repeats a recorded one's key with other content; it is recorded beside it")
	}

	return nil
}

// room waits until fewer than window of the session's history events are kept in the outbox.
func (svc *Service) room(ctx context.Context, sessionID string) error {
	for {
		acked := svc.box.Acked(sessionID)

		pending, err := svc.box.Pending(ctx, sessionID, prefix)
		if err != nil {
			return fmt.Errorf("wait for the consumers: %w", err)
		}

		if pending < window {
			return nil
		}

		svc.onWait(sessionID)

		select {
		case <-acked:
		case <-ctx.Done():
			return fmt.Errorf("wait for the consumers: %w", ctx.Err())
		}
	}
}

// finish deletes the blob from WhatsApp's server, now that its messages are in the outbox, then from the queue. A
// delete that fails is logged by its status only and not tried again (ADR 0035); an inline blob has nothing to delete
// (upload.go:309-311).
func (svc *Service) finish(ctx context.Context, sessionID string, client Client, q *queued) error {
	err := client.DeleteMedia(ctx, whatsmeow.MediaHistory, q.notif.GetDirectPath(), q.notif.GetFileEncSHA256(), q.notif.GetEncHandle())
	if err != nil {
		if ctx.Err() != nil {
			return fmt.Errorf("delete a history sync blob: %w", ctx.Err())
		}

		// Not the error: its text carries the request URL, whose query has the media connection's auth (upload.go:331-353).
		svc.log.Warn().Str("session_id", sessionID).Msg("deleting a delivered history sync blob from WhatsApp's server failed; it is not tried again")
	}

	if _, err := svc.w.Exec(ctx, "DELETE FROM history_blob WHERE seq = ?", q.seq); err != nil {
		return fmt.Errorf("take a delivered blob from the queue: %w", err)
	}

	return nil
}

// parse is each message of the blob's conversations, in order, as whatsmeow parses a live one (ParseWebMessage), mapped
// as the Events slice maps a live one, a poll followed by its votes: an entry per message, nil for one that does not
// parse or whose parse panics, and how many more are skipped: the messages of a conversation whose JID does not
// parse, which hold their places as nil, and every status update, which history never delivers (ARCHITECTURE §1).
// Stub messages, without content, are delivered as they are.
//
// A vote message whose vote is delivered decrypted, after its poll, with the same id in the same chat, is an empty
// entry: an application dedupes on the message id (HistoryMessage), so delivering both could keep the undecryptable
// one and drop the vote. Only a delivered vote counts: one the mapping dropped, or whose poll is skipped, leaves its
// encrypted copy delivered. That a blob carries both is [unknown] (ADR 0047).
func (svc *Service) parse(client Client, account *store.Device, blob *waHistorySync.HistorySync) ([][]*whatsmeowv1.MessageEvent, int) {
	var (
		messages [][]*whatsmeowv1.MessageEvent
		// votes is, for each entry, the chat and id of the vote message it is, empty for any other.
		votes []string
	)

	decrypted := map[string]bool{}

	for _, conv := range blob.GetConversations() {
		chat, err := types.ParseJID(conv.GetID())
		if err != nil || chat.IsEmpty() {
			messages = append(messages, make([][]*whatsmeowv1.MessageEvent, len(conv.GetMessages()))...)
			votes = append(votes, make([]string, len(conv.GetMessages()))...)

			continue
		}

		for _, m := range conv.GetMessages() {
			entry, vote := svc.parseOne(client, account, chat, m.GetMessage())
			messages = append(messages, entry)
			votes = append(votes, "")

			if vote {
				votes[len(votes)-1] = conv.GetID() + "\x00" + entry[0].GetInfo().GetId()
			}

			if entry == nil || oversized(entry) {
				continue
			}

			for _, e := range entry {
				// A vote a history poll carries decrypted: no message (events.HistoryMessage).
				if e.GetPollVote() != nil && e.GetMessage() == nil {
					decrypted[conv.GetID()+"\x00"+e.GetInfo().GetId()] = true
				}
			}
		}
	}

	for i, vote := range votes {
		if vote != "" && decrypted[vote] {
			messages[i] = []*whatsmeowv1.MessageEvent{}
		}
	}

	return messages, len(blob.GetStatusV3Messages())
}

// parseOne is one message parsed and mapped, or nil when it does not parse or its parse panics: one bad message is
// skipped, never its blob; and whether it is a vote message. The deliver's own recover stays the guard of everything
// else.
func (svc *Service) parseOne(client Client, account *store.Device, chat types.JID, m *waWeb.WebMessageInfo) (events []*whatsmeowv1.MessageEvent, vote bool) {
	defer func() {
		if recover() != nil {
			events, vote = nil, false
		}
	}()

	evt, err := client.ParseWebMessage(chat, m)
	if err != nil {
		return nil, false
	}

	events = svc.toEvent(evt, account)

	return events, evt.Message.GetPollUpdateMessage() != nil && len(events) > 0
}

// cost is what an entry takes in a batch: each message as a repeated field, its tag and length, then its bytes.
func cost(entry []*whatsmeowv1.MessageEvent) int {
	n := 0
	for _, m := range entry {
		n += 1 + varintSize(proto.Size(m)) + proto.Size(m)
	}

	return n
}

// oversized is an entry that alone takes more than maxEventBytes as an event, which batch skips.
func oversized(entry []*whatsmeowv1.MessageEvent) bool {
	return eventSize(cost(entry)+2) > maxEventBytes
}

// batched is one batch, and the blob indexes of its first message and of the one after its last.
type batched struct {
	first, end int
	event      *whatsmeowv1.HistoryMessagesEvent
}

// batch cuts the messages from index from into batches, in order: at most maxBatchMessages each, and at most
// maxBatchBytes as an event unless one message alone takes more. A message is an entry of parse, with a poll's votes,
// which a batch never splits, so a blob processed again from a message's index delivers its votes with it. An entry
// that alone takes more than maxEventBytes is skipped, as one that did not parse is; an empty one, an encrypted vote
// delivered decrypted with its poll, counts as delivered. delivered and skipped count the blob's messages, never a
// poll's votes, and the whole blob, before from too, so a blob processed again from its middle reports what it
// reported whole.
func batch(syncType whatsmeowv1.HistorySyncType, messages [][]*whatsmeowv1.MessageEvent, from int) (batches []batched, delivered, skipped int) {
	// current is the index of the batch being filled, -1 for none.
	current, size := -1, 0

	for i, entry := range messages {
		if entry == nil {
			skipped++

			continue
		}

		c := cost(entry)
		if eventSize(c+2) > maxEventBytes {
			skipped++

			continue
		}

		delivered++

		if i < from || len(entry) == 0 {
			continue
		}

		if current >= 0 && (len(batches[current].event.GetMessages())+len(entry) > maxBatchMessages || eventSize(size+c) > maxBatchBytes) {
			current = -1
		}

		if current < 0 {
			batches = append(batches, batched{first: i, event: &whatsmeowv1.HistoryMessagesEvent{SyncType: syncType}})
			current = len(batches) - 1
			size = 2 // the sync type
		}

		batches[current].event.Messages = append(batches[current].event.Messages, entry...)
		batches[current].end = i + 1
		size += c
	}

	return batches, delivered, skipped
}

// eventSize is the most a StreamEventsResponse whose batch is batchSize bytes takes: the batch's tag (field 18, two
// bytes) and length, and the largest seq.
func eventSize(batchSize int) int { return 2 + varintSize(batchSize) + batchSize + 11 }

func varintSize(n int) int {
	size := 1
	for n >= 0x80 {
		n >>= 7
		size++
	}

	return size
}

func progressEvent(progress *whatsmeowv1.HistorySyncProgressEvent) *whatsmeowv1.StreamEventsResponse {
	return &whatsmeowv1.StreamEventsResponse{Payload: &whatsmeowv1.StreamEventsResponse_HistoryProgress{HistoryProgress: progress}}
}

// syncType is whatsmeow's sync type number as the contract's: waE2E's, which a notification carries, and whose first
// seven waHistorySync's, which a blob carries, agree with.
func syncType(n int32) whatsmeowv1.HistorySyncType {
	switch waE2E.HistorySyncType(n) {
	case waE2E.HistorySyncType_INITIAL_BOOTSTRAP:
		return whatsmeowv1.HistorySyncType_HISTORY_SYNC_TYPE_INITIAL_BOOTSTRAP
	case waE2E.HistorySyncType_INITIAL_STATUS_V3:
		return whatsmeowv1.HistorySyncType_HISTORY_SYNC_TYPE_INITIAL_STATUS_V3
	case waE2E.HistorySyncType_FULL:
		return whatsmeowv1.HistorySyncType_HISTORY_SYNC_TYPE_FULL
	case waE2E.HistorySyncType_RECENT:
		return whatsmeowv1.HistorySyncType_HISTORY_SYNC_TYPE_RECENT
	case waE2E.HistorySyncType_PUSH_NAME:
		return whatsmeowv1.HistorySyncType_HISTORY_SYNC_TYPE_PUSH_NAME
	case waE2E.HistorySyncType_NON_BLOCKING_DATA:
		return whatsmeowv1.HistorySyncType_HISTORY_SYNC_TYPE_NON_BLOCKING_DATA
	case waE2E.HistorySyncType_ON_DEMAND:
		return whatsmeowv1.HistorySyncType_HISTORY_SYNC_TYPE_ON_DEMAND
	case waE2E.HistorySyncType_NO_HISTORY:
		return whatsmeowv1.HistorySyncType_HISTORY_SYNC_TYPE_NO_HISTORY
	case waE2E.HistorySyncType_MESSAGE_ACCESS_STATUS:
		return whatsmeowv1.HistorySyncType_HISTORY_SYNC_TYPE_MESSAGE_ACCESS_STATUS
	default:
		return whatsmeowv1.HistorySyncType_HISTORY_SYNC_TYPE_UNSPECIFIED
	}
}

// count is n as a uint32; a blob never holds 2^32 messages.
func count(n int) uint32 { return uint32(min(n, math.MaxUint32)) } //nolint:gosec // G115: bounded above

// RequestHistory asks the phone for older messages of a chat (history.proto).
func (svc *Service) RequestHistory(ctx context.Context, req *whatsmeowv1.RequestHistoryRequest) (*whatsmeowv1.RequestHistoryResponse, error) {
	last, count, err := validate(req)
	if err != nil {
		return nil, err
	}

	c, _, err := svc.sessions.Online(req.GetSessionId())
	if err != nil {
		return nil, fmt.Errorf("request history: %w", err)
	}

	client, ok := c.(Client)
	if !ok {
		return nil, fmt.Errorf("session %q: its client %T requests no history", req.GetSessionId(), c)
	}

	device, err := svc.sessions.Device(req.GetSessionId())
	if err != nil {
		return nil, fmt.Errorf("request history: %w", err)
	}

	// A peer message to the account's own phone, as mautrix-whatsapp sends it (pkg/connector/backfill.go:762-790).
	if _, err := client.SendMessage(ctx, device.GetJID().ToNonAD(), client.BuildHistorySyncRequest(last, count), whatsmeow.SendRequestExtra{ID: newID(), Peer: true}); err != nil {
		return nil, classifySend(err)
	}

	return &whatsmeowv1.RequestHistoryResponse{}, nil
}

// validate checks the request and returns the oldest message as whatsmeow's builder takes it (send.go:588-605).
func validate(req *whatsmeowv1.RequestHistoryRequest) (*types.MessageInfo, int, error) {
	if err := server.CheckID("session_id", req.GetSessionId()); err != nil {
		return nil, 0, fmt.Errorf("session id: %w", err)
	}

	chat, err := types.ParseJID(req.GetChat())
	if err != nil || chat.User == "" || chat.Device != 0 ||
		(chat.Server != types.DefaultUserServer && chat.Server != types.HiddenUserServer && chat.Server != types.GroupServer) {
		// Not the JID itself: it is someone's personal data, and the error reaches the application's logs.
		return nil, 0, invalid("chat", "is not a user's or a group's JID")
	}

	switch {
	case req.GetOldestMessageId() == "":
		return nil, 0, invalid("oldest_message_id", "is empty")
	case req.GetOldestMessageTime() == nil:
		return nil, 0, invalid("oldest_message_time", "is unset")
	case req.GetCount() == 0 || req.GetCount() > math.MaxInt32:
		return nil, 0, invalid("count", "%d is not from 1 to %d", req.GetCount(), math.MaxInt32)
	}

	last := &types.MessageInfo{
		MessageSource: types.MessageSource{Chat: chat, IsFromMe: req.GetOldestFromMe(), IsGroup: chat.Server == types.GroupServer},
		ID:            req.GetOldestMessageId(),
		Timestamp:     req.GetOldestMessageTime().AsTime(),
	}

	return last, int(req.GetCount()), nil
}

func invalid(field, format string, args ...any) error {
	return fmt.Errorf("invalid request: %w", server.InvalidArgument(field, format, args...))
}

// classifySend is a failed request's reason, as the Messaging slice classifies a send (messaging/send.go classify,
// ADR 0025): ErrNotLoggedIn, an IQ error and a wrapped ErrNotConnected come before the write; 429 is RATE_LIMITED,
// and WhatsApp's other answers, to an IQ or to the node, are WHATSAPP_ERROR with its code. Only what may have been
// written with no answer back is OUTCOME_UNKNOWN. It keeps whatsmeow's sentinel or a bare copy, never its text, which
// can name a JID.
func classifySend(err error) error {
	var iq *whatsmeow.IQError

	switch {
	case errors.Is(err, whatsmeow.ErrNotLoggedIn):
		return fmt.Errorf("request history: %w: %w", server.ErrLoggedOut, whatsmeow.ErrNotLoggedIn)
	case errors.As(err, &iq) && iq.Code == 429:
		// ErrIQRateOverLimit's Is also compares the text (errors.go:198, 230-241), so match the code.
		return fmt.Errorf("request history: %w: %w", server.ErrRateLimited, &whatsmeow.IQError{Code: iq.Code, Text: iq.Text})
	case errors.As(err, &iq):
		return whatsAppError(iq.Code, &whatsmeow.IQError{Code: iq.Code, Text: iq.Text})
	case errors.Is(err, whatsmeow.ErrNotConnected) && err != whatsmeow.ErrNotConnected: //nolint:errorlint // the unwrapped one comes after the write (request.go:207-209)
		return fmt.Errorf("request history: %w: %w", server.ErrNotConnected, whatsmeow.ErrNotConnected)
	case errors.Is(err, whatsmeow.ErrServerReturnedError) && serverErrorCode(err) != 0:
		return whatsAppError(serverErrorCode(err), whatsmeow.ErrServerReturnedError)
	default:
		return fmt.Errorf("request history: %w: %w", server.ErrOutcomeUnknown, cause(err))
	}
}

// whatsAppError is WHATSAPP_ERROR with WhatsApp's code, and the bare cause.
func whatsAppError(code int, cause error) error {
	return fmt.Errorf("request history: WhatsApp answered with error %d: %w: %w", code,
		server.WithMetadata(server.ErrWhatsAppError, "code", strconv.Itoa(code)), cause)
}

// serverErrorCode is the code of whatsmeow's ErrServerReturnedError, which carries it only in its text,
// "server returned error <code>" (send.go:468-470); 0 when there is none.
func serverErrorCode(err error) int {
	code, err := strconv.Atoi(strings.TrimPrefix(err.Error(), whatsmeow.ErrServerReturnedError.Error()+" "))
	if err != nil {
		return 0
	}

	return code
}

// cause is the error at the end of err's chain, without the text whatsmeow wrapped around it.
func cause(err error) error {
	for {
		next := errors.Unwrap(err)
		if next == nil {
			return err
		}

		err = next
	}
}

// newID is a message id as whatsmeow makes one: 3EB0 and 18 uppercase hex digits.
func newID() string {
	b := make([]byte, 9)
	_, _ = rand.Read(b)

	return "3EB0" + strings.ToUpper(hex.EncodeToString(b))
}
