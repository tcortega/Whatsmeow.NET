package main

import (
	"bytes"
	"path/filepath"
	"runtime"
	"strings"
	"sync"
	"testing"
	"time"

	"go.mau.fi/whatsmeow"
	waBinary "go.mau.fi/whatsmeow/binary"
	"go.mau.fi/whatsmeow/proto/waAdv"
	"go.mau.fi/whatsmeow/types"
	"go.mau.fi/whatsmeow/types/events"

	wmlog "github.com/tcortega/Whatsmeow.NET/sidecar/internal/log"
	"github.com/tcortega/Whatsmeow.NET/sidecar/internal/session"
	"github.com/tcortega/Whatsmeow.NET/sidecar/internal/store"
)

// lockedBuffer is a buffer the logger and the test share.
type lockedBuffer struct {
	mu  sync.Mutex
	buf bytes.Buffer
}

func (b *lockedBuffer) Write(p []byte) (int, error) {
	b.mu.Lock()
	defer b.mu.Unlock()

	n, _ := b.buf.Write(p) // bytes.Buffer.Write never returns an error

	return n, nil
}

func (b *lockedBuffer) String() string {
	b.mu.Lock()
	defer b.mu.Unlock()

	return b.buf.String()
}

// The release build's whatsmeow, on the release build's store and logger, writes its own text at the pin: lines that
// name other people's JIDs and phone numbers at warn and info, and an error that names the JID. None of them reaches
// stderr or a status message with the number (ADR 0039). The test fails when whatsmeow stops writing one of the
// lines, so a bump that moves them is read again (#48). It does not run in parallel: whatsmeow must leave no goroutine.
func TestWhatsmeowsOwnLinesAndErrorsCarryNoNumber(t *testing.T) {
	ctx := t.Context()
	goroutines := runtime.NumGoroutine()

	var stderr lockedBuffer

	redactor := wmlog.NewRedactor()
	log := newLogger(&stderr, redactor)
	wmlog.SetupSignal(log)

	db, err := store.Open(ctx, filepath.Join(t.TempDir(), "whatsmeow.db"), log)
	if err != nil {
		t.Fatalf("open the store: %v", err)
	}

	device := db.Devices.NewDevice()
	device.ID = &types.JID{User: "5511900000099", Device: 7, Server: types.DefaultUserServer}
	device.Account = &waAdv.ADVSignedDeviceIdentity{
		Details: []byte{}, AccountSignature: make([]byte, 64), AccountSignatureKey: make([]byte, 32), DeviceSignature: make([]byte, 64),
	}

	if err := device.Save(ctx); err != nil {
		t.Fatalf("save the device: %v", err)
	}

	cli, ok := session.NewWhatsmeowClient(log)("s1", device).(*whatsmeow.Client)
	if !ok {
		t.Fatal("the release client is not whatsmeow's")
	}

	pn := types.NewJID("5511900000001", types.DefaultUserServer)
	lid := types.NewJID("111111111111111", types.HiddenUserServer)

	// A message this device has no session for: whatsmeow looks for the sender's LID and logs that it has none, then
	// logs the decryption error, which names the signal address (message.go:345-351, 415; libsignal
	// SessionCipher.go:168).
	//nolint:staticcheck // SA1019: the handler of an incoming node, the only way to have whatsmeow log it without a socket
	cli.DangerousInternals().HandleEncryptedMessage(ctx, &waBinary.Node{
		Tag:     "message",
		Attrs:   waBinary.Attrs{"from": pn, "id": "3EB0A1B2C3D4E5F6", "t": "1730000000", "type": "text"},
		Content: []waBinary.Node{{Tag: "enc", Attrs: waBinary.Attrs{"type": "msg", "v": "2"}, Content: []byte{1, 2, 3}}},
	})

	// The store moves a signal session to the LID and logs both at info (store/sqlstore/store.go:248-300).
	if err := device.Sessions.PutSession(ctx, pn.SignalAddress().String(), []byte("session")); err != nil {
		t.Fatalf("put a session: %v", err)
	}

	cli.DangerousInternals().MigrateSessionStore(ctx, pn, lid) //nolint:staticcheck // SA1019: as above, what a message with both forms of its sender runs

	// Blocking a number with no LID known asks WhatsApp, and the client is not connected (user.go:1036-1040).
	_, err = cli.UpdateBlocklist(ctx, pn, events.BlocklistChangeActionBlock, "")
	if err == nil || !strings.Contains(err.Error(), pn.String()) {
		t.Fatalf("UpdateBlocklist = %v, want whatsmeow's error naming %s", err, pn)
	}

	if text := redactor.Text(err.Error()); strings.Contains(text, "900000001") || !strings.Contains(text, "~") {
		t.Errorf("the status message of %q is %q", err, text)
	}

	got := stderr.String()
	for _, line := range []string{`"component":"libsignal"`, "No LID found for ~", "Error decrypting message 3EB0A1B2C3D4E5F6 from ~", "Migrated 1 sessions, 0 identity keys and 0 sender keys from ~"} {
		if !strings.Contains(got, line) {
			t.Errorf("whatsmeow did not log %q", line)
		}
	}

	if strings.Contains(got, "900000001") || strings.Contains(got, "111111111111111") || strings.Contains(got, "900000099") {
		t.Errorf("stderr has a number:\n%s", got)
	}

	if t.Failed() {
		t.Logf("stderr:\n%s", got)
	}

	if err := db.Close(); err != nil {
		t.Fatalf("close the store: %v", err)
	}

	// whatsmeow started nothing that outlives the test.
	deadline := time.Now().Add(5 * time.Second)
	for runtime.NumGoroutine() > goroutines && time.Now().Before(deadline) {
		time.Sleep(10 * time.Millisecond)
	}

	if now := runtime.NumGoroutine(); now > goroutines {
		t.Errorf("%d goroutines after the test, %d before", now, goroutines)
	}
}
