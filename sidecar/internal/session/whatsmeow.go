package session

import (
	"github.com/rs/zerolog"
	"go.mau.fi/whatsmeow"
	"go.mau.fi/whatsmeow/store"

	sidecarlog "github.com/tcortega/Whatsmeow.NET/sidecar/internal/log"
)

// NewWhatsmeowClient returns the release build's client factory: a whatsmeow client configured as ARCHITECTURE
// §7.2 says, following mautrix-whatsapp (ADR 0013), whose log lines have no URL query (sidecarlog.Whatsmeow). It
// registers no handler, so it never calls withheld.
func NewWhatsmeowClient(log zerolog.Logger) NewClient {
	return func(sessionID string, device *store.Device, _ func()) Client {
		return configure(whatsmeow.NewClient(device, sidecarlog.Whatsmeow(log.With().Str("session_id", sessionID).Str("component", "whatsmeow").Logger())))
	}
}

// configure sets §7.2's fields. EnableAutoReconnect is already true (client.go:307), and there is no
// AutoReconnectHook: mautrix sets none, and one that returns false leaves the session Reconnecting with no event
// (client.go:673-675). EmitAppStateEventsOnFullSync is ours, not mautrix's: without it a full sync dispatches no chat
// state events (appstate.go:70-72), so the chat state after pairing and after a recovery would not arrive (ADR 0031).
func configure(cli *whatsmeow.Client) *whatsmeow.Client {
	cli.SynchronousAck = true
	cli.EnableDecryptedEventBuffer = true
	cli.ManualHistorySyncDownload = true
	cli.UseRetryMessageStore = true
	cli.AutomaticMessageRerequestFromPhone = true
	cli.InitialAutoReconnect = true
	cli.EmitAppStateEventsOnFullSync = true

	return cli
}
