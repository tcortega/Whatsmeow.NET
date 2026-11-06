package session

import (
	"context"
	"errors"
	"fmt"
	"io"
	"runtime"
	"sync"
	"testing"
	"time"

	"go.mau.fi/whatsmeow"
	"go.mau.fi/whatsmeow/proto/waAdv"
	"go.mau.fi/whatsmeow/store"
	"go.mau.fi/whatsmeow/types"
	"go.mau.fi/whatsmeow/types/events"
	"google.golang.org/grpc/codes"
	"google.golang.org/grpc/status"
	"google.golang.org/protobuf/proto"

	whatsmeowv1 "github.com/tcortega/Whatsmeow.NET/sidecar/gen/whatsmeow/v1"
	"github.com/tcortega/Whatsmeow.NET/sidecar/internal/server"
)

// pairStub is what a stub's pairing calls see and did. The stub's mu guards it.
type pairStub struct {
	qr        chan whatsmeow.QRChannelItem
	qrCtx     context.Context //nolint:containedctx // the context GetQRChannel got, to check whose it is
	phones    []phoneCall
	phoneErr  error
	uploaded  bool
	phoneCode string
}

// phoneCall is one PairPhone call.
type phoneCall struct {
	phone      string
	push       bool
	clientType whatsmeow.PairClientType
	name       string
}

// GetQRChannel opens the channel the test sends the QR channel's items on, as whatsmeow's GetQRChannel would
// (qrchan.go:221-239).
func (s *stub) GetQRChannel(ctx context.Context) (<-chan whatsmeow.QRChannelItem, error) {
	s.mu.Lock()
	defer s.mu.Unlock()

	s.pairing.qr = make(chan whatsmeow.QRChannelItem, 8)
	s.pairing.qrCtx = ctx

	return s.pairing.qr, nil
}

func (s *stub) PairPhone(_ context.Context, phone string, push bool, clientType whatsmeow.PairClientType, name string) (string, error) {
	s.mu.Lock()
	defer s.mu.Unlock()

	s.pairing.phones = append(s.pairing.phones, phoneCall{phone, push, clientType, name})

	return s.pairing.phoneCode, s.pairing.phoneErr
}

// HasUploadedPreKeys says whether the stub's first pre-key upload happened, as whatsmeow's PreKeysUploaded does.
func (s *stub) HasUploadedPreKeys() bool {
	s.mu.Lock()
	defer s.mu.Unlock()

	return s.pairing.uploaded
}

func (s *stub) send(item whatsmeow.QRChannelItem) { s.qrChannel() <- item }

func (s *stub) qrChannel() chan whatsmeow.QRChannelItem {
	s.mu.Lock()
	defer s.mu.Unlock()

	return s.pairing.qr
}

func (s *stub) disconnectCount() int {
	s.mu.Lock()
	defer s.mu.Unlock()

	return s.disconnects
}

// nextDisconnect returns a channel the client's next Disconnect closes.
func (s *stub) nextDisconnect() <-chan struct{} {
	done := make(chan struct{})
	once := sync.Once{}

	s.mu.Lock()
	s.disconnecting = func() { once.Do(func() { close(done) }) }
	s.mu.Unlock()

	return done
}

// await waits for done, at most the test's bound.
func (h *harness) await(done <-chan struct{}, what string) {
	h.t.Helper()

	select {
	case <-done:
	case <-h.ctx.Done():
		h.t.Fatalf("%s never happened", what)
	}
}

func (s *stub) phoneCalls() []phoneCall {
	s.mu.Lock()
	defer s.mu.Unlock()

	return s.pairing.phones
}

// linkAs does what whatsmeow does when the phone links the device (pair.go:222-240, 157); the QR channel's
// success is the test's to send.
func (s *stub) linkAs(t *testing.T, jid, lid string) {
	t.Helper()

	parsed, err := types.ParseJID(jid)
	if err != nil {
		t.Fatalf("jid: %v", err)
	}

	parsedLID, err := types.ParseJID(lid)
	if err != nil {
		t.Fatalf("lid: %v", err)
	}

	s.device.ID = &parsed
	s.device.LID = parsedLID
	s.device.Account = &waAdv.ADVSignedDeviceIdentity{
		Details: []byte{}, AccountSignature: make([]byte, 64), AccountSignatureKey: make([]byte, 32), DeviceSignature: make([]byte, 64),
	}

	if err := s.device.Save(context.Background()); err != nil {
		t.Fatalf("save the device: %v", err)
	}

	s.emit(&events.PairSuccess{ID: parsed, LID: parsedLID})
}

