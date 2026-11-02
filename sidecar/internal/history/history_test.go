package history

import (
	"compress/zlib"
	"context"
	"fmt"
	"path/filepath"
	"slices"
	"strconv"
	"strings"
	"sync"
	"testing"
	"time"

	"github.com/rs/zerolog"
	"go.mau.fi/whatsmeow"
	"go.mau.fi/whatsmeow/proto/waE2E"
	"go.mau.fi/whatsmeow/proto/waHistorySync"
	"go.mau.fi/whatsmeow/proto/waWeb"
	"go.mau.fi/whatsmeow/types"
	"go.mau.fi/whatsmeow/types/events"
	"google.golang.org/protobuf/proto"
	"google.golang.org/protobuf/types/known/timestamppb"

	whatsmeowv1 "github.com/tcortega/Whatsmeow.NET/sidecar/gen/whatsmeow/v1"
	eventsslice "github.com/tcortega/Whatsmeow.NET/sidecar/internal/events"
)

func TestANotificationIsQueuedAndWaitsForTheOfflineSync(t *testing.T) {
	t.Parallel()

	h := start(t)
	h.notify("s1", "N1", "/blob/1", blob(waHistorySync.HistorySync_INITIAL_BOOTSTRAP, texts(direct, "A", 2)))

	// Queued, never dropped, and nothing downloaded: the session has not caught up.
	if got := h.queueRows("s1"); !slices.Equal(got, []string{"N1 0 0 0"}) {
		t.Fatalf("queue %v, want N1 queued", got)
	}

	if h.running("s1") {
		t.Fatal("a worker runs before the offline sync completed")
	}

	h.caughtUp("s1")

	got := h.read("s1", 0, 2)
	ids, progress := messageIDs(t, got)

	if !slices.Equal(ids, []string{"A1", "A2"}) || len(progress) != 1 {
		t.Fatalf("history %v and %d progress events, want A1 A2 and one", ids, len(progress))
	}
}

func TestANotificationThatIsNotTheAccountsOwnIsNotQueued(t *testing.T) {
	t.Parallel()

	h := start(t)
	evt := notification("N1", "/blob/1", blob(waHistorySync.HistorySync_RECENT))
	evt.Info.IsFromMe = false

	if !h.handle("s1", evt) {
		t.Fatal("the handler withheld the answer")
	}

	if got := h.queueRows("s1"); len(got) != 0 {
		t.Fatalf("queue %v, want nothing: whatsmeow ignores a notification from anyone else (message.go:888-890)", got)
	}
}

func TestARepeatedNotificationIsQueuedOnce(t *testing.T) {
	t.Parallel()

	h := start(t)
	b := blob(waHistorySync.HistorySync_RECENT)
	h.notify("s1", "N1", "/blob/1", b)
	h.notify("s1", "N1", "/blob/1", b)

	if got := h.queueRows("s1"); !slices.Equal(got, []string{"N1 0 0 0"}) {
		t.Fatalf("queue %v, want N1 once", got)
	}
}

func TestTheHandlerWithholdsTheAnswerWhenTheQueueWriteFails(t *testing.T) {
	t.Parallel()

	h := start(t)
	h.stopNow()

	if h.handle("s1", notification("N1", "/blob/1", blob(waHistorySync.HistorySync_RECENT))) {
		t.Fatal("the handler answered a notification it did not queue")
	}

	// Anything else it does not record answers as it is.
	if !h.handle("s1", &events.Receipt{}) {
		t.Fatal("the handler failed an event it does not handle")
	}
}

func TestABlobArrivesAsBatchesThenProgressAndIsDeletedOnlyThen(t *testing.T) {
	t.Parallel()

	h := start(t)
	stub := h.stubs["s1"]
	stub.onDelete = func(string) {
		// Deleted only once every batch and the progress event are in the outbox.
		got, err := h.box.Read(context.Background(), "s1", 0, 100)
		if err != nil || len(got) != 2 || got[1].GetHistoryProgress() == nil {
			t.Errorf("deleted with %d events recorded (%v), want the batch and the progress event", len(got), err)
		}
	}

	groupConv := &waHistorySync.Conversation{ID: proto.String(group), Messages: []*waHistorySync.HistorySyncMsg{
		{Message: webMessage(group, "G1", false, member, "from a member")},
		{Message: webMessage(group, "G2", true, "", "from the account")},
	}}
	h.notify("s1", "N1", "/blob/1", blob(waHistorySync.HistorySync_RECENT, texts(direct, "D", 1), groupConv))
	h.caughtUp("s1")

	got := h.read("s1", 0, 2)
	at := timestamppb.New(time.Unix(1_600_000_000, 0))
	message := func(chat, sender, id string, fromMe bool, text string) *whatsmeowv1.MessageEvent {
		return &whatsmeowv1.MessageEvent{
			Info: &whatsmeowv1.MessageInfo{
				Source: &whatsmeowv1.MessageSource{Chat: chat, Sender: sender, IsFromMe: fromMe}, Id: id, MessageTime: at,
			},
			Message: &waE2E.Message{Conversation: proto.String(text)},
		}
	}
	want := []*whatsmeowv1.StreamEventsResponse{
		{Seq: got[0].GetSeq(), Payload: &whatsmeowv1.StreamEventsResponse_HistoryMessages{HistoryMessages: &whatsmeowv1.HistoryMessagesEvent{
			SyncType: whatsmeowv1.HistorySyncType_HISTORY_SYNC_TYPE_RECENT,
			Messages: []*whatsmeowv1.MessageEvent{
				message(direct, direct, "D1", false, "text D1"),
				message(group, member, "G1", false, "from a member"),
				message(group, "5511000000000@s.whatsapp.net", "G2", true, "from the account"),
			},
		}}},
		{Seq: got[1].GetSeq(), Payload: &whatsmeowv1.StreamEventsResponse_HistoryProgress{HistoryProgress: &whatsmeowv1.HistorySyncProgressEvent{
			SyncType: whatsmeowv1.HistorySyncType_HISTORY_SYNC_TYPE_RECENT, ChunkOrder: 1, Progress: 50, MessageCount: 3,
		}}},
	}

	for i := range want {
		if !proto.Equal(want[i], got[i]) {
			t.Fatalf("event %d:\n%v\nwant\n%v", i, got[i], want[i])
		}
	}

	h.idleFor("s1")

	if d := stub.deleted(); !slices.Equal(d, []string{"/blob/1"}) {
		t.Fatalf("deleted %v, want the blob once", d)
	}

	if q := h.queueRows("s1"); len(q) != 0 {
		t.Fatalf("queue %v, want it empty", q)
	}
}

