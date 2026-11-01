package main

import (
	"bytes"
	"strings"
	"testing"

	waLog "go.mau.fi/whatsmeow/util/log"
)

// whatsmeow logs every node it sends and each QR code it emits at debug level (client.go:990, qrchan.go:97), and a
// QR code carries the device's ADV secret (pair.go:125-130), so the sidecar's logger, and every logger derived from
// it, drops debug.
func TestTheLoggerDropsDebugForEveryComponent(t *testing.T) {
	t.Parallel()

	var stderr bytes.Buffer
	log := newLogger(&stderr)
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