// logIn is whatsmeow's login after the link: the pre-key upload, then Connected (connectionevents.go:187-204).
func (s *stub) logIn(uploaded bool) {
	s.mu.Lock()
	s.pairing.uploaded = uploaded
	s.mu.Unlock()

	s.emit(&events.Connected{})
}

// pair opens a Pair stream; cancel ends the call.
func (h *harness) pair(id, phone string) (whatsmeowv1.SessionService_PairClient, context.CancelFunc) {
	h.t.Helper()

	ctx, cancel := context.WithCancel(h.ctx)
	h.t.Cleanup(cancel)

	req := &whatsmeowv1.PairRequest{SessionId: id}
	if phone != "" {
		req.Phone = &whatsmeowv1.PairRequest_Phone{PhoneNumber: phone}
	}

	stream, err := h.client.Pair(ctx, req)
	if err != nil {
		h.t.Fatalf("Pair(%q): %v", id, err)
	}

	return stream, cancel
}

// pairing waits until the Pair call made the session's new client and opened its QR channel, and returns that client.
func (h *harness) pairing(id string, before *stub) *stub {
	h.t.Helper()

	for {
		if s := h.stub(id); s != before && s.qrChannel() != nil {
			return s
		}

		if h.ctx.Err() != nil {
			h.t.Fatalf("session %q never opened a QR channel", id)
		}

		runtime.Gosched()
	}
}

func recv(t *testing.T, stream whatsmeowv1.SessionService_PairClient) *whatsmeowv1.PairResponse {
	t.Helper()

	resp, err := stream.Recv()
	if err != nil {
		t.Fatalf("Pair: %v", err)
	}

	return resp
}

// end reads the stream to its end: nil when it ended with OK.
func end(t *testing.T, stream whatsmeowv1.SessionService_PairClient) error {
	t.Helper()

	for {
		resp, err := stream.Recv()
		if errors.Is(err, io.EOF) {
			return nil
		}

		if err != nil {
			return fmt.Errorf("pair: %w", err)
		}

		t.Fatalf("Pair sent %v, want the end of the stream", resp)
	}
}

func wantStep(t *testing.T, got, want *whatsmeowv1.PairResponse) {
	t.Helper()

	if !proto.Equal(got, want) {
		t.Fatalf("Pair sent %v, want %v", got, want)
	}
}

// awaitState watches the session until it is in state.
func (h *harness) awaitState(id string, want *whatsmeowv1.SessionState) {
	h.t.Helper()

	watch, err := h.client.WatchSessionState(h.ctx, &whatsmeowv1.WatchSessionStateRequest{SessionId: id})
	if err != nil {
		h.t.Fatalf("WatchSessionState: %v", err)
	}

	for {
		resp, err := watch.Recv()
		if err != nil {
			h.t.Fatalf("session %q never reached %v: %v", id, want, err)
		}

		if proto.Equal(resp.GetState(), want) {
			return
		}
	}
}

func code(c string) whatsmeow.QRChannelItem {
	return whatsmeow.QRChannelItem{Event: whatsmeow.QRChannelEventCode, Code: c, Timeout: time.Minute}
}

func qrStep(c string) *whatsmeowv1.PairResponse {
	return &whatsmeowv1.PairResponse{Step: &whatsmeowv1.PairResponse_QrCode_{QrCode: &whatsmeowv1.PairResponse_QrCode{Code: c}}}
}

func successStep(jid, lid string) *whatsmeowv1.PairResponse {
	return &whatsmeowv1.PairResponse{Step: &whatsmeowv1.PairResponse_Success_{Success: &whatsmeowv1.PairResponse_Success{Jid: jid, Lid: lid}}}
}

const (
	pairedJID = "1555:7@s.whatsapp.net"
	pairedLID = "999:7@lid"
)

