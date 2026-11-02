//go:build fakewhatsapp

package fakewhatsapp

import (
	"context"
	"errors"
	"testing"
	"time"

	"go.mau.fi/whatsmeow"
	"go.mau.fi/whatsmeow/types"
	"go.mau.fi/whatsmeow/types/events"
	"google.golang.org/protobuf/types/known/timestamppb"

	fakev1 "github.com/tcortega/Whatsmeow.NET/sidecar/gen/whatsmeow/fake/v1"
	"github.com/tcortega/Whatsmeow.NET/sidecar/internal/presence"
)

// The fake's client fits the Presence slice's interface, as *whatsmeow.Client does.
var _ presence.Client = (*Client)(nil)

// online returns session s1's client, linked, with a push name, and connected.
func online(t *testing.T) (*WhatsApp, fakev1.FakeWhatsAppServiceClient, context.Context, *Client) {
	t.Helper()

	whatsApp, control, ctx, client, got, _ := linked(t)
	<-got

	if _, err := control.SetPushName(ctx, &fakev1.SetPushNameRequest{SessionId: "s1", Name: "Ana"}); err != nil {
		t.Fatalf("SetPushName: %v", err)
	}

	if err := client.ConnectContext(ctx); err != nil {
		t.Fatalf("ConnectContext: %v", err)
	}

	<-got

	return whatsApp, control, ctx, client
}

func TestDeliveredPresenceAndChatPresenceAreDispatchedAsWhatsmeowDoes(t *testing.T) {
	t.Parallel()

	whatsApp, control, ctx := start(t)
	got := make(chan any, 2)
	client := whatsApp.NewClient("s1")
	client.AddEventHandlerWithSuccessStatus(func(evt any) bool {
		// On the node queue, as whatsmeow dispatches both (client.go:322-323, 896-903): its lock is held.
		if client.queue.TryLock() {
			client.queue.Unlock()
			t.Errorf("%T was dispatched off the node queue", evt)
		}

		got <- evt

		return false
	})

	at := time.Unix(1_700_000_000, 0).UTC()
	deliveries := []struct {
		deliver func() error
		want    any
	}{
		{func() error {
			_, err := control.DeliverPresence(ctx, &fakev1.DeliverPresenceRequest{SessionId: "s1", From: "111@lid", Unavailable: true, LastSeen: timestamppb.New(at)})
			return err //nolint:wrapcheck // the test reads it
		}, &events.Presence{From: types.NewJID("111", types.HiddenUserServer), Unavailable: true, LastSeen: at}},
		{func() error {
			_, err := control.DeliverChatPresence(ctx, &fakev1.DeliverChatPresenceRequest{
				SessionId: "s1", Chat: "120363000000000000@g.us", Sender: "111@lid", State: "composing", Media: "audio",
			})
			return err //nolint:wrapcheck // the test reads it
		}, &events.ChatPresence{
			MessageSource: types.MessageSource{Chat: types.NewJID("120363000000000000", types.GroupServer), Sender: types.NewJID("111", types.HiddenUserServer), IsGroup: true},
			State:         types.ChatPresenceComposing, Media: types.ChatPresenceMediaAudio,
		}},
	}

	for _, d := range deliveries {
		if err := d.deliver(); err != nil {
			t.Fatalf("deliver: %v", err)
		}

		// Each returns once the handlers ran.
		select {
		case evt := <-got:
			if !sameEvent(evt, d.want) {
				t.Fatalf("event = %#v, want %#v", evt, d.want)
			}
		default:
			t.Fatalf("the delivery of %T returned before the handler ran", d.want)
		}
	}

	// whatsmeow answers neither, whatever the handlers return (presence.go:18-58): nothing was sent.
	if _, err := control.DeliverPresence(ctx, &fakev1.DeliverPresenceRequest{SessionId: "nope", From: "111@lid"}); err == nil {
		t.Fatal("DeliverPresence to a session without a client succeeded")
	}

	whatsApp.mu.Lock()
	sent := len(whatsApp.sent)
	whatsApp.mu.Unlock()

	if sent != 0 {
		t.Fatalf("the client answered a presence stanza: %d entries sent", sent)
	}
}

func TestPresenceIsSentAsWhatsmeowSendsIt(t *testing.T) {
	t.Parallel()

	_, control, ctx, client := online(t)

	user := types.NewJID("111", types.HiddenUserServer)
	calls := []error{
		client.SendPresence(ctx, types.PresenceAvailable),
		client.SubscribePresence(ctx, user),
		client.SendChatPresence(ctx, user, types.ChatPresenceComposing, types.ChatPresenceMediaAudio),
		// Media goes only with composing (presence.go:131-136).
		client.SendChatPresence(ctx, user, types.ChatPresencePaused, types.ChatPresenceMediaAudio),
	}
	for i, err := range calls {
		if err != nil {
			t.Fatalf("call %d: %v", i, err)
		}
	}

	want := []*fakev1.Sent{
		{Index: 1, SessionId: "s1", Kind: &fakev1.Sent_Presence{Presence: &fakev1.Presence{Type: "available", Name: "Ana"}}},
		{Index: 2, SessionId: "s1", Kind: &fakev1.Sent_SubscribePresence{SubscribePresence: &fakev1.SubscribePresence{To: "111@lid"}}},
		{Index: 3, SessionId: "s1", Kind: &fakev1.Sent_ChatPresence{ChatPresence: &fakev1.ChatPresence{To: "111@lid", State: "composing", Media: "audio"}}},
		{Index: 4, SessionId: "s1", Kind: &fakev1.Sent_ChatPresence{ChatPresence: &fakev1.ChatPresence{To: "111@lid", State: "paused"}}},
	}
	for i, w := range want {
		wantSent(t, waitForSent(ctx, t, control, "s1", uint64(i)), w)
	}
}

