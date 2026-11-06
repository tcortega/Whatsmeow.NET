//go:build fakewhatsapp

package fakewhatsapp

import (
	"context"
	"errors"
	"testing"
	"time"

	"go.mau.fi/whatsmeow"
	"go.mau.fi/whatsmeow/appstate"
	"go.mau.fi/whatsmeow/types"
	"go.mau.fi/whatsmeow/types/events"
	"google.golang.org/protobuf/proto"
	"google.golang.org/protobuf/types/known/timestamppb"

	fakev1 "github.com/tcortega/Whatsmeow.NET/sidecar/gen/whatsmeow/fake/v1"
	"github.com/tcortega/Whatsmeow.NET/sidecar/internal/chatstate"
)

// The fake's client fits the Chat state slice's interface, as *whatsmeow.Client does.
var _ chatstate.Client = (*Client)(nil)

const chat = "5511987654321@s.whatsapp.net"

// dispatched is an event and the locks held while it was dispatched.
type dispatched struct {
	evt                   any
	onQueue, holdingState bool
}

// chatting returns session s1's client, linked (with an app state key unless withoutKey) and connected, and what it
// dispatches from then on.
func chatting(t *testing.T, withoutKey bool) (*WhatsApp, fakev1.FakeWhatsAppServiceClient, context.Context, *Client, chan dispatched) {
	t.Helper()

	whatsApp, control, ctx := start(t)

	db := openDeviceStore(t, whatsApp)

	got := make(chan dispatched, 64)
	linking := true
	client := whatsApp.NewClient("s1")
	client.Store = db.Devices.NewDevice()
	client.AddEventHandlerWithSuccessStatus(func(evt any) bool {
		if linking {
			return true
		}

		onQueue := !client.queue.TryLock()
		if !onQueue {
			client.queue.Unlock()
		}

		holdingState := !client.appStateLock.TryLock()
		if !holdingState {
			client.appStateLock.Unlock()
		}

		got <- dispatched{evt, onQueue, holdingState}

		return true
	})

	if _, err := control.LinkDevice(ctx, &fakev1.LinkDeviceRequest{SessionId: "s1", Jid: jid, WithoutAppStateKey: withoutKey}); err != nil {
		t.Fatalf("LinkDevice: %v", err)
	}

	if err := client.ConnectContext(ctx); err != nil {
		t.Fatalf("ConnectContext: %v", err)
	}

	whatsApp.Wait()

	linking = false

	return whatsApp, control, ctx, client, got
}

func nextDispatched(ctx context.Context, t *testing.T, got chan dispatched) dispatched {
	t.Helper()

	select {
	case d := <-got:
		return d
	case <-ctx.Done():
		t.Fatal("nothing was dispatched")

		return dispatched{}
	}
}

func phoneState(ctx context.Context, t *testing.T, control fakev1.FakeWhatsAppServiceClient) *fakev1.GetPhoneChatStateResponse {
	t.Helper()

	state, err := control.GetPhoneChatState(ctx, &fakev1.GetPhoneChatStateRequest{SessionId: "s1", Chat: chat})
	if err != nil {
		t.Fatalf("GetPhoneChatState: %v", err)
	}

	return state
}

func TestASentPatchIsWhatThePhoneShowsAndItsEchoIsDispatchedOffTheQueue(t *testing.T) {
	t.Parallel()

	_, control, ctx, client, got := chatting(t, false)
	until := time.UnixMilli(1_800_000_000_456)
	end := until.UnixMilli()
	target := types.NewJID("5511987654321", types.DefaultUserServer)

	// The sync after a write dispatches its change on a goroutine of its own (appstate.go:640-644), so two writes'
	// echoes come in either order: each write's echo is awaited before the next write goes out.
	echo := func(want string) dispatched {
		t.Helper()

		d := nextDispatched(ctx, t, got)
		if typeName(d.evt) != want || d.onQueue || d.holdingState {
			t.Fatalf("dispatched %#v, want %s off the queue and the app state lock", d, want)
		}

		return d
	}

	if err := client.SendAppState(ctx, appstate.BuildMuteAbs(target, true, &end)); err != nil {
		t.Fatalf("SendAppState: %v", err)
	}

	if mute := echo("*events.Mute").evt.(*events.Mute); mute.FromFullSync || mute.Action.GetMuteEndTimestamp() != end {
		t.Fatalf("echo %#v", mute)
	}

	if err := client.SendAppState(ctx, appstate.BuildStar(target, target, "M1", true, true)); err != nil {
		t.Fatalf("SendAppState: %v", err)
	}

	echo("*events.Star")

	want := &fakev1.GetPhoneChatStateResponse{
		Muted: true, MuteUntil: timestamppb.New(until),
		Starred: []*fakev1.GetPhoneChatStateResponse_StarredMessage{{MessageId: "M1", IsFromMe: true, Sender: "0"}},
	}
	if state := phoneState(ctx, t, control); !proto.Equal(state, want) {
		t.Fatalf("the phone shows %v, want %v", state, want)
	}
}