func TestPairStreamsEachQRCodeThenSuccessOnceLoggedInWithPreKeys(t *testing.T) {
	t.Parallel()

	h := start(t)
	h.create("s1")
	stream, _ := h.pair("s1", "")
	client := h.pairing("s1", nil)

	client.send(code("c1"))
	wantStep(t, recv(t, stream), qrStep("c1"))
	h.wantState("s1", pairing())

	client.send(code("c2"))
	wantStep(t, recv(t, stream), qrStep("c2"))

	client.linkAs(t, pairedJID, pairedLID)
	client.send(whatsmeow.QRChannelSuccess)
	// Linked, not logged in yet: still pairing (session.proto Pair).
	h.wantState("s1", pairing())

	client.logIn(true)
	wantStep(t, recv(t, stream), successStep(pairedJID, pairedLID))
	// Connected before the success was sent.
	h.wantState("s1", connected())

	if err := end(t, stream); err != nil {
		t.Fatalf("Pair ended with %v, want OK", err)
	}

	if client.disconnectCount() != 0 {
		t.Fatal("a successful pairing disconnected the client")
	}
}

func TestPairFailsWhenThePreKeysWereNotUploadedAndTheSessionStaysPaired(t *testing.T) {
	t.Parallel()

	h := start(t)
	h.create("s1")
	stream, _ := h.pair("s1", "")
	client := h.pairing("s1", nil)

	client.linkAs(t, pairedJID, pairedLID)
	client.send(whatsmeow.QRChannelSuccess)
	client.logIn(false)

	wantReason(t, end(t, stream), codes.Aborted, "PAIRING_FAILED", nil)
	h.wantState("s1", disconnected())

	if client.disconnectCount() == 0 {
		t.Fatal("the client was not disconnected")
	}

	// Paired: the restart finds the device by the recorded JID.
	h.stop()
	startOn(t, h.path).wantState("s1", disconnected())
}

// The login after the link dispatches Connected after the pre-key upload (connectionevents.go:187-204), so the offline
// sync can come first.
func TestAnOfflineSyncBeforeTheLoginAfterTheLinkMakesTheSessionCaughtUpOnceConnected(t *testing.T) {
	t.Parallel()

	h := start(t)
	h.create("s1")
	stream, _ := h.pair("s1", "")
	client := h.pairing("s1", nil)

	client.linkAs(t, pairedJID, pairedLID)
	client.send(whatsmeow.QRChannelSuccess)
	client.emit(&events.OfflineSyncCompleted{})
	h.wantState("s1", pairing())

	client.logIn(true)
	wantStep(t, recv(t, stream), successStep(pairedJID, pairedLID))

	if err := end(t, stream); err != nil {
		t.Fatalf("Pair ended with %v, want OK", err)
	}

	h.wantRecorded("s1", pairing(), connected(), caughtUp())
}

func TestAnEarlyOfflineSyncIsForgottenWhenTheLoginAfterTheLinkFails(t *testing.T) {
	t.Parallel()

	h := start(t)
	h.create("s1")
	stream, _ := h.pair("s1", "")
	client := h.pairing("s1", nil)

	client.linkAs(t, pairedJID, pairedLID)
	client.send(whatsmeow.QRChannelSuccess)
	client.emit(&events.OfflineSyncCompleted{})
	client.logIn(false)
	wantReason(t, end(t, stream), codes.Aborted, "PAIRING_FAILED", nil)

	h.connect("s1")
	client.emit(&events.Connected{})

	h.wantRecorded("s1", pairing(), disconnected(), connecting(), connected())
}

func TestPairFailsWhenNoLoginComesInTime(t *testing.T) {
	t.Parallel()

	h := start(t)
	h.svc.linkTimeout = 10 * time.Millisecond
	h.create("s1")
	stream, _ := h.pair("s1", "")
	client := h.pairing("s1", nil)

	client.linkAs(t, pairedJID, pairedLID)
	client.send(whatsmeow.QRChannelSuccess)

	wantReason(t, end(t, stream), codes.Aborted, "PAIRING_FAILED", nil)
	h.wantState("s1", disconnected())

	if client.disconnectCount() == 0 {
		t.Fatal("the client was not disconnected")
	}
}

func TestAStoppingStateAfterTheLinkEndsPairingInThatState(t *testing.T) {
	t.Parallel()

	h := start(t)
	h.create("s1")
	stream, _ := h.pair("s1", "")
	client := h.pairing("s1", nil)

	client.linkAs(t, pairedJID, pairedLID)
	client.send(whatsmeow.QRChannelSuccess)
	client.emit(&events.TemporaryBan{Code: 101})

	wantReason(t, end(t, stream), codes.Aborted, "PAIRING_FAILED", nil)
	h.wantState("s1", tempBanned(101, nil))
}

