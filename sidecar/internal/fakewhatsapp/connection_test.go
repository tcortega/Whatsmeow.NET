//go:build fakewhatsapp

package fakewhatsapp

import (
	"context"
	"errors"
	"path/filepath"
	"testing"
	"time"

	"github.com/rs/zerolog"
	"go.mau.fi/whatsmeow"
	"go.mau.fi/whatsmeow/store"
	"go.mau.fi/whatsmeow/types"
	"go.mau.fi/whatsmeow/types/events"
	"google.golang.org/protobuf/types/known/durationpb"

	fakev1 "github.com/tcortega/Whatsmeow.NET/sidecar/gen/whatsmeow/fake/v1"
	"github.com/tcortega/Whatsmeow.NET/sidecar/internal/session"
	sidecarstore "github.com/tcortega/Whatsmeow.NET/sidecar/internal/store"
)

// The fake's client fits the Sessions slice's interface, as *whatsmeow.Client does.
var _ session.Client = (*Client)(nil)

const jid = "1555:1@s.whatsapp.net"

// linked returns session s1's client on a real store, its events, and the store, after LinkDevice.
func linked(t *testing.T) (*WhatsApp, fakev1.FakeWhatsAppServiceClient, context.Context, *Client, chan any, *sidecarstore.Store) {
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

	got := make(chan any, 8)
	client := whatsApp.NewClient("s1")
	client.Store = db.Devices.NewDevice()
	client.AddEventHandlerWithSuccessStatus(func(evt any) bool {
		got <- evt

		return true
	})

	if _, err := control.LinkDevice(ctx, &fakev1.LinkDeviceRequest{SessionId: "s1", Jid: jid}); err != nil {
		t.Fatalf("LinkDevice: %v", err)
	}

	return whatsApp, control, ctx, client, got, db
}

func TestLinkDeviceSavesTheDeviceThenDispatchesPairSuccess(t *testing.T) {
	t.Parallel()

	_, _, ctx, _, got, db := linked(t)
	want := types.NewADJID("1555", 0, 1)

	if evt, ok := (<-got).(*events.PairSuccess); !ok || evt.ID != want {
		t.Fatalf("event = %v, want PairSuccess for %s", evt, want)
	}

	device, err := db.Devices.GetDevice(ctx, want)
	if err != nil || device == nil {
		t.Fatalf("GetDevice = %v, %v; want the saved device", device, err)
	}
}

func TestLinkDeviceWithALidSavesItThenDispatchesItInPairSuccess(t *testing.T) {
	t.Parallel()

	whatsApp, control, ctx := start(t)
	db, err := sidecarstore.Open(context.Background(), filepath.Join(t.TempDir(), "whatsmeow-net.db"), zerolog.Nop())
	if err != nil {
		t.Fatalf("open the store: %v", err)
	}

	t.Cleanup(func() {
		whatsApp.Wait()
		_ = db.Close()
	})

	got := make(chan any, 8)
	client := whatsApp.NewClient("s1")
	client.Store = db.Devices.NewDevice()
	client.AddEventHandlerWithSuccessStatus(func(evt any) bool {
		got <- evt

		return true
	})

	if _, err := control.LinkDevice(ctx, &fakev1.LinkDeviceRequest{SessionId: "s1", Jid: jid, Lid: "2666:1@lid"}); err != nil {
		t.Fatalf("LinkDevice: %v", err)
	}

	want := types.NewADJID("2666", 0, 1)
	want.Server = types.HiddenUserServer

	if evt, ok := (<-got).(*events.PairSuccess); !ok || evt.LID != want {
		t.Fatalf("event = %v, want PairSuccess with LID %s", evt, want)
	}

	device, err := db.Devices.GetDevice(ctx, types.NewADJID("1555", 0, 1))
	if err != nil || device == nil || device.LID != want {
		t.Fatalf("GetDevice = %v, %v; want the saved device with LID %s", device, err, want)
	}
}

func TestConnectOpensTheConnectionThenWhatsAppAcceptsTheLogin(t *testing.T) {
	t.Parallel()

	_, _, ctx, client, got, _ := linked(t)
	<-got

	if err := client.ConnectContext(ctx); err != nil {
		t.Fatalf("ConnectContext: %v", err)
	}

	if evt, ok := (<-got).(*events.Connected); !ok {
		t.Fatalf("event = %v, want Connected", evt)
	}

	if err := client.ConnectContext(ctx); !errors.Is(err, whatsmeow.ErrAlreadyConnected) {
		t.Fatalf("a second ConnectContext = %v, want %v", err, whatsmeow.ErrAlreadyConnected)
	}
}