func TestTheWorkerKeepsAtMostAWindowOfHistoryUntilTheConsumersAck(t *testing.T) {
	t.Parallel()

	h := start(t)
	// Ten full batches and a progress event: eleven history events.
	h.notify("s1", "N1", "/blob/1", blob(waHistorySync.HistorySync_FULL, texts(direct, "M", 10*maxBatchMessages)))

	// An abandoned consumer: it has a cursor and never acks.
	if _, err := h.box.Cursor(h.ctx, "s1", "abandoned"); err != nil {
		t.Fatalf("Cursor: %v", err)
	}

	h.caughtUp("s1")
	h.waitedFor("s1")

	// The outbox growth with an abandoned consumer (ADR 0035): the window, and nothing deleted from the server.
	var rows, size int
	if err := h.db.DB.QueryRowContext(h.ctx, "SELECT count(*), coalesce(sum(length(payload)), 0) FROM outbox WHERE session_id = 's1'").Scan(&rows, &size); err != nil {
		t.Fatalf("measure the outbox: %v", err)
	}

	t.Logf("abandoned consumer: %d outbox rows, %d payload bytes, after a blob of %d messages", rows, size, 10*maxBatchMessages)

	if rows != window || len(h.stubs["s1"].deleted()) != 0 {
		t.Fatalf("%d rows and deletes %v, want the window, %d, and none", rows, h.stubs["s1"].deleted(), window)
	}

	// A consumer that acks gets the rest, a window at a time.
	var after uint64

	var all []*whatsmeowv1.StreamEventsResponse

	for len(all) < 11 {
		got := h.read("s1", after, min(window, 11-len(all)))
		all = append(all, got...)
		after = seqOf(got)

		if err := h.box.Ack(h.ctx, "s1", "abandoned", after); err != nil {
			t.Fatalf("Ack: %v", err)
		}
	}

	ids, progress := messageIDs(t, all)
	if len(ids) != 10*maxBatchMessages || ids[0] != "M1" || ids[len(ids)-1] != fmt.Sprintf("M%d", 10*maxBatchMessages) || len(progress) != 1 {
		t.Fatalf("%d messages and %d progress events, want %d in order and one", len(ids), len(progress), 10*maxBatchMessages)
	}

	h.idleFor("s1")

	if d := h.stubs["s1"].deleted(); !slices.Equal(d, []string{"/blob/1"}) {
		t.Fatalf("deleted %v, want the blob once", d)
	}
}

func TestARestartProcessesAHalfDoneBlobAgainFromItsNextBatch(t *testing.T) {
	t.Parallel()

	path := filepath.Join(t.TempDir(), "whatsmeow-net.db")
	b := blob(waHistorySync.HistorySync_FULL, texts(direct, "M", 6*maxBatchMessages))

	h := startLogging(t, zerolog.Nop(), path)
	h.notify("s1", "N1", "/blob/1", b)
	h.caughtUp("s1")
	h.waitedFor("s1")

	first := h.read("s1", 0, window)
	if q := h.queueRows("s1"); !slices.Equal(q, []string{fmt.Sprintf("N1 %d 0 0", window*maxBatchMessages)}) {
		t.Fatalf("queue %v, want N1 with %d batches of messages done", q, window)
	}

	// The sidecar stops with the blob half done, and nothing deleted.
	h.stopNow()

	if d := h.stubs["s1"].deleted(); len(d) != 0 {
		t.Fatalf("deleted %v before the blob was done", d)
	}

	h = startLogging(t, zerolog.Nop(), path)
	h.stubs["s1"].blobs["/blob/1"] = b

	if err := h.box.Ack(h.ctx, "s1", "client", seqOf(first)); err != nil {
		t.Fatalf("Ack: %v", err)
	}

	h.caughtUp("s1")

	rest := h.read("s1", seqOf(first), 6-window+1)
	ids, progress := messageIDs(t, append(first, rest...))

	want := make([]string, 0, 6*maxBatchMessages)
	for i := 1; i <= 6*maxBatchMessages; i++ {
		want = append(want, fmt.Sprintf("M%d", i))
	}

	// Every message once: the batches before the restart are not recorded again.
	if !slices.Equal(ids, want) || len(progress) != 1 || int(progress[0].GetMessageCount()) != len(want) {
		t.Fatalf("%d messages, %v progress; want %d once each and one progress event counting them", len(ids), progress, len(want))
	}

	h.idleFor("s1")

	if d := h.stubs["s1"].deleted(); !slices.Equal(d, []string{"/blob/1"}) {
		t.Fatalf("deleted %v, want the blob once", d)
	}
}

func TestABlobRecordedBeforeACrashIsDeletedOnTheNextRunWithoutADownload(t *testing.T) {
	t.Parallel()

	h := start(t)
	notif, _ := proto.Marshal(notification("N1", "/blob/1", blob(waHistorySync.HistorySync_RECENT)).Message.GetProtocolMessage().GetHistorySyncNotification())

	if _, err := h.db.DB.ExecContext(h.ctx, "INSERT INTO history_blob (session_id, message_id, notification, messages_done, recorded) VALUES ('s1', 'N1', ?, 1, 1)", notif); err != nil {
		t.Fatalf("queue a recorded blob: %v", err)
	}

	h.caughtUp("s1")
	h.idleFor("s1")

	if got, d := h.stubs["s1"].downloaded(), h.stubs["s1"].deleted(); len(got) != 0 || !slices.Equal(d, []string{"/blob/1"}) {
		t.Fatalf("downloads %v and deletes %v, want none and the blob", got, d)
	}

	if q := h.queueRows("s1"); len(q) != 0 {
		t.Fatalf("queue %v, want it empty", q)
	}
}

func TestABlobThatCanNoLongerBeDownloadedIsReportedAndTheNextArrives(t *testing.T) {
	t.Parallel()

	corrupt := func() error {
		_, err := zlib.NewReader(strings.NewReader("not zlib"))

		return fmt.Errorf("failed to prepare to decompress: %w", err)
	}

	for _, c := range []struct {
		name string
		err  error
		want whatsmeowv1.HistorySyncFailure
	}{
		{"403", fmt.Errorf("failed to download: %w", whatsmeow.ErrMediaDownloadFailedWith403), whatsmeowv1.HistorySyncFailure_HISTORY_SYNC_FAILURE_EXPIRED},
		{"404", fmt.Errorf("failed to download: %w", whatsmeow.ErrMediaDownloadFailedWith404), whatsmeowv1.HistorySyncFailure_HISTORY_SYNC_FAILURE_EXPIRED},
		{"410", fmt.Errorf("failed to download: %w", whatsmeow.ErrMediaDownloadFailedWith410), whatsmeowv1.HistorySyncFailure_HISTORY_SYNC_FAILURE_EXPIRED},
		{"mac", fmt.Errorf("failed to download: %w", whatsmeow.ErrInvalidMediaHMAC), whatsmeowv1.HistorySyncFailure_HISTORY_SYNC_FAILURE_INVALID},
		{"sha", fmt.Errorf("failed to download: %w", whatsmeow.ErrInvalidMediaSHA256), whatsmeowv1.HistorySyncFailure_HISTORY_SYNC_FAILURE_INVALID},
		{"no url", fmt.Errorf("failed to download: %w", whatsmeow.ErrNoURLPresent), whatsmeowv1.HistorySyncFailure_HISTORY_SYNC_FAILURE_INVALID},
		{"zlib", corrupt(), whatsmeowv1.HistorySyncFailure_HISTORY_SYNC_FAILURE_INVALID},
		{"proto", fmt.Errorf("failed to unmarshal: %w", proto.Unmarshal([]byte{0xff}, &waHistorySync.HistorySync{})), whatsmeowv1.HistorySyncFailure_HISTORY_SYNC_FAILURE_INVALID},
	} {
		t.Run(c.name, func(t *testing.T) {
			t.Parallel()

			h := start(t)
			h.stubs["s1"].errs["/blob/1"] = []error{c.err}
			h.notify("s1", "N1", "/blob/1", blob(waHistorySync.HistorySync_RECENT, texts(direct, "A", 1)))
			h.notify("s1", "N2", "/blob/2", blob(waHistorySync.HistorySync_RECENT, texts(direct, "B", 1)))
			h.caughtUp("s1")

			got := h.read("s1", 0, 3)
			ids, progress := messageIDs(t, got)

			if !slices.Equal(ids, []string{"B1"}) || len(progress) != 2 || progress[0].GetFailure() != c.want ||
				progress[0].GetMessageCount() != 0 || progress[1].GetFailure() != whatsmeowv1.HistorySyncFailure_HISTORY_SYNC_FAILURE_UNSPECIFIED {
				t.Fatalf("messages %v, progress %v; want the first reported %v, then B1 delivered", ids, progress, c.want)
			}

			h.idleFor("s1")

			// Only a blob whose messages are in the outbox is deleted.
			if d := h.stubs["s1"].deleted(); !slices.Equal(d, []string{"/blob/2"}) {
				t.Fatalf("deleted %v, want only the second blob", d)
			}
		})
	}
}