func TestPairEndsWithTheReasonOfEachFinalQRItem(t *testing.T) {
	t.Parallel()

	cases := []struct {
		name    string
		dropped bool
		item    *whatsmeow.QRChannelItem
		code    codes.Code
		reason  string
	}{
		{"codes ran out", false, &whatsmeow.QRChannelTimeout, codes.Aborted, "PAIRING_TIMED_OUT"},
		{"connection dropped", true, &whatsmeow.QRChannelTimeout, codes.Unavailable, "CONNECTION_FAILED"},
		{"pair error", false, &whatsmeow.QRChannelItem{Event: whatsmeow.QRChannelEventError, Error: errors.New("hmac")}, codes.Aborted, "PAIRING_FAILED"},
		{"passkey request", false, &whatsmeow.QRChannelItem{Event: whatsmeow.QRChannelEventPasskeyRequest}, codes.Aborted, "PAIRING_FAILED"},
		{"passkey confirmation", false, &whatsmeow.QRChannelItem{Event: whatsmeow.QRChannelEventPasskeyResponse}, codes.Aborted, "PAIRING_FAILED"},
		{"client outdated", false, &whatsmeow.QRChannelClientOutdated, codes.Aborted, "PAIRING_FAILED"},
		{"unexpected state", false, &whatsmeow.QRChannelErrUnexpectedEvent, codes.Aborted, "PAIRING_FAILED"},
		{"scanned without multidevice", false, &whatsmeow.QRChannelScannedWithoutMultidevice, codes.Aborted, "PAIRING_FAILED"},
		{"closed without an item", false, nil, codes.Aborted, "PAIRING_FAILED"},
	}

	h := start(t)

	for i, c := range cases {
		id := "s" + string(rune('a'+i))
		h.create(id)
		stream, _ := h.pair(id, "")
		client := h.pairing(id, nil)

		if c.dropped {
			client.emit(&events.Disconnected{})
		}

		if c.item != nil {
			client.send(*c.item)
		} else {
			close(client.qrChannel())
		}

		err := end(t, stream)
		t.Run(c.name, func(t *testing.T) { wantReason(t, err, c.code, c.reason, nil) })
		h.awaitState(id, created())
	}
}

func TestAPermanentDisconnectBeforeTheLinkLeavesTheSessionUnpaired(t *testing.T) {
	t.Parallel()

	h := start(t)
	h.create("s1")
	stream, _ := h.pair("s1", "")
	client := h.pairing("s1", nil)

	client.emit(&events.ClientOutdated{})
	client.send(whatsmeow.QRChannelClientOutdated)

	wantReason(t, end(t, stream), codes.Aborted, "PAIRING_FAILED", nil)
	h.awaitState("s1", created())
}

func TestCancellingBeforeTheLinkDisconnectsAndRestoresThePreviousState(t *testing.T) {
	t.Parallel()

	for _, previous := range []*whatsmeowv1.SessionState{created(), loggedOut(401)} {
		h := start(t)
		h.create("s1")
		h.setState("s1", previous)
		stream, cancel := h.pair("s1", "")
		client := h.pairing("s1", nil)

		client.send(code("c1"))
		wantStep(t, recv(t, stream), qrStep("c1"))
		disconnected := client.nextDisconnect()
		cancel()

		if got := status.Code(end(t, stream)); got != codes.Canceled {
			t.Fatalf("Pair ended with %v, want Canceled", got)
		}

		h.awaitState("s1", previous)
		h.await(disconnected, "the disconnect of the cancelled pairing")
	}
}

func TestCancellingAfterTheLinkNeverDisconnectsAndTheLoginGoesOn(t *testing.T) {
	t.Parallel()

	h := start(t)
	h.create("s1")
	stream, cancel := h.pair("s1", "")
	client := h.pairing("s1", nil)

	client.linkAs(t, pairedJID, pairedLID)
	cancel()

	if got := status.Code(end(t, stream)); got != codes.Canceled {
		t.Fatalf("Pair ended with %v, want Canceled", got)
	}

	client.send(whatsmeow.QRChannelSuccess)
	client.logIn(true)
	h.awaitState("s1", connected())

	if client.disconnectCount() != 0 {
		t.Fatal("a cancel after the link disconnected the client")
	}
}

