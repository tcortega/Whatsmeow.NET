// Package history is the History sync slice (ARCHITECTURE §7.7, ADR 0035): the account's history, which WhatsApp sends
// after pairing in blobs, and more on request, delivered as history events that never delay live traffic.
//
// whatsmeow runs with ManualHistorySyncDownload (§7.2), so it deletes nothing itself. The slice's event handler queues
// each notification in history_blob before whatsmeow answers it, and one worker per session delivers the queue once
// the session's offline sync completed: it downloads a blob, parses its messages as whatsmeow parses a live one, and
// records them in batches small enough for one gRPC message, then a progress event, and only then deletes the blob
// from WhatsApp's server. A blob half done when the sidecar stops is processed again from its next batch.
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
)

const (
	// window is how many of a session's history events the worker keeps in the outbox before every consumer acked
	// them: a live message is recorded behind at most this many. With no consumer, or one that stopped acking, the
	// history waits there [observed: TestTheWorkerKeepsAtMostAWindowOfHistoryUntilTheConsumersAck].
	window = 4
	// maxBatchMessages and maxBatchBytes bound a batch: at most this many messages, and at most this many bytes as one
	// StreamEventsResponse, unless it is one message alone [inferred: a trade between the window's memory in .NET and
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
	// prefix starts the outbox key of every history event: a blob's batch is prefix, its notification's id, 0, its
	// number; its progress event is prefix, the id, 0, "p".
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
}

var _ Client = (*whatsmeow.Client)(nil)

// Sessions is what the slice needs of the Sessions slice: a session's client while it is online, and its device for
// the account's own JID.
type Sessions interface {
	Online(id string) (session.Client, <-chan struct{}, error)
	Device(id string) (*store.Device, error)
}

// MessageEvent maps a whatsmeow message to the contract's MessageEvent: the Events slice's mapping of a live one
// (events.MessageEvent), so a history message carries what a live one does.
type MessageEvent func(*waevents.Message) *whatsmeowv1.MessageEvent

// Service implements whatsmeowv1.HistoryServiceServer, the whatsmeow event handler that queues notifications, and the
// sessions' workers.
type Service struct {
	whatsmeowv1.UnimplementedHistoryServiceServer

	db       *sql.DB
	box      *outbox.Outbox
	log      zerolog.Logger
	toEvent  MessageEvent
	sessions Sessions
	// download lets one download and parse run at a time across the sessions: a blob is held whole in memory
	// (DownloadHistorySync, message.go:783-825).
	download chan struct{}
	// retryAfter is the clock of the wait between attempts.
	retryAfter func(time.Duration) <-chan time.Time
	// onWait, onQueue and onIdle tell a test that a worker waits for its consumers, waits for another session's
	// download, or found nothing left to do.
	onWait, onQueue, onIdle func(sessionID string)

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
}