func TestWithoutAnAppStateKeyAWriteFailsBeforeItGoesOutAsWhatsmeowsDoes(t *testing.T) {
	t.Parallel()

	_, control, ctx, client, _ := chatting(t, true)

	if err := client.SendAppState(ctx, appstate.BuildPin(types.NewJID("5511987654321", types.DefaultUserServer), true)); err == nil || err.Error() != "no app state keys found, creating app state keys is not yet supported" {
		t.Fatalf("SendAppState = %v, want whatsmeow's error for no keys", err)
	}

	if state := phoneState(ctx, t, control); state.GetPinned() {
		t.Fatal("the write reached the phone")
	}
}

func TestAWriteToACorruptedCollectionIsRejectedUntilAFullSyncRepairsIt(t *testing.T) {
	t.Parallel()

	whatsApp, control, ctx, client, got := chatting(t, false)
	target := types.NewJID("5511987654321", types.DefaultUserServer)

	if err := client.SendAppState(ctx, appstate.BuildArchive(target, true, time.Time{}, nil)); err != nil {
		t.Fatalf("SendAppState: %v", err)
	}

	for range 2 {
		nextDispatched(ctx, t, got)
	}

	if _, err := control.CorruptAppState(ctx, &fakev1.CorruptAppStateRequest{SessionId: "s1", Collection: "regular_low"}); err != nil {
		t.Fatalf("CorruptAppState: %v", err)
	}

	// Rejected with a 409 whose patches do not verify, AppStateSyncError dispatched on the caller's goroutine.
	err := client.SendAppState(ctx, appstate.BuildPin(target, true))
	if !errors.Is(err, whatsmeow.ErrAppStateUpdate) || !errors.Is(err, appstate.ErrMismatchingLTHash) {
		t.Fatalf("SendAppState = %v, want a rejected update whose patches mismatch", err)
	}

	if d := nextDispatched(ctx, t, got); typeName(d.evt) != "*events.AppStateSyncError" || d.onQueue || d.holdingState {
		t.Fatalf("dispatched %#v, want AppStateSyncError on the caller's goroutine", d)
	}

	if state := phoneState(ctx, t, control); state.GetPinned() || !state.GetArchived() {
		t.Fatalf("the phone shows %v", state)
	}

	// A full sync is recorded, repairs it, and dispatches the collection's entries, then its end, off the lock.
	if err := client.FetchAppState(ctx, appstate.WAPatchRegularLow, true, false); err != nil {
		t.Fatalf("FetchAppState: %v", err)
	}

	sent, err := whatsApp.waitForSent(ctx, "s1", 0)
	if err != nil || sent.GetAppStateFullSync().GetCollection() != "regular_low" {
		t.Fatalf("sent %v, %v; want a full sync of regular_low", sent, err)
	}

	for _, want := range []string{"*events.Archive", "*events.Pin", "*events.AppStateSyncComplete"} {
		d := nextDispatched(ctx, t, got)
		if typeName(d.evt) != want || d.holdingState {
			t.Fatalf("dispatched %#v, want %s off the app state lock", d, want)
		}

		if archive, ok := d.evt.(*events.Archive); ok && !archive.FromFullSync {
			t.Fatalf("%#v is not from a full sync", archive)
		}
	}

	if err := client.SendAppState(ctx, appstate.BuildPin(target, true)); err != nil {
		t.Fatalf("SendAppState after the repair: %v", err)
	}

	if state := phoneState(ctx, t, control); !state.GetPinned() {
		t.Fatalf("the phone shows %v", state)
	}
}

func TestAnUnrecoverableCollectionFailsItsFullSyncWithAnErrorUnderTheLock(t *testing.T) {
	t.Parallel()

	_, control, ctx, client, got := chatting(t, false)

	if _, err := control.CorruptAppState(ctx, &fakev1.CorruptAppStateRequest{SessionId: "s1", Collection: "regular_high", Unrecoverable: true}); err != nil {
		t.Fatalf("CorruptAppState: %v", err)
	}

	if err := client.FetchAppState(ctx, appstate.WAPatchRegularHigh, true, false); !errors.Is(err, appstate.ErrMismatchingLTHash) {
		t.Fatalf("FetchAppState = %v, want a mismatching LTHash", err)
	}

	d := nextDispatched(ctx, t, got)
	if failed, ok := d.evt.(*events.AppStateSyncError); !ok || !failed.FullSync || !d.holdingState {
		t.Fatalf("dispatched %#v, want a full sync's AppStateSyncError under the app state lock", d)
	}

	if _, err := control.CorruptAppState(ctx, &fakev1.CorruptAppStateRequest{SessionId: "s1", Collection: "nope"}); err == nil {
		t.Fatal("CorruptAppState took a collection that does not exist")
	}
}