func TestHoldNextLoginHoldsConnectedUntilReleaseLoginRanItsHandlers(t *testing.T) {
	t.Parallel()

	whatsApp, control, ctx, client, got, _ := linked(t)
	<-got

	if _, err := control.HoldNextLogin(ctx, &fakev1.HoldNextLoginRequest{SessionId: "s1"}); err != nil {
		t.Fatalf("HoldNextLogin: %v", err)
	}

	if err := client.ConnectContext(ctx); err != nil {
		t.Fatalf("ConnectContext: %v", err)
	}

	// Nothing dispatched off the queue is still running: the login is held.
	whatsApp.Wait()

	if len(got) != 0 {
		t.Fatalf("event = %v, want none until ReleaseLogin", <-got)
	}

	if _, err := control.ReleaseLogin(ctx, &fakev1.ReleaseLoginRequest{SessionId: "s1"}); err != nil {
		t.Fatalf("ReleaseLogin: %v", err)
	}

	select {
	case evt := <-got:
		if _, ok := evt.(*events.Connected); !ok {
			t.Fatalf("event = %v, want Connected", evt)
		}
	default:
		t.Fatal("ReleaseLogin returned before Connected's handlers ran")
	}

	// Only the next login is held.
	client.Disconnect()

	if err := client.ConnectContext(ctx); err != nil {
		t.Fatalf("ConnectContext: %v", err)
	}

	if evt, ok := (<-got).(*events.Connected); !ok {
		t.Fatalf("event = %v, want Connected", evt)
	}
}

func TestReleaseLoginFailsWithNoLoginHeld(t *testing.T) {
	t.Parallel()

	_, control, ctx, _, got, _ := linked(t)
	<-got

	if _, err := control.ReleaseLogin(ctx, &fakev1.ReleaseLoginRequest{SessionId: "s1"}); err == nil {
		t.Fatal("ReleaseLogin with nothing held succeeded")
	}

	// Held, but no login yet: nothing to release.
	if _, err := control.HoldNextLogin(ctx, &fakev1.HoldNextLoginRequest{SessionId: "s1"}); err != nil {
		t.Fatalf("HoldNextLogin: %v", err)
	}

	if _, err := control.ReleaseLogin(ctx, &fakev1.ReleaseLoginRequest{SessionId: "s1"}); err == nil {
		t.Fatal("ReleaseLogin before the login succeeded")
	}
}

func TestFailNextConnectFailsOnlyTheNextConnect(t *testing.T) {
	t.Parallel()

	_, control, ctx, client, _, _ := linked(t)

	if _, err := control.FailNextConnect(ctx, &fakev1.FailNextConnectRequest{SessionId: "s1"}); err != nil {
		t.Fatalf("FailNextConnect: %v", err)
	}

	if err := client.ConnectContext(ctx); !errors.Is(err, errConnectFailed) {
		t.Fatalf("ConnectContext = %v, want %v", err, errConnectFailed)
	}

	if err := client.ConnectContext(ctx); err != nil {
		t.Fatalf("the connect after = %v, want nil", err)
	}
}

func TestDisconnectIsRecordedOnlyForAnOpenConnection(t *testing.T) {
	t.Parallel()

	_, control, ctx, client, _, _ := linked(t)

	client.Disconnect()

	if err := client.ConnectContext(ctx); err != nil {
		t.Fatalf("ConnectContext: %v", err)
	}

	client.Disconnect()
	client.Disconnect()

	// The first entry is the open connection's close; the next connect's close comes second.
	wantSent(t, waitForSent(ctx, t, control, "s1", 0), &fakev1.Sent{Index: 1, SessionId: "s1", Kind: &fakev1.Sent_Disconnect{Disconnect: &fakev1.Disconnect{}}})

	if err := client.ConnectContext(ctx); err != nil {
		t.Fatalf("ConnectContext: %v", err)
	}

	client.Disconnect()
	wantSent(t, waitForSent(ctx, t, control, "s1", 1), &fakev1.Sent{Index: 2, SessionId: "s1", Kind: &fakev1.Sent_Disconnect{Disconnect: &fakev1.Disconnect{}}})
}

func TestLogoutSendsTheUnlinkThenDisconnectsAndDeletesTheDevice(t *testing.T) {
	t.Parallel()

	_, control, ctx, client, _, _ := linked(t)

	if err := client.Logout(ctx); !errors.Is(err, whatsmeow.ErrNotConnected) {
		t.Fatalf("Logout before connecting = %v, want %v", err, whatsmeow.ErrNotConnected)
	}

	if err := client.ConnectContext(ctx); err != nil {
		t.Fatalf("ConnectContext: %v", err)
	}

	if err := client.Logout(ctx); err != nil {
		t.Fatalf("Logout: %v", err)
	}

	wantSent(t, waitForSent(ctx, t, control, "s1", 0),
		&fakev1.Sent{Index: 1, SessionId: "s1", Kind: &fakev1.Sent_RemoveCompanionDevice{RemoveCompanionDevice: &fakev1.RemoveCompanionDevice{}}})
	wantSent(t, waitForSent(ctx, t, control, "s1", 1), &fakev1.Sent{Index: 2, SessionId: "s1", Kind: &fakev1.Sent_Disconnect{Disconnect: &fakev1.Disconnect{}}})

	if !client.Store.Deleted {
		t.Fatal("the device was not deleted")
	}
}

