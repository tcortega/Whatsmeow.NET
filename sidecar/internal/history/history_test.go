package history

import (
	"compress/zlib"
	"context"
	"fmt"
	"path/filepath"
	"slices"
	"strings"
	"testing"
	"time"

	"github.com/rs/zerolog"
	"go.mau.fi/whatsmeow"
	"go.mau.fi/whatsmeow/proto/waE2E"
	"go.mau.fi/whatsmeow/proto/waHistorySync"
	"go.mau.fi/whatsmeow/types"
	"go.mau.fi/whatsmeow/types/events"
	"google.golang.org/protobuf/proto"
	"google.golang.org/protobuf/types/known/timestamppb"

	whatsmeowv1 "github.com/tcortega/Whatsmeow.NET/sidecar/gen/whatsmeow/v1"
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
	if q := h.queueRows("s1"); !slices.Equal(q, []string{fmt.Sprintf("N1 %d 0 0", window)}) {
		t.Fatalf("queue %v, want N1 with %d batches done", q, window)
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

	if _, err := h.db.DB.ExecContext(h.ctx, "INSERT INTO history_blob (session_id, message_id, notification, batches_done, recorded) VALUES ('s1', 'N1', ?, 1, 1)", notif); err != nil {
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

	// The worker stops without counting it, and the next connection's worker delivers it.
	h.ended("s1")

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

	batches, skipped := batch(whatsmeowv1.HistorySyncType_HISTORY_SYNC_TYPE_FULL, messages)

	var (
		ids   []string
		sizes []int
	)

	for _, b := range batches {
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

	if !slices.Equal(ids, want) || skipped != 1 {
		t.Fatalf("batched %d messages, skipped %d; want every one but the one too large for a gRPC message, in order", len(ids), skipped)
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
		if got := reason(err); got != c.reason {
			t.Errorf("%s: %v, want %s", c.name, err, c.reason)
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