func TestPairUsesTheSidecarsLifetimeNotTheCall(t *testing.T) {
	t.Parallel()

	h := start(t)
	h.create("s1")
	stream, cancel := h.pair("s1", "")
	client := h.pairing("s1", nil)
	client.send(code("c1"))
	wantStep(t, recv(t, stream), qrStep("c1"))
	cancel()
	_ = end(t, stream)
	h.awaitState("s1", created())

	client.mu.Lock()
	qrCtx, connectCtxs := client.pairing.qrCtx, client.connectCtxs
	client.mu.Unlock()

	// whatsmeow's QR channel disconnects when its context ends (qrchan.go:125-131), which would race the 515
	// reconnect after a link; the connection outlives the call too (client.go:543-601).
	if qrCtx.Err() != nil || len(connectCtxs) != 1 || connectCtxs[0].Err() != nil {
		t.Fatal("the QR channel or the connection got the call's context")
	}
}

func TestPairByPhoneSendsOnePairingCodeAfterTheFirstQRCode(t *testing.T) {
	t.Parallel()

	h := start(t)
	h.create("s1")
	stream, _ := h.pair("s1", "+1 (555) 123-4567")
	client := h.pairing("s1", nil)
	client.mu.Lock()
	client.pairing.phoneCode = "ABCD-EFGH"
	client.mu.Unlock()

	client.send(code("c1"))
	wantStep(t, recv(t, stream), &whatsmeowv1.PairResponse{
		Step: &whatsmeowv1.PairResponse_PairingCode_{PairingCode: &whatsmeowv1.PairResponse_PairingCode{Code: "ABCD-EFGH"}},
	})

	want := []phoneCall{{"15551234567", true, whatsmeow.PairClientChrome, "Chrome (Linux)"}}
	if got := client.phoneCalls(); len(got) != 1 || got[0] != want[0] {
		t.Fatalf("PairPhone calls = %v, want %v", got, want)
	}

	// The next code is neither streamed nor a new pairing code: the next item is the success.
	client.send(code("c2"))
	client.linkAs(t, pairedJID, pairedLID)
	client.send(whatsmeow.QRChannelSuccess)
	client.logIn(true)
	wantStep(t, recv(t, stream), successStep(pairedJID, pairedLID))

	if got := client.phoneCalls(); len(got) != 1 {
		t.Fatalf("PairPhone was called %d times, want once", len(got))
	}
}

func TestAnInvalidPhoneNumberIsInvalidArgumentAndConnectsNothing(t *testing.T) {
	t.Parallel()

	h := start(t)

	for _, phone := range []string{"+12 345", "0555 123 4567"} {
		h.create("s1")
		before := h.stub("s1")
		stream, _ := h.pair("s1", phone)

		wantReason(t, end(t, stream), codes.InvalidArgument, "INVALID_ARGUMENT", map[string]string{"field": "phone.phone_number"})

		if h.stub("s1") != before || before.connects() != 0 {
			t.Fatalf("%q: the Pair call made a client or connected", phone)
		}

		h.wantState("s1", created())
	}
}

func TestPairPhoneErrors(t *testing.T) {
	t.Parallel()

	cases := []struct {
		err      error
		code     codes.Code
		reason   string
		metadata map[string]string
	}{
		{whatsmeow.ErrIQRateOverLimit, codes.ResourceExhausted, "RATE_LIMITED", nil},
		{&whatsmeow.IQError{Code: 400, Text: "bad-request"}, codes.FailedPrecondition, "WHATSAPP_ERROR", map[string]string{"code": "400"}},
		{errors.New("element missing"), codes.Aborted, "PAIRING_FAILED", nil},
	}

	h := start(t)

	for i, c := range cases {
		id := "s" + string(rune('a'+i))
		h.create(id)
		stream, _ := h.pair(id, "+15551234567")
		client := h.pairing(id, nil)
		client.mu.Lock()
		client.pairing.phoneErr = c.err
		client.mu.Unlock()

		client.send(code("c1"))
		wantReason(t, end(t, stream), c.code, c.reason, c.metadata)
		h.awaitState(id, created())
	}
}

