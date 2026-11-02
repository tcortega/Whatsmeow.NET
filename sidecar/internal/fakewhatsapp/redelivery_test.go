//go:build fakewhatsapp

package fakewhatsapp

import (
	"context"
	"path/filepath"
	"sync"
	"testing"

	"github.com/rs/zerolog"
	"go.mau.fi/whatsmeow/types/events"

	fakev1 "github.com/tcortega/Whatsmeow.NET/sidecar/gen/whatsmeow/fake/v1"
	sidecarstore "github.com/tcortega/Whatsmeow.NET/sidecar/internal/store"
)

// withholding is a linked, connected session s1 whose handler fails the first delivery of each message id, as a
// failed outbox write would, and reports every event it saw on got.
func withholding(t *testing.T) (*WhatsApp, fakev1.FakeWhatsAppServiceClient, context.Context, *Client, chan any) {
	t.Helper()

	whatsApp, control, ctx := start(t)

	db, err := sidecarstore.Open(context.Background(), filepath.Join(t.TempDir(), "whatsmeow-net.db"), zerolog.Nop())
	if err != nil {
		t.Fatalf("open the store: %v", err)
	}

	t.Cleanup(func() {
		whatsApp.Wait()
		_ = db.Close()
	})

	var (
		mu   sync.Mutex
		seen = map[string]bool{}
	)

	got := make(chan any, 16)
	client := whatsApp.NewClient("s1")
	client.Store = db.Devices.NewDevice()
	client.AddEventHandlerWithSuccessStatus(func(evt any) bool {
		got <- evt

		msg, ok := evt.(*events.Message)
		if !ok {
			return true
		}

		mu.Lock()
		defer mu.Unlock()

		first := !seen[msg.Info.ID]
		seen[msg.Info.ID] = true

		return !first
	})

	if _, err := control.LinkDevice(ctx, &fakev1.LinkDeviceRequest{SessionId: "s1", Jid: jid}); err != nil {
		t.Fatalf("LinkDevice: %v", err)
	}

	if _, ok := (<-got).(*events.PairSuccess); !ok {
		t.Fatal("want PairSuccess first")
	}

	if err := client.ConnectContext(ctx); err != nil {
		t.Fatalf("ConnectContext: %v", err)
	}

	if evt, ok := (<-got).(*events.Connected); !ok {
		t.Fatalf("event = %v, want Connected", evt)
	}

	return whatsApp, control, ctx, client, got
}

// then is the next event the handler saw, which must be a T.
func then[T any](ctx context.Context, t *testing.T, got chan any) T {
	t.Helper()

	var want T

	select {
	case evt := <-got:
		typed, ok := evt.(T)
		if !ok {
			t.Fatalf("event = %T %v, want %T", evt, evt, want)
		}

		return typed
	case <-ctx.Done():
		t.Fatalf("no event before the test's bound, want %T", want)

		return want
	}
}