func TestADownloadThatKeepsFailingIsTriedThreeTimesThenReportedUnavailable(t *testing.T) {
	t.Parallel()

	h := start(t)
	h.stubs["s1"].errs["/blob/1"] = []error{networkError(), networkError(), networkError()}
	h.stubs["s1"].errs["/blob/2"] = []error{networkError(), networkError()}
	h.notify("s1", "N1", "/blob/1", blob(waHistorySync.HistorySync_RECENT, texts(direct, "A", 1)))
	h.notify("s1", "N2", "/blob/2", blob(waHistorySync.HistorySync_RECENT, texts(direct, "B", 1)))
	h.caughtUp("s1")

	got := h.read("s1", 0, 3)
	ids, progress := messageIDs(t, got)

	if !slices.Equal(ids, []string{"B1"}) || len(progress) != 2 ||
		progress[0].GetFailure() != whatsmeowv1.HistorySyncFailure_HISTORY_SYNC_FAILURE_UNAVAILABLE ||
		progress[1].GetFailure() != whatsmeowv1.HistorySyncFailure_HISTORY_SYNC_FAILURE_UNSPECIFIED {
		t.Fatalf("messages %v, progress %v; want the first unavailable after three tries, the second delivered on its third", ids, progress)
	}

	if d := h.stubs["s1"].downloaded(); !slices.Equal(d, []string{"/blob/1", "/blob/1", "/blob/1", "/blob/2", "/blob/2", "/blob/2"}) {
		t.Fatalf("downloads %v, want three of each", d)
	}
}

func TestADownloadThatFailsBecauseTheSessionWentOfflineIsNotCounted(t *testing.T) {
	t.Parallel()

	h := start(t)
	h.stubs["s1"].errs["/blob/1"] = []error{fmt.Errorf("failed to download: failed to refresh media connections: %w", whatsmeow.ErrNotConnected)}
	h.notify("s1", "N1", "/blob/1", blob(waHistorySync.HistorySync_RECENT, texts(direct, "A", 1)))
	h.caughtUp("s1")

	// The worker stops without counting it, and the next offline sync delivers it.
	h.offlineFor("s1")

	if q := h.queueRows("s1"); !slices.Equal(q, []string{"N1 0 0 0"}) {
		t.Fatalf("queue %v, want N1 with no attempt counted", q)
	}

	h.caughtUp("s1")

	if ids, _ := messageIDs(t, h.read("s1", 0, 2)); !slices.Equal(ids, []string{"A1"}) {
		t.Fatalf("messages %v, want A1", ids)
	}
}

func TestTheWorkerEndsWhenTheSessionLeavesOnlineAndTheNextOfflineSyncResumes(t *testing.T) {
	t.Parallel()

	h := start(t)
	h.notify("s1", "N1", "/blob/1", blob(waHistorySync.HistorySync_FULL, texts(direct, "M", 6*maxBatchMessages)))
	h.caughtUp("s1")
	h.waitedFor("s1")

	h.sessions.leave("s1")
	h.ended("s1")

	h.sessions.mu.Lock()
	h.sessions.online["s1"] = &online{client: h.stubs["s1"], device: h.sessions.online["s1"].device, left: make(chan struct{})}
	h.sessions.mu.Unlock()

	first := h.read("s1", 0, window)
	if err := h.box.Ack(h.ctx, "s1", "client", seqOf(first)); err != nil {
		t.Fatalf("Ack: %v", err)
	}

	h.caughtUp("s1")

	if ids, progress := messageIDs(t, append(first, h.read("s1", seqOf(first), 6-window+1)...)); len(ids) != 6*maxBatchMessages || len(progress) != 1 {
		t.Fatalf("%d messages and %d progress events, want %d and one", len(ids), len(progress), 6*maxBatchMessages)
	}
}

func TestOneDownloadRunsAtATimeAcrossSessions(t *testing.T) {
	t.Parallel()

	h := start(t, "s1", "s2")
	hold := make(chan struct{})
	h.stubs["s1"].hold = hold
	h.stubs["s1"].entered = make(chan string, 1)

	h.notify("s1", "N1", "/blob/1", blob(waHistorySync.HistorySync_RECENT, texts(direct, "A", 1)))
	h.notify("s2", "N2", "/blob/2", blob(waHistorySync.HistorySync_RECENT, texts(direct, "B", 1)))
	h.caughtUp("s1")

	select {
	case <-h.stubs["s1"].entered:
	case <-h.ctx.Done():
		t.Fatal("s1's download never started")
	}

	h.caughtUp("s2")

	select {
	case id := <-h.queued:
		if id != "s2" {
			t.Fatalf("session %s queued, want s2", id)
		}
	case <-h.ctx.Done():
		t.Fatal("s2's download did not wait for s1's")
	}

	if d := h.stubs["s2"].downloaded(); len(d) != 0 {
		t.Fatalf("s2 downloaded %v while s1's download ran", d)
	}

	close(hold)

	if ids, _ := messageIDs(t, h.read("s2", 0, 2)); !slices.Equal(ids, []string{"B1"}) {
		t.Fatalf("s2's messages %v, want B1", ids)
	}
}