func TestPairPreconditions(t *testing.T) {
	t.Parallel()

	h := start(t)

	paired := []*whatsmeowv1.SessionState{
		disconnected(), connecting(), connected(), caughtUp(), reconnecting(), replaced(), tempBanned(101, nil), outdated(), failed(1, "x"),
	}

	for _, state := range paired {
		h.create("s1")
		h.setState("s1", state)
		before := h.stub("s1")
		stream, _ := h.pair("s1", "")

		wantReason(t, end(t, stream), codes.FailedPrecondition, "ALREADY_PAIRED", nil)

		if h.stub("s1") != before {
			t.Fatalf("%v: the Pair call made a new client", state)
		}

		h.wantState("s1", state)
	}

	h.create("s2")
	first, _ := h.pair("s2", "")
	client := h.pairing("s2", nil)

	second, _ := h.pair("s2", "")
	wantReason(t, end(t, second), codes.FailedPrecondition, "PAIRING_IN_PROGRESS", nil)

	_, err := h.client.Connect(h.ctx, &whatsmeowv1.ConnectRequest{SessionId: "s2"})
	wantReason(t, err, codes.FailedPrecondition, "PAIRING_IN_PROGRESS", nil)

	_, err = h.client.Disconnect(h.ctx, &whatsmeowv1.DisconnectRequest{SessionId: "s2"})
	wantReason(t, err, codes.FailedPrecondition, "PAIRING_IN_PROGRESS", nil)

	// The first pairing goes on.
	client.send(code("c1"))
	wantStep(t, recv(t, first), qrStep("c1"))
}

func TestEachPairingGetsANewClientAndTheOldOnesAreIgnored(t *testing.T) {
	t.Parallel()

	h := start(t)
	h.create("s1")
	stream, cancel := h.pair("s1", "")
	old := h.pairing("s1", nil)
	cancel()
	_ = end(t, stream)
	h.awaitState("s1", created())

	h.svc.linkTimeout = 10 * time.Millisecond
	stream, _ = h.pair("s1", "")
	client := h.pairing("s1", old)

	// A late link from the replaced client is not this pairing's: its codes still run out.
	old.linkAs(t, pairedJID, pairedLID)
	client.send(code("c1"))
	wantStep(t, recv(t, stream), qrStep("c1"))
	client.send(whatsmeow.QRChannelTimeout)

	wantReason(t, end(t, stream), codes.Aborted, "PAIRING_TIMED_OUT", nil)
}

func TestDeleteWhilePairingDisconnectsAndEndsThePairing(t *testing.T) {
	t.Parallel()

	h := start(t)
	h.create("s1")
	stream, _ := h.pair("s1", "")
	client := h.pairing("s1", nil)

	if _, err := h.client.DeleteSession(h.ctx, &whatsmeowv1.DeleteSessionRequest{SessionId: "s1"}); err != nil {
		t.Fatalf("DeleteSession: %v", err)
	}

	if client.disconnectCount() == 0 {
		t.Fatal("Delete did not disconnect the pairing client")
	}

	wantReason(t, end(t, stream), codes.NotFound, "SESSION_NOT_FOUND", nil)
}

func TestARestartWhileWaitingForTheLoginComesBackDisconnected(t *testing.T) {
	t.Parallel()

	h := start(t)
	h.create("s1")
	h.create("s2")
	stream, _ := h.pair("s1", "")
	client := h.pairing("s1", nil)
	client.linkAs(t, pairedJID, pairedLID)
	client.send(whatsmeow.QRChannelSuccess)

	unlinked, _ := h.pair("s2", "")
	h.pairing("s2", nil)

	// Close ends the wait for the login and returns; nothing waits for the timeout.
	h.stop()
	_ = end(t, stream)
	_ = end(t, unlinked)

	restarted := startOn(t, h.path)
	restarted.wantState("s1", disconnected())
	restarted.wantState("s2", created())
}

func TestAFailedConnectEndsPairingWithConnectionFailed(t *testing.T) {
	t.Parallel()

	h := start(t)
	makeClient := h.svc.newClient
	h.svc.newClient = func(id string, device *store.Device, withheld func()) Client {
		c := makeClient(id, device, withheld)
		c.(*stub).connectErr = errors.New("websocket: bad handshake") //nolint:forcetypeassert,err113 // the harness's stub; whatsmeow's error

		return c
	}

	h.create("s1")
	stream, _ := h.pair("s1", "")

	wantReason(t, end(t, stream), codes.Unavailable, "CONNECTION_FAILED", nil)
	h.wantState("s1", created())

	if h.stub("s1").disconnectCount() == 0 {
		t.Fatal("the client was not disconnected")
	}
}

func TestALinkDuringShutdownIsStillRecorded(t *testing.T) {
	t.Parallel()

	h := start(t)
	h.create("s1")
	_, _ = h.pair("s1", "")
	client := h.pairing("s1", nil)

	// Close ends the lifetime first (session.go Close); whatsmeow may still dispatch the link it saved.
	h.svc.end()
	client.linkAs(t, pairedJID, pairedLID)

	h.stop()
	startOn(t, h.path).wantState("s1", disconnected())
}

