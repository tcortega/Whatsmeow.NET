//go:build fakewhatsapp

package fakewhatsapp

import (
	"context"
	"errors"
	"path/filepath"
	"regexp"
	"testing"

	"github.com/rs/zerolog"
	"go.mau.fi/whatsmeow"
	"go.mau.fi/whatsmeow/types/events"

	fakev1 "github.com/tcortega/Whatsmeow.NET/sidecar/gen/whatsmeow/fake/v1"
	sidecarstore "github.com/tcortega/Whatsmeow.NET/sidecar/internal/store"
)

// qrCodeFormat is whatsmeow's QR payload: ref, then the noise key, the identity key and the ADV secret in base64,
// then the client type (pair.go:125-130).
var qrCodeFormat = regexp.MustCompile(`^https://wa\.me/settings/linked_devices#.+,[A-Za-z0-9+/]{43}=,[A-Za-z0-9+/]{43}=,[A-Za-z0-9+/]{43}=,[0-9a-z]$`)

// pairingClient returns session s1's unpaired client on a real store, its events, and its QR channel, connected.
func pairingClient(t *testing.T) (*WhatsApp, fakev1.FakeWhatsAppServiceClient, context.Context, *Client, chan any, <-chan whatsmeow.QRChannelItem) {
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

	qr, err := client.GetQRChannel(ctx)
	if err != nil {
		t.Fatalf("GetQRChannel: %v", err)
	}

	if err := client.ConnectContext(ctx); err != nil {
		t.Fatalf("ConnectContext: %v", err)
	}

	return whatsApp, control, ctx, client, got, qr
}

func wantCode(t *testing.T, item whatsmeow.QRChannelItem) string {
	t.Helper()

	if item.Event != whatsmeow.QRChannelEventCode || !qrCodeFormat.MatchString(item.Code) {
		t.Fatalf("QR item = %+v, want a code in whatsmeow's format", item)
	}

	return item.Code
}

func TestAPairingClientGetsANewQRCodeOnConnectAndOnEachRotation(t *testing.T) {
	t.Parallel()

	_, control, ctx, _, _, qr := pairingClient(t)
	first := wantCode(t, <-qr)

	if _, err := control.RotateQrCode(ctx, &fakev1.RotateQrCodeRequest{SessionId: "s1"}); err != nil {
		t.Fatalf("RotateQrCode: %v", err)
	}

	if second := wantCode(t, <-qr); second == first {
		t.Fatalf("the rotated code is the first one again: %s", second)
	}
}

func TestGetQRChannelRefusesAConnectedClient(t *testing.T) {
	t.Parallel()

	_, _, ctx, client, _, _ := pairingClient(t)

	if _, err := client.GetQRChannel(ctx); !errors.Is(err, whatsmeow.ErrQRAlreadyConnected) {
		t.Fatalf("GetQRChannel = %v, want %v", err, whatsmeow.ErrQRAlreadyConnected)
	}
}

func TestExpireQrCodesSendsTimeoutClosesTheChannelAndDisconnects(t *testing.T) {
	t.Parallel()

	_, control, ctx, client, _, qr := pairingClient(t)
	<-qr

	if _, err := control.ExpireQrCodes(ctx, &fakev1.ExpireQrCodesRequest{SessionId: "s1"}); err != nil {
		t.Fatalf("ExpireQrCodes: %v", err)
	}

	if item := <-qr; item != whatsmeow.QRChannelTimeout {
		t.Fatalf("QR item = %+v, want timeout", item)
	}

	if _, open := <-qr; open {
		t.Fatal("the QR channel stayed open")
	}

	wantSent(t, waitForSent(ctx, t, control, "s1", 0), &fakev1.Sent{Index: 1, SessionId: "s1", Kind: &fakev1.Sent_Disconnect{Disconnect: &fakev1.Disconnect{}}})

	if client.IsConnected() {
		t.Fatal("the client stayed connected")
	}
}

