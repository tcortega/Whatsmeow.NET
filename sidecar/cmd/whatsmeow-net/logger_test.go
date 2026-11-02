package main

import (
	"bytes"
	"strings"
	"testing"

	waLog "go.mau.fi/whatsmeow/util/log"

	wmlog "github.com/tcortega/Whatsmeow.NET/sidecar/internal/log"
)

// whatsmeow logs every node it sends and each QR code it emits at debug level (client.go:990, qrchan.go:97), and a
// QR code carries the device's ADV secret (pair.go:125-130), so the sidecar's logger, and every logger derived from
// it, drops debug.
func TestTheLoggerDropsDebugForEveryComponent(t *testing.T) {
	t.Parallel()

	var stderr bytes.Buffer
	log := newLogger(&stderr, wmlog.NewRedactor())
	whatsmeow := waLog.Zerolog(log.With().Str("session_id", "s1").Str("component", "whatsmeow").Logger())

	log.Debug().Msg("sidecar debug")
	whatsmeow.Sub("QRChannel").Debugf("Emitting QR code %s", "https://wa.me/settings/linked_devices#ref,noise,identity,adv,9")
	whatsmeow.Sub("Send").Debugf("%s", "<iq><link_code_companion_reg jid=\"15551234567@s.whatsapp.net\"/></iq>")

	if stderr.Len() != 0 {
		t.Fatalf("debug lines reached stderr: %s", stderr.String())
	}

	whatsmeow.Infof("Successfully paired")

	if !strings.Contains(stderr.String(), `"level":"info"`) {
		t.Fatalf("an info line did not reach stderr: %q", stderr.String())
	}
}

// Every line the logger writes at info and above has no phone number or JID user part, whoever wrote it: whatsmeow
// logs other people's JIDs at warn and error (message.go:415), and its store logs phone numbers at info, through its
// own logger (store/sqlstore/store.go:300; internal/store) (ARCHITECTURE §7.8, ADR 0039).
func TestTheLoggerRedactsEveryComponentsLines(t *testing.T) {
	t.Parallel()

	var stderr bytes.Buffer
	log := newLogger(&stderr, wmlog.NewRedactor())

	waLog.Zerolog(log.With().Str("component", "store").Logger()).Infof("Migrated %d sessions from %s to %s", 1, "5511900000001", "111111111111111_1")
	wmlog.Whatsmeow(log).Sub("Client").Warnf("Error decrypting message %s from %s: %v", "3EB0", "5511900000001:3@s.whatsapp.net", "no session for user 5511900000001:3")
	log.Error().Str("error", "failed to get user info for 5511900000001@s.whatsapp.net").Msg("call failed")

	if got := stderr.String(); strings.Contains(got, "5511900000001") || strings.Contains(got, "111111111111111") || strings.Count(got, "\n") != 3 {
		t.Fatalf("stderr has a number: %s", got)
	}
}