func TestEventsAfterTheLoginEndedAreTheSessionsAgain(t *testing.T) {
	t.Parallel()

	h := start(t)
	h.create("s1")
	_, _ = h.pair("s1", "")
	client := h.pairing("s1", nil)
	client.linkAs(t, pairedJID, pairedLID)
	client.send(whatsmeow.QRChannelSuccess)

	// Holding the session's ops keeps the wait for the login from finishing, so the pairing is still open when the
	// next events come.
	h.svc.mu.Lock()
	s := h.svc.sessions["s1"]
	h.svc.mu.Unlock()
	s.ops.Lock()

	release := sync.OnceFunc(s.ops.Unlock)
	t.Cleanup(release) // before the harness's stop, which takes ops

	client.logIn(true)
	h.wantState("s1", connected())

	client.emit(&events.Disconnected{})
	h.wantState("s1", reconnecting())
	client.emit(&events.LoggedOut{Reason: events.ConnectFailureLoggedOut})
	h.wantState("s1", loggedOut(401))
	release()
}

func TestAPermanentDisconnectTheQRChannelIgnoresEndsPairingBeforeTheLink(t *testing.T) {
	t.Parallel()

	h := start(t)

	// whatsmeow's QR channel ends on ClientOutdated, ConnectFailure, LoggedOut and TemporaryBan (qrchan.go:187-199),
	// not on these.
	for i, evt := range []events.PermanentDisconnect{&events.StreamReplaced{}, &events.CATRefreshError{Error: errors.New("refresh")}} {
		id := "s" + string(rune('a'+i))
		h.create(id)
		stream, _ := h.pair(id, "")
		client := h.pairing(id, nil)

		client.emit(evt)

		wantReason(t, end(t, stream), codes.Aborted, "PAIRING_FAILED", nil)
		h.awaitState(id, created())

		if client.disconnectCount() == 0 {
			t.Fatalf("%T: the client was not disconnected", evt)
		}
	}
}

func TestDeleteAfterTheLinkEndsTheWaitForTheLogin(t *testing.T) {
	t.Parallel()

	h := start(t)
	h.create("s1")
	stream, _ := h.pair("s1", "")
	client := h.pairing("s1", nil)
	client.linkAs(t, pairedJID, pairedLID)
	client.send(whatsmeow.QRChannelSuccess)

	if _, err := h.client.DeleteSession(h.ctx, &whatsmeowv1.DeleteSessionRequest{SessionId: "s1"}); err != nil {
		t.Fatalf("DeleteSession: %v", err)
	}

	// Well before the 60 s bound, which is longer than the test's own.
	wantReason(t, end(t, stream), codes.NotFound, "SESSION_NOT_FOUND", nil)
}

func TestALinkThatComesAfterACancelIsRecorded(t *testing.T) {
	t.Parallel()

	h := start(t)
	h.create("s1")
	stream, cancel := h.pair("s1", "")
	client := h.pairing("s1", nil)
	client.send(code("c1"))
	wantStep(t, recv(t, stream), qrStep("c1"))
	cancel()
	_ = end(t, stream)
	h.awaitState("s1", created())

	// whatsmeow saved the device and dispatches PairSuccess after all (pair.go:148-158): the session is paired.
	client.linkAs(t, pairedJID, pairedLID)
	h.wantState("s1", disconnected())
}

func TestALinkDuringTheCancelsDisconnectIsRecorded(t *testing.T) {
	t.Parallel()

	h := start(t)
	h.create("s1")
	stream, cancel := h.pair("s1", "")
	client := h.pairing("s1", nil)
	client.send(code("c1"))
	wantStep(t, recv(t, stream), qrStep("c1"))

	parsed, err := types.ParseJID(pairedJID)
	if err != nil {
		t.Fatalf("jid: %v", err)
	}

	client.mu.Lock()
	client.disconnecting = func() {
		// What whatsmeow does if the phone linked the device just as the call was cancelled (pair.go:148-158).
		client.device.ID = &parsed
		client.device.Account = &waAdv.ADVSignedDeviceIdentity{
			Details: []byte{}, AccountSignature: make([]byte, 64), AccountSignatureKey: make([]byte, 32), DeviceSignature: make([]byte, 64),
		}

		if err := client.device.Save(context.Background()); err != nil {
			t.Errorf("save the device: %v", err)
		}

		client.emit(&events.PairSuccess{ID: parsed})
	}
	client.mu.Unlock()

	cancel()
	_ = end(t, stream)
	h.awaitState("s1", disconnected())
}