func TestAPhoneChangeSyncsOnTheNodeQueueAndFailsUnderTheLockWhileCorrupted(t *testing.T) {
	t.Parallel()

	_, control, ctx, _, got := chatting(t, false)
	at := time.UnixMilli(1_700_000_000_123)

	if _, err := control.SyncChatState(ctx, &fakev1.SyncChatStateRequest{
		SessionId: "s1", Chat: chat, ChangeTime: timestamppb.New(at), FullSync: true, Change: &fakev1.PhoneChatChange{Kind: &fakev1.PhoneChatChange_Pinned{Pinned: true}},
	}); err != nil {
		t.Fatalf("SyncChatState: %v", err)
	}

	d := nextDispatched(ctx, t, got)
	if pin, ok := d.evt.(*events.Pin); !ok || !d.onQueue || d.holdingState || !pin.Action.GetPinned() || !pin.FromFullSync || !pin.Timestamp.Equal(at) {
		t.Fatalf("dispatched %#v, want the full sync's Pin on the queue, off the app state lock", d)
	}

	if _, err := control.CorruptAppState(ctx, &fakev1.CorruptAppStateRequest{SessionId: "s1", Collection: "regular_low"}); err != nil {
		t.Fatalf("CorruptAppState: %v", err)
	}

	if _, err := control.SyncChatState(ctx, &fakev1.SyncChatStateRequest{
		SessionId: "s1", Chat: chat, Change: &fakev1.PhoneChatChange{Kind: &fakev1.PhoneChatChange_Read{Read: false}},
	}); err != nil {
		t.Fatalf("SyncChatState: %v", err)
	}

	d = nextDispatched(ctx, t, got)
	if failed, ok := d.evt.(*events.AppStateSyncError); !ok || failed.FullSync || !d.onQueue || !d.holdingState {
		t.Fatalf("dispatched %#v, want AppStateSyncError on the queue under the app state lock", d)
	}

	// The phone made the change all the same; only the session's copy missed it.
	if state := phoneState(ctx, t, control); !state.GetUnread() || !state.GetPinned() {
		t.Fatalf("the phone shows %v", state)
	}

	select {
	case d := <-got:
		t.Fatalf("dispatched %#v after the failed sync", d)
	default:
	}
}

func TestAHeldFullSyncIsRecordedAndWaitsUntilReleased(t *testing.T) {
	t.Parallel()

	whatsApp, control, ctx, client, _ := chatting(t, false)

	if _, err := control.HoldFullSync(ctx, &fakev1.HoldFullSyncRequest{SessionId: "s1", Hold: true}); err != nil {
		t.Fatalf("HoldFullSync: %v", err)
	}

	synced := make(chan error, 1)

	go func() { synced <- client.FetchAppState(ctx, appstate.WAPatchRegularHigh, true, false) }()

	if sent, err := whatsApp.waitForSent(ctx, "s1", 0); err != nil || sent.GetAppStateFullSync().GetCollection() != "regular_high" {
		t.Fatalf("sent %v, %v; want a full sync of regular_high", sent, err)
	}

	// Held, under the app state lock: a phone change waits for it.
	if client.appStateLock.TryLock() {
		t.Fatal("the held full sync does not hold the app state lock")
	}

	if _, err := control.HoldFullSync(ctx, &fakev1.HoldFullSyncRequest{SessionId: "s1"}); err != nil {
		t.Fatalf("HoldFullSync: %v", err)
	}

	if err := <-synced; err != nil {
		t.Fatalf("FetchAppState: %v", err)
	}
}

func typeName(evt any) string {
	switch evt.(type) {
	case *events.Mute:
		return "*events.Mute"
	case *events.Star:
		return "*events.Star"
	case *events.Pin:
		return "*events.Pin"
	case *events.Archive:
		return "*events.Archive"
	case *events.AppStateSyncError:
		return "*events.AppStateSyncError"
	case *events.AppStateSyncComplete:
		return "*events.AppStateSyncComplete"
	default:
		return "other"
	}
}