func TestBatchesStayWithinTheirBoundsInOrder(t *testing.T) {
	t.Parallel()

	text := func(id string, size int) *whatsmeowv1.MessageEvent {
		return &whatsmeowv1.MessageEvent{Info: &whatsmeowv1.MessageInfo{Id: id}, Message: &waE2E.Message{Conversation: proto.String(strings.Repeat("x", size))}}
	}

	var messages []*whatsmeowv1.MessageEvent

	for i := range 600 {
		messages = append(messages, text(fmt.Sprintf("S%d", i), 10))
	}

	for i := range 5 {
		messages = append(messages, text(fmt.Sprintf("L%d", i), 600<<10))
	}

	messages = append(messages, text("ALONE", 2<<20), text("TOO-LARGE", 16<<20), text("LAST", 10))

	batches, delivered, skipped := batch(whatsmeowv1.HistorySyncType_HISTORY_SYNC_TYPE_FULL, messages, 0)

	var (
		ids   []string
		sizes []int
	)

	for _, cut := range batches {
		b := cut.event
		event := &whatsmeowv1.StreamEventsResponse{Seq: ^uint64(0), Payload: &whatsmeowv1.StreamEventsResponse_HistoryMessages{HistoryMessages: b}}
		size := proto.Size(event)
		sizes = append(sizes, size)

		if len(b.GetMessages()) > maxBatchMessages || (size > maxBatchBytes && len(b.GetMessages()) > 1) || size > maxEventBytes {
			t.Fatalf("a batch of %d messages and %d bytes, over the bounds", len(b.GetMessages()), size)
		}

		for _, m := range b.GetMessages() {
			ids = append(ids, m.GetInfo().GetId())
		}
	}

	want := make([]string, 0, len(messages))
	for _, m := range messages {
		if m.GetInfo().GetId() != "TOO-LARGE" {
			want = append(want, m.GetInfo().GetId())
		}
	}

	if !slices.Equal(ids, want) || skipped != 1 || delivered != len(want) {
		t.Fatalf("batched %d messages, skipped %d; want every one but the one too large for a gRPC message, in order", len(ids), skipped)
	}

	// From the middle: the batches start at that message, keyed by its index, and the counts are the whole blob's.
	rest, restDelivered, restSkipped := batch(whatsmeowv1.HistorySyncType_HISTORY_SYNC_TYPE_FULL, messages, 300)
	if rest[0].first != 300 || rest[0].event.GetMessages()[0].GetInfo().GetId() != "S300" || restDelivered != delivered || restSkipped != skipped {
		t.Fatalf("from 300: first %d (%s), counts %d and %d; want 300 (S300) and the whole blob's", rest[0].first,
			rest[0].event.GetMessages()[0].GetInfo().GetId(), restDelivered, restSkipped)
	}

	for i := 1; i < len(rest); i++ {
		if rest[i].first < rest[i-1].end {
			t.Fatalf("batch %d starts at %d, after one that ends at %d", i, rest[i].first, rest[i-1].end)
		}
	}

	t.Logf("batch sizes: %v", sizes)
}

func TestAMessageThatDoesNotParseIsSkippedAndCountedAndTheLogCarriesNoJID(t *testing.T) {
	t.Parallel()

	var log logged

	h := startLogging(t, zerolog.New(&log).Level(zerolog.InfoLevel), filepath.Join(t.TempDir(), "whatsmeow-net.db"))

	broken := &waHistorySync.Conversation{ID: proto.String(group), Messages: []*waHistorySync.HistorySyncMsg{
		{Message: webMessage(group, "G1", false, "", "a group message without a sender")},
		{Message: webMessage(group, "G2", false, member, "fine")},
	}}
	badChat := &waHistorySync.Conversation{ID: proto.String("5511987654321:abc@s.whatsapp.net"), Messages: texts(direct, "X", 1).Messages}
	h.notify("s1", "N1", "/blob/1", blob(waHistorySync.HistorySync_RECENT, broken, badChat))
	h.stubs["s1"].errs["/blob/2"] = []error{fmt.Errorf("failed to download: https://mmg.whatsapp.net/v/x?oh=SECRET&oe=1 for %s: %w", member, whatsmeow.ErrMediaDownloadFailedWith404)}
	h.notify("s1", "N2", "/blob/2", blob(waHistorySync.HistorySync_RECENT))
	h.caughtUp("s1")

	ids, progress := messageIDs(t, h.read("s1", 0, 3))
	if !slices.Equal(ids, []string{"G2"}) || progress[0].GetSkippedCount() != 2 || progress[0].GetMessageCount() != 1 {
		t.Fatalf("messages %v, progress %v; want G2 and two skipped", ids, progress)
	}

	h.idleFor("s1")
	h.stopNow()

	if s := log.String(); strings.Contains(s, "5511") || strings.Contains(s, "120363") || strings.Contains(s, "SECRET") {
		t.Fatalf("the log carries a JID or a URL query:\n%s", s)
	}
}

func TestASessionsBlobsGoWithItsDeletion(t *testing.T) {
	t.Parallel()

	h := start(t)
	h.notify("s1", "N1", "/blob/1", blob(waHistorySync.HistorySync_RECENT))

	if _, err := h.db.DB.ExecContext(h.ctx, "DELETE FROM session WHERE id = 's1'"); err != nil {
		t.Fatalf("delete the session: %v", err)
	}

	var n int
	if err := h.db.DB.QueryRowContext(h.ctx, "SELECT count(*) FROM history_blob").Scan(&n); err != nil || n != 0 {
		t.Fatalf("%d queued, %v; want none", n, err)
	}
}

func TestRequestHistoryAsksThePhoneForOlderMessagesOfTheChat(t *testing.T) {
	t.Parallel()

	h := start(t)
	at := timestamppb.New(timestamppb.Now().AsTime().Truncate(1e9))

	if _, err := h.client.RequestHistory(h.ctx, &whatsmeowv1.RequestHistoryRequest{
		SessionId: "s1", Chat: group, OldestMessageId: "OLD1", OldestFromMe: true, OldestMessageTime: at, Count: 50,
	}); err != nil {
		t.Fatalf("RequestHistory: %v", err)
	}

	s := h.stubs["s1"]
	if len(s.sends) != 1 {
		t.Fatalf("%d sends, want one", len(s.sends))
	}

	got := s.sends[0]
	chat, _ := types.ParseJID(group)
	want := s.real.BuildHistorySyncRequest(&types.MessageInfo{MessageSource: types.MessageSource{Chat: chat, IsFromMe: true}, ID: "OLD1", Timestamp: at.AsTime()}, 50)

	if got.to.String() != "5511000000000@s.whatsapp.net" || !got.extra.Peer || !idPattern.MatchString(got.extra.ID) || !proto.Equal(got.message, want) {
		t.Fatalf("sent %v to %v with %+v, want %v to the account as a peer message with a 3EB0 id", got.message, got.to, got.extra, want)
	}
}

func TestRequestHistoryRefusesWhatItCannotAsk(t *testing.T) {
	t.Parallel()

	h := start(t)
	at := timestamppb.Now()
	valid := func(set func(*whatsmeowv1.RequestHistoryRequest)) *whatsmeowv1.RequestHistoryRequest {
		req := &whatsmeowv1.RequestHistoryRequest{SessionId: "s1", Chat: direct, OldestMessageId: "OLD1", OldestMessageTime: at, Count: 50}
		set(req)

		return req
	}

	for _, c := range []struct {
		name, reason string
		req          *whatsmeowv1.RequestHistoryRequest
	}{
		{"session", "INVALID_ARGUMENT", valid(func(r *whatsmeowv1.RequestHistoryRequest) { r.SessionId = "" })},
		{"chat", "INVALID_ARGUMENT", valid(func(r *whatsmeowv1.RequestHistoryRequest) { r.Chat = "nobody" })},
		{"device", "INVALID_ARGUMENT", valid(func(r *whatsmeowv1.RequestHistoryRequest) { r.Chat = "5511987654321:2@s.whatsapp.net" })},
		{"broadcast", "INVALID_ARGUMENT", valid(func(r *whatsmeowv1.RequestHistoryRequest) { r.Chat = "status@broadcast" })},
		{"id", "INVALID_ARGUMENT", valid(func(r *whatsmeowv1.RequestHistoryRequest) { r.OldestMessageId = "" })},
		{"time", "INVALID_ARGUMENT", valid(func(r *whatsmeowv1.RequestHistoryRequest) { r.OldestMessageTime = nil })},
		{"zero", "INVALID_ARGUMENT", valid(func(r *whatsmeowv1.RequestHistoryRequest) { r.Count = 0 })},
		{"too many", "INVALID_ARGUMENT", valid(func(r *whatsmeowv1.RequestHistoryRequest) { r.Count = 1 << 31 })},
		{"unknown session", "SESSION_NOT_FOUND", valid(func(r *whatsmeowv1.RequestHistoryRequest) { r.SessionId = "s9" })},
	} {
		_, err := h.client.RequestHistory(h.ctx, c.req)
		if got := reason(err); got != c.reason || strings.Contains(err.Error(), "5511") {
			t.Errorf("%s: %v, want %s without the JID", c.name, err, c.reason)
		}
	}

	if len(h.stubs["s1"].sends) != 0 {
		t.Fatalf("sent %d, want nothing", len(h.stubs["s1"].sends))
	}
}