func TestAMessageLeftUnansweredIsDeliveredAgainAtTheNextLogin(t *testing.T) {
	t.Parallel()

	_, control, ctx, client, got := withholding(t)

	if _, err := control.DeliverMessage(ctx, &fakev1.DeliverMessageRequest{SessionId: "s1", Id: "M1", Chat: "1666@s.whatsapp.net", Sender: "1666@s.whatsapp.net"}); err != nil {
		t.Fatalf("DeliverMessage: %v", err)
	}

	if msg := then[*events.Message](ctx, t, got); msg.Info.ID != "M1" {
		t.Fatalf("delivered %q, want M1", msg.Info.ID)
	}

	// Another message on the same connection does not bring M1 back: only a login does (ARCHITECTURE §14, #10).
	if _, err := control.DeliverMessage(ctx, &fakev1.DeliverMessageRequest{SessionId: "s1", Id: "M2", Chat: "1666@s.whatsapp.net", Sender: "1666@s.whatsapp.net"}); err != nil {
		t.Fatalf("DeliverMessage: %v", err)
	}

	if msg := then[*events.Message](ctx, t, got); msg.Info.ID != "M2" {
		t.Fatalf("delivered %q while connected, want M2: WhatsApp delivered M1 again without a login", msg.Info.ID)
	}

	client.Disconnect()

	if err := client.ConnectContext(ctx); err != nil {
		t.Fatalf("ConnectContext: %v", err)
	}

	then[*events.Connected](ctx, t, got)

	// Both came again after the login, in the order WhatsApp sent them first; each is answered now.
	for _, want := range []string{"M1", "M2"} {
		if msg := then[*events.Message](ctx, t, got); msg.Info.ID != want {
			t.Fatalf("delivered %q again, want %q", msg.Info.ID, want)
		}
	}

	receipts := 0

	for after := uint64(0); receipts < 2; {
		sent := waitForSent(ctx, t, control, "s1", after)
		if sent.GetReceipt() != nil {
			receipts++
		}

		after = sent.GetIndex()
	}

	// Answered once: the next login delivers nothing again.
	client.Disconnect()

	if err := client.ConnectContext(ctx); err != nil {
		t.Fatalf("ConnectContext: %v", err)
	}

	then[*events.Connected](ctx, t, got)

	if _, err := control.DeliverMessage(ctx, &fakev1.DeliverMessageRequest{SessionId: "s1", Id: "M3", Chat: "1666@s.whatsapp.net", Sender: "1666@s.whatsapp.net"}); err != nil {
		t.Fatalf("DeliverMessage: %v", err)
	}

	if msg := then[*events.Message](ctx, t, got); msg.Info.ID != "M3" {
		t.Fatalf("delivered %q, want M3: an answered message came again", msg.Info.ID)
	}
}

func TestResetConnectionDropsTheConnectionThenLogsInAgainAndDeliversWhatWasUnanswered(t *testing.T) {
	t.Parallel()

	_, control, ctx, client, got := withholding(t)

	if _, err := control.DeliverMessage(ctx, &fakev1.DeliverMessageRequest{SessionId: "s1", Id: "M1", Chat: "1666@s.whatsapp.net", Sender: "1666@s.whatsapp.net"}); err != nil {
		t.Fatalf("DeliverMessage: %v", err)
	}

	then[*events.Message](ctx, t, got)

	client.ResetConnection()

	// As whatsmeow: Disconnected, a login, then the stanza WhatsApp delivers again (client.go:612-631, 653).
	then[*events.Disconnected](ctx, t, got)
	then[*events.Connected](ctx, t, got)

	if msg := then[*events.Message](ctx, t, got); msg.Info.ID != "M1" {
		t.Fatalf("delivered %q again, want M1", msg.Info.ID)
	}

	if !client.IsConnected() {
		t.Fatal("not connected after the reset")
	}

	wantSent(t, waitForSent(ctx, t, control, "s1", 0), &fakev1.Sent{Index: 1, SessionId: "s1", Kind: &fakev1.Sent_Disconnect{Disconnect: &fakev1.Disconnect{}}})

	if sent := waitForSent(ctx, t, control, "s1", 1); sent.GetReceipt().GetId() != "M1" {
		t.Fatalf("sent = %v, want the receipt for M1", sent)
	}
}

// The reconnect after a reset logs in as any connect does, so HoldNextLogin holds it too: the session stays
// reconnecting until ReleaseLogin, as one whose reconnect waits on the network.
func TestHoldNextLoginHoldsTheLoginOfAResetsReconnect(t *testing.T) {
	t.Parallel()

	whatsApp, control, ctx, client, got := withholding(t)

	if _, err := control.HoldNextLogin(ctx, &fakev1.HoldNextLoginRequest{SessionId: "s1"}); err != nil {
		t.Fatalf("HoldNextLogin: %v", err)
	}

	client.ResetConnection()
	then[*events.Disconnected](ctx, t, got)

	// Nothing dispatched off the queue is still running: the login is held.
	whatsApp.Wait()

	select {
	case evt := <-got:
		t.Fatalf("event = %T, want none until ReleaseLogin", evt)
	default:
	}

	if _, err := control.ReleaseLogin(ctx, &fakev1.ReleaseLoginRequest{SessionId: "s1"}); err != nil {
		t.Fatalf("ReleaseLogin: %v", err)
	}

	then[*events.Connected](ctx, t, got)
}