// New returns the slice on the database and the outbox. toEvent is the Events slice's message mapping. Use gives it
// the sessions before any client connects.
func New(db *sql.DB, box *outbox.Outbox, toEvent MessageEvent, log zerolog.Logger) *Service {
	ctx, cancel := context.WithCancel(context.Background())

	return &Service{
		db: db, box: box, log: log, toEvent: toEvent, download: make(chan struct{}, 1), retryAfter: time.After,
		onWait: func(string) {}, onQueue: func(string) {}, onIdle: func(string) {},
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
// its own recover (client.go:999-1013). whatsmeow sends the hist_sync receipt on its own goroutine before any handler
// runs, whatever the handler returns (message.go:899-906, 1172-1178). OfflineSyncCompleted starts the session's worker.
// It never downloads, and never waits for .NET, the network or media.
func (svc *Service) Handler(sessionID string) whatsmeow.EventHandlerWithSuccessStatus {
	return func(evt any) (ok bool) {
		defer func() {
			if r := recover(); r != nil {
				svc.log.Error().Str("session_id", sessionID).Interface("panic", r).Msgf("queueing a %T panicked; its answer is withheld", evt)

				ok = false
			}
		}()

		switch e := evt.(type) {
		case *waevents.Message:
			notif := e.Message.GetProtocolMessage().GetHistorySyncNotification()
			// whatsmeow acts only on the account's own (message.go:888-890).
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

	// No caller's context: whatsmeow gives the handler none. SQLite's busy_timeout bounds the write.
	if _, err := svc.db.ExecContext(context.Background(), `INSERT INTO history_blob (session_id, message_id, notification) VALUES (?, ?, ?)
		ON CONFLICT (session_id, message_id) DO NOTHING`, sessionID, messageID, data); err != nil {
		return fmt.Errorf("queue a notification: %w", err)
	}

	return nil
}

// wake wakes the session's worker, if it runs, without waiting. A session that has none is not caught up yet: its
// notification waits in the queue for the offline sync.
func (svc *Service) wake(sessionID string) {
	svc.mu.Lock()
	defer svc.mu.Unlock()

	if w, ok := svc.workers[sessionID]; ok {
		select {
		case w.wake <- struct{}{}:
		default:
		}
	}
}

// start starts the session's worker, once its previous one ended; it runs while the session is online.
func (svc *Service) start(sessionID string) {
	svc.mu.Lock()
	defer svc.mu.Unlock()

	if svc.ctx.Err() != nil {
		return
	}

	previous := svc.workers[sessionID]
	w := &worker{wake: make(chan struct{}, 1), done: make(chan struct{})}
	w.wake <- struct{}{}
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

// forget removes the session's worker once it ended, unless a newer one took its place.
func (svc *Service) forget(sessionID string, w *worker) {
	svc.mu.Lock()
	if svc.workers[sessionID] == w {
		delete(svc.workers, sessionID)
	}
	svc.mu.Unlock()

	close(w.done)
}

// work delivers the session's queue while the session is online: woken by a notification, it takes the blobs in the
// order they arrived. It ends once the session leaves Connected and CaughtUp or is deleted (session.Online's
// channel), at Close, and when a download fails because the session went offline.
func (svc *Service) work(sessionID string, w *worker) {
	c, online, err := svc.sessions.Online(sessionID)
	if err != nil {
		return
	}

	client, ok := c.(Client)
	if !ok {
		svc.log.Error().Str("session_id", sessionID).Msgf("its client %T downloads no history; nothing delivers it", c)

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

	for {
		select {
		case <-ctx.Done():
			return
		case <-w.wake:
		}

		if err := svc.drain(ctx, sessionID, client); err != nil {
			if ctx.Err() == nil && !errors.Is(err, errOffline) {
				svc.log.Error().Err(err).Str("session_id", sessionID).Msg("delivering the history failed; it goes on at the next offline sync")
			}

			return
		}

		svc.onIdle(sessionID)
	}
}

// errOffline is a download that failed because the session is not connected: the next connection's worker tries
// again, without counting it.
var errOffline = errors.New("the session went offline")

// queued is one blob of the queue.
type queued struct {
	seq                int64
	messageID          string
	notif              *waE2E.HistorySyncNotification
	batchesDone, tries int
	recorded           bool
}

// drain delivers the session's queue until it is empty.
func (svc *Service) drain(ctx context.Context, sessionID string, client Client) error {
	for {
		next, ok, err := svc.next(ctx, sessionID)
		if err != nil || !ok {
			return err
		}

		if err := svc.deliver(ctx, sessionID, client, next); err != nil {
			return err
		}
	}
}

// next is the session's oldest queued blob.
func (svc *Service) next(ctx context.Context, sessionID string) (queued, bool, error) {
	var (
		q        queued
		data     []byte
		recorded int
	)

	err := svc.db.QueryRowContext(ctx, `SELECT seq, message_id, notification, batches_done, recorded, attempts FROM history_blob
		WHERE session_id = ? ORDER BY seq LIMIT 1`, sessionID).Scan(&q.seq, &q.messageID, &data, &q.batchesDone, &recorded, &q.tries)
	if errors.Is(err, sql.ErrNoRows) {
		return q, false, nil
	}

	if err != nil {
		return q, false, fmt.Errorf("read the queue: %w", err)
	}

	q.recorded = recorded == 1
	q.notif = &waE2E.HistorySyncNotification{}

	if err := proto.Unmarshal(data, q.notif); err != nil {
		return q, false, fmt.Errorf("read a queued notification: %w", err)
	}

	return q, true, nil
}

// deliver delivers one blob: its batches from the first not recorded yet, its progress event, then the delete from
// WhatsApp's server. A blob that cannot be downloaded any more is reported and leaves the queue; one whose download
// failed otherwise is tried again after retryDelay, attempts times in all.
func (svc *Service) deliver(ctx context.Context, sessionID string, client Client, q queued) error {
	if q.recorded {
		return svc.finish(ctx, sessionID, client, q)
	}

	blob, err := svc.fetch(ctx, sessionID, client, q.notif)
	if err != nil {
		return svc.failed(ctx, sessionID, q, err)
	}

	syncType := syncType(int32(blob.GetSyncType()))
	messages, skipped := svc.parse(client, blob)
	batches, oversized := batch(syncType, messages)
	skipped += oversized

	for n := q.batchesDone; n < len(batches); n++ {
		key := fmt.Sprintf("%s%s\x00%d", prefix, q.messageID, n)
		event := &whatsmeowv1.StreamEventsResponse{Payload: &whatsmeowv1.StreamEventsResponse_HistoryMessages{HistoryMessages: batches[n]}}

		if err := svc.record(ctx, sessionID, key, event, "UPDATE history_blob SET batches_done = ? WHERE seq = ?", n+1, q.seq); err != nil {
			return err
		}
	}

	progress := &whatsmeowv1.HistorySyncProgressEvent{
		SyncType: syncType, ChunkOrder: blob.GetChunkOrder(), Progress: blob.GetProgress(),
		MessageCount: count(len(messages) - oversized), SkippedCount: count(skipped),
	}
	if err := svc.record(ctx, sessionID, prefix+q.messageID+"\x00p", progressEvent(progress), "UPDATE history_blob SET recorded = 1 WHERE seq = ?", q.seq); err != nil {
		return err
	}

	return svc.finish(ctx, sessionID, client, q)
}

// fetch downloads and parses the blob, one at a time across the sessions; synchronousStorage, so what whatsmeow
// stores from it (message secrets, LID mappings, push names) is stored before its messages are delivered.
func (svc *Service) fetch(ctx context.Context, sessionID string, client Client, notif *waE2E.HistorySyncNotification) (*waHistorySync.HistorySync, error) {
	select {
	case svc.download <- struct{}{}:
	default:
		svc.onQueue(sessionID)

		select {
		case svc.download <- struct{}{}:
		case <-ctx.Done():
			return nil, fmt.Errorf("wait for another session's download: %w", ctx.Err())
		}
	}

	defer func() { <-svc.download }()

	blob, err := client.DownloadHistorySync(ctx, notif, true)
	if err != nil {
		return nil, fmt.Errorf("download a history sync blob: %w", err)
	}

	return blob, nil
}

// failed handles a download that failed: offline is not counted and ends the worker, a blob that cannot be
// downloaded any more is reported, and anything else is counted and tried again.
func (svc *Service) failed(ctx context.Context, sessionID string, q queued, err error) error {
	failure := classify(err)

	switch {
	case ctx.Err() != nil || errors.Is(err, whatsmeow.ErrNotConnected):
		return errOffline
	case failure == whatsmeowv1.HistorySyncFailure_HISTORY_SYNC_FAILURE_UNSPECIFIED && q.tries+1 < attempts:
		if _, err := svc.db.ExecContext(ctx, "UPDATE history_blob SET attempts = attempts + 1 WHERE seq = ?", q.seq); err != nil {
			return fmt.Errorf("count a failed download: %w", err)
		}

		svc.log.Warn().Str("session_id", sessionID).Int("attempt", q.tries+1).Msg("downloading a history sync blob failed; it is tried again in a minute")

		select {
		case <-svc.retryAfter(retryDelay):
			return nil
		case <-ctx.Done():
			return fmt.Errorf("wait to try again: %w", ctx.Err())
		}
	case failure == whatsmeowv1.HistorySyncFailure_HISTORY_SYNC_FAILURE_UNSPECIFIED:
		failure = whatsmeowv1.HistorySyncFailure_HISTORY_SYNC_FAILURE_UNAVAILABLE
	}

	svc.log.Warn().Str("session_id", sessionID).Str("failure", failure.String()).Msg("a history sync blob could not be downloaded; it is reported and the next goes on")

	progress := &whatsmeowv1.HistorySyncProgressEvent{
		SyncType: syncType(int32(q.notif.GetSyncType())), ChunkOrder: q.notif.GetChunkOrder(), Progress: q.notif.GetProgress(), Failure: failure,
	}

	return svc.record(ctx, sessionID, prefix+q.messageID+"\x00p", progressEvent(progress), "DELETE FROM history_blob WHERE seq = ?", q.seq)
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
// transaction.
func (svc *Service) record(ctx context.Context, sessionID, key string, event *whatsmeowv1.StreamEventsResponse, update string, args ...any) error {
	if err := svc.room(ctx, sessionID); err != nil {
		return err
	}

	differs, err := svc.box.Record(ctx, sessionID, key, event, func(tx *sql.Tx) error {
		if _, err := tx.ExecContext(ctx, update, args...); err != nil {
			return fmt.Errorf("update the queue: %w", err)
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
func (svc *Service) finish(ctx context.Context, sessionID string, client Client, q queued) error {
	err := client.DeleteMedia(ctx, whatsmeow.MediaHistory, q.notif.GetDirectPath(), q.notif.GetFileEncSHA256(), q.notif.GetEncHandle())
	if err != nil {
		if ctx.Err() != nil {
			return fmt.Errorf("delete a history sync blob: %w", ctx.Err())
		}

		// Not the error: its text carries the request URL, whose query has the media connection's auth (upload.go:331-353).
		svc.log.Warn().Str("session_id", sessionID).Msg("deleting a delivered history sync blob from WhatsApp's server failed; it is not tried again")
	}

	if _, err := svc.db.ExecContext(ctx, "DELETE FROM history_blob WHERE seq = ?", q.seq); err != nil {
		return fmt.Errorf("take a delivered blob from the queue: %w", err)
	}

	return nil
}

// parse is each message of the blob's conversations as whatsmeow parses a live one (ParseWebMessage), mapped as the
// Events slice maps a live one, and how many did not parse. Stub messages, without content, are delivered as they
// are.
func (svc *Service) parse(client Client, blob *waHistorySync.HistorySync) ([]*whatsmeowv1.MessageEvent, int) {
	var (
		messages []*whatsmeowv1.MessageEvent
		skipped  int
	)

	for _, conv := range blob.GetConversations() {
		chat, err := types.ParseJID(conv.GetID())
		if err != nil || chat.IsEmpty() {
			skipped += len(conv.GetMessages())

			continue
		}

		for _, m := range conv.GetMessages() {
			evt, err := client.ParseWebMessage(chat, m.GetMessage())
			if err != nil {
				skipped++

				continue
			}

			messages = append(messages, svc.toEvent(evt))
		}
	}

	return messages, skipped
}

// batch cuts messages into batches, in order: at most maxBatchMessages each, and at most maxBatchBytes as an event
// unless one message alone takes more. A message that alone takes more than maxEventBytes is skipped and counted.
func batch(syncType whatsmeowv1.HistorySyncType, messages []*whatsmeowv1.MessageEvent) ([]*whatsmeowv1.HistoryMessagesEvent, int) {
	var (
		batches []*whatsmeowv1.HistoryMessagesEvent
		current *whatsmeowv1.HistoryMessagesEvent
		size    int
		skipped int
	)

	for _, m := range messages {
		// The message as a repeated field of the batch: its tag and length, then its bytes.
		cost := 1 + varintSize(proto.Size(m)) + proto.Size(m)
		if eventSize(cost+2) > maxEventBytes {
			skipped++

			continue
		}

		if current != nil && (len(current.GetMessages()) == maxBatchMessages || eventSize(size+cost) > maxBatchBytes) {
			current = nil
		}

		if current == nil {
			current = &whatsmeowv1.HistoryMessagesEvent{SyncType: syncType}
			batches = append(batches, current)
			size = 2 // the sync type
		}

		current.Messages = append(current.Messages, m)
		size += cost
	}

	return batches, skipped
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

// syncType is whatsmeow's sync type number as the contract's; waE2E's and waHistorySync's agree on the shared ones.
func syncType(n int32) whatsmeowv1.HistorySyncType {
	switch waHistorySync.HistorySync_HistorySyncType(n) {
	case waHistorySync.HistorySync_INITIAL_BOOTSTRAP:
		return whatsmeowv1.HistorySyncType_HISTORY_SYNC_TYPE_INITIAL_BOOTSTRAP
	case waHistorySync.HistorySync_INITIAL_STATUS_V3:
		return whatsmeowv1.HistorySyncType_HISTORY_SYNC_TYPE_INITIAL_STATUS_V3
	case waHistorySync.HistorySync_FULL:
		return whatsmeowv1.HistorySyncType_HISTORY_SYNC_TYPE_FULL
	case waHistorySync.HistorySync_RECENT:
		return whatsmeowv1.HistorySyncType_HISTORY_SYNC_TYPE_RECENT
	case waHistorySync.HistorySync_PUSH_NAME:
		return whatsmeowv1.HistorySyncType_HISTORY_SYNC_TYPE_PUSH_NAME
	case waHistorySync.HistorySync_NON_BLOCKING_DATA:
		return whatsmeowv1.HistorySyncType_HISTORY_SYNC_TYPE_NON_BLOCKING_DATA
	case waHistorySync.HistorySync_ON_DEMAND:
		return whatsmeowv1.HistorySyncType_HISTORY_SYNC_TYPE_ON_DEMAND
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
		return nil, 0, invalid("chat", "%q is not a user's or a group's JID", req.GetChat())
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

// classifySend is a failed request's reason, as the Messaging slice classifies a send (ADR 0025): only a failure that
// provably came before the write is anything but OUTCOME_UNKNOWN. It keeps whatsmeow's sentinel or a bare copy, never
// its text, which can name a JID.
func classifySend(err error) error {
	switch {
	case errors.Is(err, whatsmeow.ErrNotLoggedIn):
		return fmt.Errorf("request history: %w: %w", server.ErrLoggedOut, whatsmeow.ErrNotLoggedIn)
	case errors.Is(err, whatsmeow.ErrNotConnected) && err != whatsmeow.ErrNotConnected: //nolint:errorlint // the unwrapped one comes after the write (request.go:207-209)
		return fmt.Errorf("request history: %w: %w", server.ErrNotConnected, whatsmeow.ErrNotConnected)
	default:
		return fmt.Errorf("request history: %w: %w", server.ErrOutcomeUnknown, cause(err))
	}
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