func TestRaisePermanentDisconnectDispatchesEachType(t *testing.T) {
	t.Parallel()

	cases := []struct {
		name    string
		req     *fakev1.RaisePermanentDisconnectRequest
		want    events.PermanentDisconnect
		deleted bool
	}{
		{"LoggedOut", &fakev1.RaisePermanentDisconnectRequest{Kind: &fakev1.RaisePermanentDisconnectRequest_LoggedOut_{LoggedOut: &fakev1.RaisePermanentDisconnectRequest_LoggedOut{Reason: 403}}}, &events.LoggedOut{OnConnect: true, Reason: 403}, true},
		{"StreamReplaced", &fakev1.RaisePermanentDisconnectRequest{Kind: &fakev1.RaisePermanentDisconnectRequest_StreamReplaced_{StreamReplaced: &fakev1.RaisePermanentDisconnectRequest_StreamReplaced{}}}, &events.StreamReplaced{}, false},
		{"ClientOutdated", &fakev1.RaisePermanentDisconnectRequest{Kind: &fakev1.RaisePermanentDisconnectRequest_ClientOutdated_{ClientOutdated: &fakev1.RaisePermanentDisconnectRequest_ClientOutdated{}}}, &events.ClientOutdated{}, false},
		{"CATRefreshError", &fakev1.RaisePermanentDisconnectRequest{Kind: &fakev1.RaisePermanentDisconnectRequest_CatRefreshError_{CatRefreshError: &fakev1.RaisePermanentDisconnectRequest_CatRefreshError{Message: "expired"}}}, &events.CATRefreshError{Error: errors.New("expired")}, false},
		{"TemporaryBan", &fakev1.RaisePermanentDisconnectRequest{Kind: &fakev1.RaisePermanentDisconnectRequest_TemporaryBan_{TemporaryBan: &fakev1.RaisePermanentDisconnectRequest_TemporaryBan{Code: 101, Expire: durationpb.New(time.Hour)}}}, &events.TemporaryBan{Code: 101, Expire: time.Hour}, false},
		{"ConnectFailure", &fakev1.RaisePermanentDisconnectRequest{Kind: &fakev1.RaisePermanentDisconnectRequest_ConnectFailure_{ConnectFailure: &fakev1.RaisePermanentDisconnectRequest_ConnectFailure{Reason: 418, Message: "unknown"}}}, &events.ConnectFailure{Reason: 418, Message: "unknown"}, false},
	}

	for _, c := range cases {
		t.Run(c.name, func(t *testing.T) {
			t.Parallel()

			whatsApp, control, ctx, client, got, _ := linked(t)
			<-got

			if err := client.ConnectContext(ctx); err != nil {
				t.Fatalf("ConnectContext: %v", err)
			}

			<-got
			c.req.SessionId = "s1"

			if _, err := control.RaisePermanentDisconnect(ctx, c.req); err != nil {
				t.Fatalf("RaisePermanentDisconnect: %v", err)
			}

			whatsApp.Wait()

			evt := <-got
			if evt, ok := evt.(events.PermanentDisconnect); !ok || evt.PermanentDisconnectDescription() != c.want.PermanentDisconnectDescription() {
				t.Fatalf("event = %#v, want %#v", evt, c.want)
			}

			if client.Store.Deleted != c.deleted {
				t.Fatalf("device deleted = %v, want %v", client.Store.Deleted, c.deleted)
			}

			// The connection is gone: the next connect is a new one, unless the device was deleted.
			var want error
			if c.deleted {
				want = store.ErrDeviceDeleted
			}

			if err := client.ConnectContext(ctx); !errors.Is(err, want) {
				t.Fatalf("ConnectContext after = %v, want %v", err, want)
			}
		})
	}
}

func TestFailNextLogoutTimesOutOnlyTheNextLogoutAndStaysConnected(t *testing.T) {
	t.Parallel()

	_, control, ctx, client, _, _ := linked(t)

	if err := client.ConnectContext(ctx); err != nil {
		t.Fatalf("ConnectContext: %v", err)
	}

	if _, err := control.FailNextLogout(ctx, &fakev1.FailNextLogoutRequest{SessionId: "s1"}); err != nil {
		t.Fatalf("FailNextLogout: %v", err)
	}

	if err := client.Logout(ctx); !errors.Is(err, whatsmeow.ErrIQTimedOut) || !client.IsConnected() || client.Store.Deleted {
		t.Fatalf("Logout = %v, connected %v, deleted %v; want %v, still connected, the device kept", err, client.IsConnected(), client.Store.Deleted, whatsmeow.ErrIQTimedOut)
	}

	if err := client.Logout(ctx); err != nil {
		t.Fatalf("the logout after = %v, want nil", err)
	}
}