func TestRequestHistoryErrorsBecomeTheirReasonsWithoutAJID(t *testing.T) {
	t.Parallel()

	h := start(t)
	req := &whatsmeowv1.RequestHistoryRequest{SessionId: "s1", Chat: direct, OldestMessageId: "OLD1", OldestMessageTime: timestamppb.Now(), Count: 50}

	for _, c := range []struct {
		err    error
		reason string
	}{
		{fmt.Errorf("failed to send message node to %s: %w", direct, whatsmeow.ErrNotConnected), "NOT_CONNECTED"},
		{whatsmeow.ErrNotConnected, "OUTCOME_UNKNOWN"},
		{fmt.Errorf("failed to get device list of %s: %w", direct, whatsmeow.ErrIQTimedOut), "OUTCOME_UNKNOWN"},
		{fmt.Errorf("%w for %s", whatsmeow.ErrNotLoggedIn, direct), "LOGGED_OUT"},
		// As the Messaging slice reads a send (messaging/send.go classify): 429 is a rate limit, WhatsApp's other answers
		// are definite failures.
		{fmt.Errorf("failed to get device list of %s: %w", direct, &whatsmeow.IQError{Code: 429, Text: "rate-overlimit"}), "RATE_LIMITED"},
		{fmt.Errorf("failed to get device list of %s: %w", direct, &whatsmeow.IQError{Code: 500, Text: "internal-server-error"}), "WHATSAPP_ERROR"},
		{fmt.Errorf("%w %d", whatsmeow.ErrServerReturnedError, 479), "WHATSAPP_ERROR"},
	} {
		h.stubs["s1"].sendErr = c.err

		_, err := h.client.RequestHistory(h.ctx, req)
		if got := reason(err); got != c.reason || strings.Contains(err.Error(), "5511") {
			t.Errorf("%v: %v, want %s without the JID", c.err, err, c.reason)
		}
	}

	h.sessions.leave("s1")

	if _, err := h.client.RequestHistory(h.ctx, req); reason(err) != "NOT_CONNECTED" {
		t.Fatalf("offline: %v, want NOT_CONNECTED", err)
	}
}

func TestAHistoryMediaMessageCarriesWhatALiveOneDoes(t *testing.T) {
	t.Parallel()

	h := start(t)
	image := &waE2E.ImageMessage{
		URL: proto.String("https://mmg.whatsapp.net/x"), DirectPath: proto.String("/v/x"), MediaKey: []byte{1}, FileSHA256: []byte{2},
		FileEncSHA256: []byte{3}, FileLength: proto.Uint64(4), Mimetype: proto.String("image/jpeg"),
	}
	plain := webMessage(direct, "I1", false, "", "")
	plain.Message = &waE2E.Message{ImageMessage: image}
	once := webMessage(direct, "I2", false, "", "")
	once.Message = &waE2E.Message{ViewOnceMessageV2: &waE2E.FutureProofMessage{Message: &waE2E.Message{ImageMessage: image}}}

	conv := &waHistorySync.Conversation{ID: proto.String(direct), Messages: []*waHistorySync.HistorySyncMsg{{Message: plain}, {Message: once}}}
	h.notify("s1", "N1", "/blob/1", blob(waHistorySync.HistorySync_RECENT, conv))
	h.caughtUp("s1")

	got := h.read("s1", 0, 1)[0].GetHistoryMessages().GetMessages()

	// As the Events slice maps a live one: the view-once wrapper removed, and said (events.MessageEvent).
	if len(got) != 2 || !proto.Equal(got[0].GetMessage().GetImageMessage(), image) || got[0].GetViewOnce() ||
		!proto.Equal(got[1].GetMessage().GetImageMessage(), image) || !got[1].GetViewOnce() {
		t.Fatalf("messages %v, want the image twice, the second view once", got)
	}
}

func TestASecondOfflineSyncInOneConnectionWakesTheWorker(t *testing.T) {
	t.Parallel()

	h := start(t)
	h.caughtUp("s1")
	h.idleFor("s1")

	// whatsmeow dispatches OfflineSyncCompleted for each offline ib (connectionevents.go:85-88).
	h.caughtUp("s1")
	h.notify("s1", "N1", "/blob/1", blob(waHistorySync.HistorySync_RECENT, texts(direct, "A", 1)))

	if ids, progress := messageIDs(t, h.read("s1", 0, 2)); !slices.Equal(ids, []string{"A1"}) || len(progress) != 1 {
		t.Fatalf("messages %v and %d progress events, want A1 and one", ids, len(progress))
	}
}

func TestAnOfflineSyncBeforeConnectedStartsTheWorkerOnceTheSessionIsUp(t *testing.T) {
	t.Parallel()

	h := start(t)
	// whatsmeow dispatches Connected on its own goroutine after two IQs (connectionevents.go:183-205), and the offline
	// ib on the node queue, so OfflineSyncCompleted can come first.
	h.sessions.connecting("s1")
	h.notify("s1", "N1", "/blob/1", blob(waHistorySync.HistorySync_RECENT, texts(direct, "A", 1)))
	h.caughtUp("s1")
	h.delayed(upTimeout)

	h.sessions.comeUp("s1")

	if ids, progress := messageIDs(t, h.read("s1", 0, 2)); !slices.Equal(ids, []string{"A1"}) || len(progress) != 1 {
		t.Fatalf("messages %v and %d progress events, want A1 and one", ids, len(progress))
	}
}

func TestAWorkerWhoseSessionNeverComesUpGivesUpAndTheNextOfflineSyncStartsOne(t *testing.T) {
	t.Parallel()

	var log logged

	h := startLogging(t, zerolog.New(&log).Level(zerolog.DebugLevel), filepath.Join(t.TempDir(), "whatsmeow-net.db"))
	h.sessions.connecting("s1")
	h.notify("s1", "N1", "/blob/1", blob(waHistorySync.HistorySync_RECENT, texts(direct, "A", 1)))
	h.caughtUp("s1")
	h.delayed(upTimeout)

	h.upExpired <- time.Time{}

	h.ended("s1")

	if !strings.Contains(log.String(), `"level":"debug"`) {
		t.Fatalf("giving up was not logged at debug:\n%s", log.String())
	}

	h.sessions.comeUp("s1")
	h.caughtUp("s1")

	if ids, _ := messageIDs(t, h.read("s1", 0, 2)); !slices.Equal(ids, []string{"A1"}) {
		t.Fatalf("messages %v, want A1", ids)
	}
}

