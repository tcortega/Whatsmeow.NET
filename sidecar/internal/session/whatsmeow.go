package session

import (
	"github.com/rs/zerolog"
	"go.mau.fi/whatsmeow"
	"go.mau.fi/whatsmeow/store"
	waLog "go.mau.fi/whatsmeow/util/log"
)

// NewWhatsmeowClient returns the release build's client factory: a whatsmeow client configured as ARCHITECTURE
// §7.2 says, following mautrix-whatsapp (ADR 0013).
func NewWhatsmeowClient(log zerolog.Logger) NewClient {
	return func(sessionID string, device *store.Device) Client {
		return configure(whatsmeow.NewClient(device, waLog.Zerolog(log.With().Str("session_id", sessionID).Str("component", "whatsmeow").Logger())))
	}
}

// configure sets §7.2's fields. EnableAutoReconnect is already true (client.go:307), and there is no
// AutoReconnectHook: mautrix sets none, and one that returns false leaves the session Reconnecting with no event
// (client.go:673-675).
func configure(cli *whatsmeow.Client) *whatsmeow.Client {
	cli.SynchronousAck = true
	cli.EnableDecryptedEventBuffer = true
	cli.ManualHistorySyncDownload = true
	cli.UseRetryMessageStore = true
	cli.AutomaticMessageRerequestFromPhone = true
	cli.InitialAutoReconnect = true

	return cli
}