func TestResetConnectionDoesNothingWithoutAConnectionAndADisconnectStopsItsReconnect(t *testing.T) {
	t.Parallel()

	whatsApp, _, ctx, client, got := withholding(t)

	client.Disconnect()
	client.ResetConnection()

	whatsApp.Wait()

	if client.IsConnected() {
		t.Fatal("a reset without a connection connected")
	}

	select {
	case evt := <-got:
		t.Fatalf("a reset without a connection dispatched %T", evt)
	default:
	}

	// Connected again, then reset and disconnected while the reset's Disconnected handlers run: a Disconnect during
	// whatsmeow's reconnect wait cancels the reconnect (client.go:657-659).
	if err := client.ConnectContext(ctx); err != nil {
		t.Fatalf("ConnectContext: %v", err)
	}

	then[*events.Connected](ctx, t, got)

	client.mu.Lock() // holds the reset's Disconnected handlers until the Disconnect below ran
	client.ResetConnection()
	client.Disconnect()
	client.mu.Unlock()

	whatsApp.Wait()

	then[*events.Disconnected](ctx, t, got)

	if client.IsConnected() {
		t.Fatal("the reset reconnected after the Disconnect that followed it")
	}
}

func TestAMessageMarkedWithholdFirstAnswerFailsItsFirstRecordingOnly(t *testing.T) {
	t.Parallel()

	whatsApp, control, ctx := start(t)
	recorded := make(chan string, 4)
	client := whatsApp.NewClient("s1")
	client.AddEventHandlerWithSuccessStatus(whatsApp.Failing("s1", func(evt any) bool {
		if msg, ok := evt.(*events.Message); ok {
			recorded <- msg.Info.ID
		}

		return true
	}))

	req := &fakev1.DeliverMessageRequest{SessionId: "s1", Id: "M1", Chat: "1666@s.whatsapp.net", Sender: "1666@s.whatsapp.net", WithholdFirstAnswer: true}
	if _, err := control.DeliverMessage(ctx, req); err != nil {
		t.Fatalf("DeliverMessage: %v", err)
	}

	if _, err := control.DeliverMessage(ctx, &fakev1.DeliverMessageRequest{SessionId: "s1", Id: "M2", Chat: "1666@s.whatsapp.net", Sender: "1666@s.whatsapp.net"}); err != nil {
		t.Fatalf("DeliverMessage: %v", err)
	}

	// M1's first delivery failed before the handler ran, unanswered; M2, not marked, was recorded and answered.
	if id := <-recorded; id != "M2" {
		t.Fatalf("recorded %q first, want M2: the marked message's first delivery was recorded", id)
	}

	wantSent(t, waitForSent(ctx, t, control, "s1", 0), receipt(1, "s1", "M2"))

	// Its next delivery is recorded and answered.
	if _, err := control.DeliverMessage(ctx, &fakev1.DeliverMessageRequest{SessionId: "s1", Id: "M1", Chat: "1666@s.whatsapp.net", Sender: "1666@s.whatsapp.net"}); err != nil {
		t.Fatalf("DeliverMessage: %v", err)
	}

	if id := <-recorded; id != "M1" {
		t.Fatalf("recorded %q, want M1", id)
	}

	wantSent(t, waitForSent(ctx, t, control, "s1", 1), receipt(2, "s1", "M1"))
}