func TestDropConnectionDispatchesDisconnectedThenTheQRChannelTimesOut(t *testing.T) {
	t.Parallel()

	_, control, ctx, client, got, qr := pairingClient(t)
	<-qr

	if _, err := control.DropConnection(ctx, &fakev1.DropConnectionRequest{SessionId: "s1"}); err != nil {
		t.Fatalf("DropConnection: %v", err)
	}

	if evt, ok := (<-got).(*events.Disconnected); !ok {
		t.Fatalf("event = %v, want Disconnected", evt)
	}

	if item := <-qr; item != whatsmeow.QRChannelTimeout {
		t.Fatalf("QR item = %+v, want timeout", item)
	}

	if client.IsConnected() {
		t.Fatal("the client stayed connected")
	}
}

func TestLinkDeviceWhilePairingSendsSuccessThenLogsInWithThePreKeys(t *testing.T) {
	t.Parallel()

	_, control, ctx, client, got, qr := pairingClient(t)
	<-qr

	if _, err := control.LinkDevice(ctx, &fakev1.LinkDeviceRequest{SessionId: "s1", Jid: jid}); err != nil {
		t.Fatalf("LinkDevice: %v", err)
	}

	if evt, ok := (<-got).(*events.PairSuccess); !ok {
		t.Fatalf("event = %v, want PairSuccess", evt)
	}

	if item := <-qr; item != whatsmeow.QRChannelSuccess {
		t.Fatalf("QR item = %+v, want success", item)
	}

	if evt, ok := (<-got).(*events.Connected); !ok {
		t.Fatalf("event = %v, want Connected", evt)
	}

	if !client.HasUploadedPreKeys() {
		t.Fatal("the login after the link uploaded no pre-keys")
	}
}

func TestFailNextPreKeyUploadLogsInWithoutThePreKeys(t *testing.T) {
	t.Parallel()

	_, control, ctx, client, got, qr := pairingClient(t)
	<-qr

	if _, err := control.FailNextPreKeyUpload(ctx, &fakev1.FailNextPreKeyUploadRequest{SessionId: "s1"}); err != nil {
		t.Fatalf("FailNextPreKeyUpload: %v", err)
	}

	if _, err := control.LinkDevice(ctx, &fakev1.LinkDeviceRequest{SessionId: "s1", Jid: jid}); err != nil {
		t.Fatalf("LinkDevice: %v", err)
	}

	<-got

	if evt, ok := (<-got).(*events.Connected); !ok {
		t.Fatalf("event = %v, want Connected", evt)
	}

	if client.HasUploadedPreKeys() {
		t.Fatal("the pre-keys were uploaded")
	}
}

func TestPairPhoneReturnsACodeOrTheScriptedError(t *testing.T) {
	t.Parallel()

	_, control, ctx, client, _, _ := pairingClient(t)

	if _, err := client.PairPhone(ctx, "12345", true, whatsmeow.PairClientChrome, "Chrome (Linux)"); !errors.Is(err, whatsmeow.ErrPhoneNumberTooShort) {
		t.Fatalf("PairPhone with a short number = %v, want %v", err, whatsmeow.ErrPhoneNumberTooShort)
	}

	if _, err := control.FailNextPairPhone(ctx, &fakev1.FailNextPairPhoneRequest{SessionId: "s1", Code: 429}); err != nil {
		t.Fatalf("FailNextPairPhone: %v", err)
	}

	var iqErr *whatsmeow.IQError
	if _, err := client.PairPhone(ctx, "15551234567", true, whatsmeow.PairClientChrome, "Chrome (Linux)"); !errors.As(err, &iqErr) || iqErr.Code != 429 {
		t.Fatalf("PairPhone = %v, want an IQ error 429", err)
	}

	// WhatsApp's linking alphabet, four and four (pair-code.go:50, 142).
	code, err := client.PairPhone(ctx, "15551234567", true, whatsmeow.PairClientChrome, "Chrome (Linux)")
	if err != nil || !regexp.MustCompile(`^[1-9A-HJ-NP-TV-Z]{4}-[1-9A-HJ-NP-TV-Z]{4}$`).MatchString(code) {
		t.Fatalf("PairPhone = %q, %v; want a pairing code", code, err)
	}
}