func TestADeliveryThatFailsIsTriedAgainAfterABackoffWhileOnline(t *testing.T) {
	t.Parallel()

	h := start(t)
	// A failure that is neither the download's nor offline: the queue's update fails.
	if _, err := h.db.DB.ExecContext(h.ctx, "CREATE TRIGGER broken BEFORE UPDATE ON history_blob BEGIN SELECT RAISE(ABORT, 'broken'); END"); err != nil {
		t.Fatalf("break the queue: %v", err)
	}

	h.notify("s1", "N1", "/blob/1", blob(waHistorySync.HistorySync_RECENT, texts(direct, "A", 1)))
	h.caughtUp("s1")
	h.delayed(minBackoff)

	if _, err := h.db.DB.ExecContext(h.ctx, "DROP TRIGGER broken"); err != nil {
		t.Fatalf("repair the queue: %v", err)
	}

	if ids, progress := messageIDs(t, h.read("s1", 0, 2)); !slices.Equal(ids, []string{"A1"}) || len(progress) != 1 {
		t.Fatalf("messages %v and %d progress events, want A1 and one", ids, len(progress))
	}
}

func TestAQueuedNotificationThatDoesNotParseIsReportedInvalidAndTheNextArrives(t *testing.T) {
	t.Parallel()

	h := start(t)
	if _, err := h.db.DB.ExecContext(h.ctx, "INSERT INTO history_blob (session_id, message_id, notification) VALUES ('s1', 'N0', x'ff')"); err != nil {
		t.Fatalf("queue a broken notification: %v", err)
	}

	h.notify("s1", "N1", "/blob/1", blob(waHistorySync.HistorySync_RECENT, texts(direct, "A", 1)))
	h.caughtUp("s1")

	ids, progress := messageIDs(t, h.read("s1", 0, 3))
	if !slices.Equal(ids, []string{"A1"}) || len(progress) != 2 || progress[0].GetFailure() != whatsmeowv1.HistorySyncFailure_HISTORY_SYNC_FAILURE_INVALID ||
		progress[1].GetFailure() != whatsmeowv1.HistorySyncFailure_HISTORY_SYNC_FAILURE_UNSPECIFIED {
		t.Fatalf("messages %v, progress %v; want the broken one reported invalid, then A1 delivered", ids, progress)
	}

	h.idleFor("s1")

	if q := h.queueRows("s1"); len(q) != 0 {
		t.Fatalf("queue %v, want it empty", q)
	}
}

func TestAStalledDownloadTimesOutAndCountsAsAnAttempt(t *testing.T) {
	t.Parallel()

	h := start(t)
	h.svc.downloadTimeout = 50 * time.Millisecond
	// Never closed: each download waits until its context ends.
	h.stubs["s1"].hold = make(chan struct{})
	h.notify("s1", "N1", "/blob/1", blob(waHistorySync.HistorySync_RECENT, texts(direct, "A", 1)))
	h.caughtUp("s1")

	_, progress := messageIDs(t, h.read("s1", 0, 1))
	if len(progress) != 1 || progress[0].GetFailure() != whatsmeowv1.HistorySyncFailure_HISTORY_SYNC_FAILURE_UNAVAILABLE {
		t.Fatalf("progress %v, want the blob reported unavailable", progress)
	}

	if d := h.stubs["s1"].downloaded(); len(d) != attempts {
		t.Fatalf("downloads %v, want %d", d, attempts)
	}
}

func TestARestartAfterTheMappingChangedResumesAtTheNextMessage(t *testing.T) {
	t.Parallel()

	path := filepath.Join(t.TempDir(), "whatsmeow-net.db")
	const n = 6 * maxBatchMessages
	b := blob(waHistorySync.HistorySync_FULL, texts(direct, "M", n))

	var log logged

	h := startLogging(t, zerolog.New(&log), path)
	// A mapping that makes each message 8 KiB, so the first run cuts batches by bytes, of fewer messages each.
	h.svc.toEvent = func(m *events.Message) *whatsmeowv1.MessageEvent {
		e := eventsslice.MessageEvent(m)
		e.Message = &waE2E.Message{Conversation: proto.String(strings.Repeat("x", 8<<10))}

		return e
	}
	h.notify("s1", "N1", "/blob/1", b)
	h.caughtUp("s1")
	h.waitedFor("s1")

	first := h.read("s1", 0, window)
	h.stopNow()

	// The next sidecar maps messages as they are, so its batches are cut elsewhere.
	h = startLogging(t, zerolog.New(&log), path)
	h.stubs["s1"].blobs["/blob/1"] = b

	if err := h.box.Ack(h.ctx, "s1", "client", seqOf(first)); err != nil {
		t.Fatalf("Ack: %v", err)
	}

	h.caughtUp("s1")

	var all []*whatsmeowv1.StreamEventsResponse

	after := seqOf(first)

	for {
		got := h.read("s1", after, 1)
		all = append(all, got...)
		after = seqOf(got)

		if got[0].GetHistoryProgress() != nil {
			break
		}

		if err := h.box.Ack(h.ctx, "s1", "client", after); err != nil {
			t.Fatalf("Ack: %v", err)
		}
	}

	ids, _ := messageIDs(t, append(first, all...))

	want := make([]string, 0, n)
	for i := 1; i <= n; i++ {
		want = append(want, fmt.Sprintf("M%d", i))
	}

	if !slices.Equal(ids, want) {
		t.Fatalf("%d messages, want %d once each, in order", len(ids), n)
	}

	// No batch of the second run took the key of one of the first.
	h.stopNow()

	if s := log.String(); strings.Contains(s, "repeats a recorded one's key") {
		t.Fatalf("a batch reused a recorded batch's key:\n%s", s)
	}
}

func TestALoggedOutOrRepairedSessionLosesItsQueue(t *testing.T) {
	t.Parallel()

	for _, evt := range []any{&events.LoggedOut{Reason: events.ConnectFailureLoggedOut}, &events.PairSuccess{}} {
		t.Run(fmt.Sprintf("%T", evt), func(t *testing.T) {
			t.Parallel()

			h := start(t)
			h.notify("s1", "N1", "/blob/1", blob(waHistorySync.HistorySync_RECENT, texts(direct, "A", 1)))

			if !h.handle("s1", evt) {
				t.Fatalf("the handler failed %T", evt)
			}

			if q := h.queueRows("s1"); len(q) != 0 {
				t.Fatalf("queue %v, want the old account's blobs gone", q)
			}
		})
	}
}

func TestParsingRunsOneAtATimeAcrossSessions(t *testing.T) {
	t.Parallel()

	h := start(t, "s1", "s2")
	hold := make(chan struct{})
	release := sync.OnceFunc(func() { close(hold) })
	// Before the harness's stop, which waits for the held worker.
	t.Cleanup(release)
	h.stubs["s1"].parseHold = hold
	h.stubs["s1"].parseEntered = make(chan string, 1)

	h.notify("s1", "N1", "/blob/1", blob(waHistorySync.HistorySync_RECENT, texts(direct, "A", 1)))
	h.notify("s2", "N2", "/blob/2", blob(waHistorySync.HistorySync_RECENT, texts(direct, "B", 1)))
	h.caughtUp("s1")

	select {
	case <-h.stubs["s1"].parseEntered:
	case <-h.ctx.Done():
		t.Fatal("s1's parse never started")
	}

	h.caughtUp("s2")

	select {
	case id := <-h.queued:
		if id != "s2" {
			t.Fatalf("session %s queued, want s2", id)
		}
	case <-h.ctx.Done():
		t.Fatal("s2's download did not wait for s1's parse")
	}

	if d := h.stubs["s2"].downloaded(); len(d) != 0 {
		t.Fatalf("s2 downloaded %v while s1's parse ran", d)
	}

	release()

	if ids, _ := messageIDs(t, h.read("s2", 0, 2)); !slices.Equal(ids, []string{"B1"}) {
		t.Fatalf("s2's messages %v, want B1", ids)
	}
}