func TestPresenceFailsAsWhatsmeowsDoes(t *testing.T) {
	t.Parallel()

	_, control, ctx, client, got, db := linked(t)
	<-got

	user := types.NewJID("111", types.HiddenUserServer)

	// No push name until the app state sync sets it (presence.go:67-68), then no connection (client.go:982).
	if err := client.SendPresence(ctx, types.PresenceAvailable); !errors.Is(err, whatsmeow.ErrNoPushName) {
		t.Fatalf("SendPresence without a push name = %v, want %v", err, whatsmeow.ErrNoPushName)
	}

	if _, err := control.SetPushName(ctx, &fakev1.SetPushNameRequest{SessionId: "s1", Name: "Ana"}); err != nil {
		t.Fatalf("SetPushName: %v", err)
	}

	for i, err := range []error{
		client.SendPresence(ctx, types.PresenceAvailable),
		client.SubscribePresence(ctx, user),
		client.SendChatPresence(ctx, user, types.ChatPresenceComposing, ""),
	} {
		if !errors.Is(err, whatsmeow.ErrNotConnected) {
			t.Errorf("call %d while not connected = %v, want %v", i, err, whatsmeow.ErrNotConnected)
		}
	}

	// A device that was never linked has no own JID (tctoken.go:121-122).
	whatsApp, _, _ := start(t)
	unlinked := whatsApp.NewClient("s2")
	unlinked.Store = db.Devices.NewDevice()

	if err := unlinked.SubscribePresence(ctx, user); !errors.Is(err, whatsmeow.ErrNotLoggedIn) {
		t.Fatalf("SubscribePresence without a device = %v, want %v", err, whatsmeow.ErrNotLoggedIn)
	}
}

func TestSetPushNameSavesTheDevice(t *testing.T) {
	t.Parallel()

	_, control, ctx, client, _, db := linked(t)

	if _, err := control.SetPushName(ctx, &fakev1.SetPushNameRequest{SessionId: "s1", Name: "Ana"}); err != nil {
		t.Fatalf("SetPushName: %v", err)
	}

	device, err := db.Devices.GetDevice(ctx, *client.Store.ID)
	if err != nil || device.PushName != "Ana" {
		t.Fatalf("the saved device = %v, %v; want push name Ana", device, err)
	}
}

func TestAvailableMakesTheReceiptsActiveAndUnavailableInactiveAgain(t *testing.T) {
	t.Parallel()

	_, control, ctx, client := online(t)

	deliver := func(id string) {
		t.Helper()

		if _, err := control.DeliverMessage(ctx, &fakev1.DeliverMessageRequest{SessionId: "s1", Id: id, Chat: "111@lid", Sender: "111@lid"}); err != nil {
			t.Fatalf("DeliverMessage: %v", err)
		}
	}
	receipt := func(index uint64, id, kind string) *fakev1.Sent {
		return &fakev1.Sent{Index: index, SessionId: "s1", Kind: &fakev1.Sent_Receipt{Receipt: &fakev1.Receipt{Id: id, Type: kind}}}
	}

	deliver("M1")
	wantSent(t, waitForSent(ctx, t, control, "s1", 0), receipt(1, "M1", "inactive"))

	// whatsmeow sends active receipts from available on (presence.go:70-72), with no type (receipt.go:280-296).
	if err := client.SendPresence(ctx, types.PresenceAvailable); err != nil {
		t.Fatalf("SendPresence: %v", err)
	}

	deliver("M2")
	wantSent(t, waitForSent(ctx, t, control, "s1", 2), receipt(3, "M2", ""))

	if err := client.SendPresence(ctx, types.PresenceUnavailable); err != nil {
		t.Fatalf("SendPresence: %v", err)
	}

	deliver("M3")
	wantSent(t, waitForSent(ctx, t, control, "s1", 4), receipt(5, "M3", "inactive"))
}

// sameEvent compares the fields a presence delivery sets; a MessageSource holds a slice, so it is not comparable.
func sameEvent(got, want any) bool {
	switch w := want.(type) {
	case *events.Presence:
		g, ok := got.(*events.Presence)

		return ok && *g == *w
	case *events.ChatPresence:
		g, ok := got.(*events.ChatPresence)

		return ok && g.Chat == w.Chat && g.Sender == w.Sender && g.IsGroup == w.IsGroup && g.State == w.State && g.Media == w.Media
	default:
		return false
	}
}