func TestAPairCallWhoseSessionWasDeletedMeanwhileConnectsNothing(t *testing.T) {
	t.Parallel()

	h := start(t)
	h.create("s1")
	h.svc.mu.Lock()
	s := h.svc.sessions["s1"]
	h.svc.mu.Unlock()
	before := h.stub("s1")

	// Pair found the session, then a Delete finished before Pair started pairing it.
	if _, err := h.client.DeleteSession(h.ctx, &whatsmeowv1.DeleteSessionRequest{SessionId: "s1"}); err != nil {
		t.Fatalf("DeleteSession: %v", err)
	}

	_, _, _, err := h.svc.startPairing(h.ctx, s)
	if !errors.Is(err, server.ErrSessionNotFound) {
		t.Fatalf("startPairing = %v, want %v", err, server.ErrSessionNotFound)
	}

	if h.stub("s1") != before {
		t.Fatal("pairing a deleted session made a client")
	}
}

func TestDisconnectAfterTheLinkEndsTheLoginAndDisconnects(t *testing.T) {
	t.Parallel()

	h := start(t)
	h.create("s1")
	stream, cancel := h.pair("s1", "")
	client := h.pairing("s1", nil)
	client.linkAs(t, pairedJID, pairedLID)
	cancel()
	_ = end(t, stream)

	// The call is gone, so cancelling it is no way to stop the login.
	if _, err := h.client.Disconnect(h.ctx, &whatsmeowv1.DisconnectRequest{SessionId: "s1"}); err != nil {
		t.Fatalf("Disconnect after the link: %v", err)
	}

	h.wantState("s1", disconnected())

	if client.disconnectCount() == 0 {
		t.Fatal("the client was not disconnected")
	}

	// whatsmeow's 515 reconnect ignores that Disconnect (connectionevents.go:33-36): the login it still brings is
	// disconnected again, and the session stays Disconnected.
	again := client.nextDisconnect()
	client.logIn(true)
	h.await(again, "the disconnect of the login after Disconnect")
	h.wantState("s1", disconnected())
}

func TestAClientThatReconnectsAfterItsSessionWasDeletedIsDisconnected(t *testing.T) {
	t.Parallel()

	h := start(t)
	h.create("s1")
	stream, _ := h.pair("s1", "")
	client := h.pairing("s1", nil)
	client.linkAs(t, pairedJID, pairedLID)
	client.send(whatsmeow.QRChannelSuccess)

	if _, err := h.client.DeleteSession(h.ctx, &whatsmeowv1.DeleteSessionRequest{SessionId: "s1"}); err != nil {
		t.Fatalf("DeleteSession: %v", err)
	}

	_ = end(t, stream)
	again := client.nextDisconnect()

	// whatsmeow's 515 goroutine disconnects, then connects again on its own (connectionevents.go:33-36): its
	// connect checks only Store.Deleted (client.go:569), which Delete, deleting by the recorded JID, does not set.
	if err := client.ConnectContext(context.Background()); err != nil {
		t.Fatalf("ConnectContext: %v", err)
	}

	client.logIn(true)
	h.await(again, "the disconnect of the deleted session's client")
}

func TestDisconnectStopsALinkedLoginThatEndedWithoutPreKeys(t *testing.T) {
	t.Parallel()

	h := start(t)
	h.create("s1")
	_, _ = h.pair("s1", "")
	client := h.pairing("s1", nil)
	client.linkAs(t, pairedJID, pairedLID)
	client.send(whatsmeow.QRChannelSuccess)

	// Holding ops keeps the wait for the login from disconnecting first, as a Disconnect call can get ops first.
	h.svc.mu.Lock()
	s := h.svc.sessions["s1"]
	h.svc.mu.Unlock()
	s.ops.Lock()

	release := sync.OnceFunc(s.ops.Unlock)
	t.Cleanup(release)

	client.logIn(false)

	s.mu.Lock()
	stopped := stopLogin(s)
	s.mu.Unlock()
	release()

	// Disconnect would otherwise answer PAIRING_IN_PROGRESS for a pairing that already failed.
	if !stopped {
		t.Fatal("Disconnect cannot stop a linked login that ended without the pre-keys")
	}
}