func TestATransientFailureAsTheClientGoesOfflineIsNotCounted(t *testing.T) {
	t.Parallel()

	t.Run("not connected", func(t *testing.T) {
		t.Parallel()

		h := start(t)
		stub := h.stubs["s1"]
		stub.errs["/blob/1"] = []error{networkError()}
		// The socket is gone before whatsmeow dispatched Disconnected: the session is still online.
		stub.onDownload = func(string) { stub.setConnected(false) }
		h.notify("s1", "N1", "/blob/1", blob(waHistorySync.HistorySync_RECENT, texts(direct, "A", 1)))
		h.caughtUp("s1")
		h.offlineFor("s1")

		if q := h.queueRows("s1"); !slices.Equal(q, []string{"N1 0 0 0"}) {
			t.Fatalf("queue %v, want N1 with no attempt counted", q)
		}

		stub.onDownload = nil
		stub.setConnected(true)
		h.caughtUp("s1")

		if ids, _ := messageIDs(t, h.read("s1", 0, 2)); !slices.Equal(ids, []string{"A1"}) {
			t.Fatalf("messages %v, want A1", ids)
		}
	})

	t.Run("left online", func(t *testing.T) {
		t.Parallel()

		h := start(t)
		stub := h.stubs["s1"]
		stub.errs["/blob/1"] = []error{networkError()}
		stub.onDownload = func(string) { h.sessions.leave("s1") }
		h.notify("s1", "N1", "/blob/1", blob(waHistorySync.HistorySync_RECENT, texts(direct, "A", 1)))
		h.caughtUp("s1")
		h.ended("s1")

		if q := h.queueRows("s1"); !slices.Equal(q, []string{"N1 0 0 0"}) {
			t.Fatalf("queue %v, want N1 with no attempt counted", q)
		}
	})
}

func TestAPanicDeliveringABlobReportsItInvalidAndTheNextArrives(t *testing.T) {
	t.Parallel()

	var log logged

	h := startLogging(t, zerolog.New(&log).Level(zerolog.InfoLevel), filepath.Join(t.TempDir(), "whatsmeow-net.db"))
	// Outside the parse, which recovers per message: the per-blob guard.
	h.stubs["s1"].panicDownload = "/blob/1"
	h.notify("s1", "N1", "/blob/1", blob(waHistorySync.HistorySync_RECENT, texts(direct, "P", 1)))
	h.notify("s1", "N2", "/blob/2", blob(waHistorySync.HistorySync_RECENT, texts(direct, "B", 1)))
	h.caughtUp("s1")

	ids, progress := messageIDs(t, h.read("s1", 0, 3))
	if !slices.Equal(ids, []string{"B1"}) || len(progress) != 2 || progress[0].GetFailure() != whatsmeowv1.HistorySyncFailure_HISTORY_SYNC_FAILURE_INVALID {
		t.Fatalf("messages %v, progress %v; want the first reported invalid, then B1 delivered", ids, progress)
	}

	h.idleFor("s1")

	if q := h.queueRows("s1"); len(q) != 0 {
		t.Fatalf("queue %v, want it empty", q)
	}

	h.stopNow()

	if s := log.String(); !strings.Contains(s, "panicked") || strings.Contains(s, "5511") {
		t.Fatalf("the log does not report the panic, or carries a JID:\n%s", s)
	}
}

func TestStatusMessagesAreNotDeliveredAndCountAsSkipped(t *testing.T) {
	t.Parallel()

	h := start(t)
	b := blob(waHistorySync.HistorySync_INITIAL_STATUS_V3, texts(direct, "A", 1))
	b.StatusV3Messages = []*waWeb.WebMessageInfo{
		webMessage("status@broadcast", "S1", false, direct, "a status"), webMessage("status@broadcast", "S2", false, member, "another"),
	}
	h.notify("s1", "N1", "/blob/1", b)
	h.caughtUp("s1")

	ids, progress := messageIDs(t, h.read("s1", 0, 2))
	if !slices.Equal(ids, []string{"A1"}) || len(progress) != 1 || progress[0].GetMessageCount() != 1 || progress[0].GetSkippedCount() != 2 {
		t.Fatalf("messages %v, progress %v; want A1, and the statuses counted skipped", ids, progress)
	}
}

func TestANotificationWithoutABlobThatSaysThereIsNoneIsReportedWithNoMessages(t *testing.T) {
	t.Parallel()

	for _, c := range []struct {
		notified waE2E.HistorySyncType
		want     whatsmeowv1.HistorySyncType
	}{
		{waE2E.HistorySyncType_NO_HISTORY, whatsmeowv1.HistorySyncType_HISTORY_SYNC_TYPE_NO_HISTORY},
		{waE2E.HistorySyncType_MESSAGE_ACCESS_STATUS, whatsmeowv1.HistorySyncType_HISTORY_SYNC_TYPE_MESSAGE_ACCESS_STATUS},
	} {
		t.Run(c.notified.String(), func(t *testing.T) {
			t.Parallel()

			h := start(t)
			evt := notification("N1", "", blob(waHistorySync.HistorySync_RECENT))
			notif := evt.Message.GetProtocolMessage().GetHistorySyncNotification()
			notif.SyncType = c.notified.Enum()
			notif.FileEncSHA256, notif.EncHandle = nil, nil

			if !h.handle("s1", evt) {
				t.Fatal("the handler withheld the answer")
			}

			h.caughtUp("s1")

			_, progress := messageIDs(t, h.read("s1", 0, 1))
			if len(progress) != 1 || progress[0].GetSyncType() != c.want || progress[0].GetMessageCount() != 0 ||
				progress[0].GetFailure() != whatsmeowv1.HistorySyncFailure_HISTORY_SYNC_FAILURE_UNSPECIFIED {
				t.Fatalf("progress %v, want %v with no messages and no failure", progress, c.want)
			}

			h.idleFor("s1")

			if got, d, q := h.stubs["s1"].downloaded(), h.stubs["s1"].deleted(), h.queueRows("s1"); len(got) != 0 || len(d) != 0 || len(q) != 0 {
				t.Fatalf("downloads %v, deletes %v, queue %v; want none of each", got, d, q)
			}
		})
	}
}

func TestAMessageWhoseParsePanicsIsSkippedAndTheRestOfItsBlobArrives(t *testing.T) {
	t.Parallel()

	h := start(t)
	h.stubs["s1"].panicOn = "P1"
	h.notify("s1", "N1", "/blob/1", blob(waHistorySync.HistorySync_RECENT, texts(direct, "P", 2)))
	h.caughtUp("s1")

	ids, progress := messageIDs(t, h.read("s1", 0, 2))
	if !slices.Equal(ids, []string{"P2"}) || len(progress) != 1 || progress[0].GetFailure() != whatsmeowv1.HistorySyncFailure_HISTORY_SYNC_FAILURE_UNSPECIFIED ||
		progress[0].GetMessageCount() != 1 || progress[0].GetSkippedCount() != 1 {
		t.Fatalf("messages %v, progress %v; want P2 delivered and P1 skipped", ids, progress)
	}
}

func TestAHistoryMessageWithAnInvalidUTF8PushNameArrivesWithTheReplacementCharacter(t *testing.T) {
	t.Parallel()

	h := start(t)
	m := webMessage(direct, "A1", false, "", "hi")
	// ParseWebMessage copies the push name unchecked (client.go:1039-1040); proto3 strings must be UTF-8.
	m.PushName = proto.String("Bia \xff")
	h.notify("s1", "N1", "/blob/1", blob(waHistorySync.HistorySync_RECENT, &waHistorySync.Conversation{
		ID: proto.String(direct), Messages: []*waHistorySync.HistorySyncMsg{{Message: m}},
	}))
	h.caughtUp("s1")

	got := h.read("s1", 0, 1)[0].GetHistoryMessages().GetMessages()
	if len(got) != 1 || got[0].GetInfo().GetPushName() != "Bia \uFFFD" {
		t.Fatalf("messages %v, want A1 with the push name %q", got, "Bia \uFFFD")
	}
}

func TestABatchThatCannotBeMarshalledReportsItsBlobInvalidAndTheNextArrives(t *testing.T) {
	t.Parallel()

	h := start(t)
	// A mapping that lets an invalid UTF-8 string through: the outbox's marshal fails with a protobuf error.
	h.svc.toEvent = func(m *events.Message) *whatsmeowv1.MessageEvent {
		e := eventsslice.MessageEvent(m)
		if e.GetInfo().GetId() == "X1" {
			e.Info.Id = "X1\xff"
		}

		return e
	}
	h.notify("s1", "N1", "/blob/1", blob(waHistorySync.HistorySync_RECENT, texts(direct, "X", 1)))
	h.notify("s1", "N2", "/blob/2", blob(waHistorySync.HistorySync_RECENT, texts(direct, "B", 1)))
	h.caughtUp("s1")

	ids, progress := messageIDs(t, h.read("s1", 0, 3))
	if !slices.Equal(ids, []string{"B1"}) || len(progress) != 2 || progress[0].GetFailure() != whatsmeowv1.HistorySyncFailure_HISTORY_SYNC_FAILURE_INVALID {
		t.Fatalf("messages %v, progress %v; want the first reported invalid, then B1 delivered", ids, progress)
	}

	h.idleFor("s1")

	if q := h.queueRows("s1"); len(q) != 0 {
		t.Fatalf("queue %v, want it empty", q)
	}
}

func TestASessionThatCameUpAsTheWaitRanOutIsNotGivenUp(t *testing.T) {
	t.Parallel()

	// The wait's timer and the session's Up channel both ready at once: select picks either, so each run tries it.
	for i := range 20 {
		t.Run(strconv.Itoa(i), func(t *testing.T) {
			t.Parallel()

			h := start(t)
			h.sessions.mu.Lock()
			h.sessions.online["s1"].notYet = 1
			h.sessions.mu.Unlock()
			h.upExpired <- time.Time{}
			h.notify("s1", "N1", "/blob/1", blob(waHistorySync.HistorySync_RECENT, texts(direct, "A", 1)))
			h.caughtUp("s1")

			if ids, _ := messageIDs(t, h.read("s1", 0, 2)); !slices.Equal(ids, []string{"A1"}) {
				t.Fatalf("messages %v, want A1", ids)
			}
		})
	}
}

func TestAWorkerWhoseQueueWasClearedMidBlobRecordsNothingMore(t *testing.T) {
	t.Parallel()

	h := start(t)
	h.notify("s1", "N1", "/blob/1", blob(waHistorySync.HistorySync_FULL, texts(direct, "M", 6*maxBatchMessages)))
	h.caughtUp("s1")
	h.waitedFor("s1")

	first := h.read("s1", 0, window)

	// The account logs out while the worker waits for room: its queue goes, and the worker must not write on.
	if !h.handle("s1", &events.LoggedOut{Reason: events.ConnectFailureLoggedOut}) {
		t.Fatal("the handler failed LoggedOut")
	}

	if err := h.box.Ack(h.ctx, "s1", "client", seqOf(first)); err != nil {
		t.Fatalf("Ack: %v", err)
	}

	h.idleFor("s1")

	if got, err := h.box.Read(h.ctx, "s1", seqOf(first), 100); err != nil || len(got) != 0 {
		t.Fatalf("%d events recorded after the queue was cleared (%v), want none", len(got), err)
	}

	if d := h.stubs["s1"].deleted(); len(d) != 0 {
		t.Fatalf("deleted %v, want nothing: the blob was not delivered", d)
	}
}

func TestTheBackoffDoublesToItsCapAndStartsAgainAfterASuccess(t *testing.T) {
	t.Parallel()

	h := start(t)
	broken := "CREATE TRIGGER broken BEFORE UPDATE ON history_blob BEGIN SELECT RAISE(ABORT, 'broken'); END"

	if _, err := h.db.DB.ExecContext(h.ctx, broken); err != nil {
		t.Fatalf("break the queue: %v", err)
	}

	h.notify("s1", "N1", "/blob/1", blob(waHistorySync.HistorySync_RECENT, texts(direct, "A", 1)))
	h.caughtUp("s1")

	want := []time.Duration{time.Second, 2 * time.Second, 4 * time.Second, 8 * time.Second, 16 * time.Second, 32 * time.Second, time.Minute, time.Minute}
	got := make([]time.Duration, 0, len(want))

	for len(got) < len(want) {
		select {
		case d := <-h.delays:
			got = append(got, d)
		case <-h.ctx.Done():
			t.Fatalf("backoffs %v, want %v", got, want)
		}
	}

	if !slices.Equal(got, want) {
		t.Fatalf("backoffs %v, want %v", got, want)
	}

	if _, err := h.db.DB.ExecContext(h.ctx, "DROP TRIGGER broken"); err != nil {
		t.Fatalf("repair the queue: %v", err)
	}

	h.read("s1", 0, 2)
	h.idleFor("s1")

	for len(h.delays) > 0 {
		<-h.delays
	}

	// The next failure starts at the first backoff again.
	if _, err := h.db.DB.ExecContext(h.ctx, broken); err != nil {
		t.Fatalf("break the queue: %v", err)
	}

	h.notify("s1", "N2", "/blob/2", blob(waHistorySync.HistorySync_RECENT, texts(direct, "B", 1)))

	select {
	case d := <-h.delays:
		if d != minBackoff {
			t.Fatalf("the backoff after a success %s, want %s", d, minBackoff)
		}
	case <-h.ctx.Done():
		t.Fatal("no backoff after the second failure")
	}

	if _, err := h.db.DB.ExecContext(h.ctx, "DROP TRIGGER broken"); err != nil {
		t.Fatalf("repair the queue: %v", err)
	}
}

func TestAPanickingHandlerLogsThePanicsTypeOnly(t *testing.T) {
	t.Parallel()

	var log logged

	h := startLogging(t, zerolog.New(&log), filepath.Join(t.TempDir(), "whatsmeow-net.db"))

	// A nil message: reading it panics with a runtime error, whose text a JID could be in as well.
	if h.handle("s1", (*events.Message)(nil)) {
		t.Fatal("the handler answered an event it panicked on")
	}

	h.stopNow()

	if s := log.String(); !strings.Contains(s, "runtime.") || strings.Contains(s, "nil pointer") {
		t.Fatalf("the log does not carry the panic's type only:\n%s", s)
	}
}
