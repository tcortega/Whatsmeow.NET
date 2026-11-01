//go:build fakewhatsapp

package main

import (
	"github.com/rs/zerolog"
	"go.mau.fi/whatsmeow/store"

	fakev1 "github.com/tcortega/Whatsmeow.NET/sidecar/gen/whatsmeow/fake/v1"
	"github.com/tcortega/Whatsmeow.NET/sidecar/internal/fakewhatsapp"
	"github.com/tcortega/Whatsmeow.NET/sidecar/internal/server"
	"github.com/tcortega/Whatsmeow.NET/sidecar/internal/session"
)

// fakeBuild is true in a test build: it can fake WhatsApp with --fake-whatsapp (ADR 0019).
const fakeBuild = true

// serveFake replaces WhatsApp with the fake and serves its control next to whatsmeow.v1, on the same server, so
// the same socket and token. It returns the fake's client factory, in place of whatsmeow's.
func serveFake(srv *server.Server, log zerolog.Logger) session.NewClient {
	log.Warn().Msg("fake WhatsApp mode: nothing connects to WhatsApp; the control service scripts it")

	whatsApp := fakewhatsapp.New(log)
	fakev1.RegisterFakeWhatsAppServiceServer(srv, fakewhatsapp.NewControl(whatsApp))

	return func(sessionID string, device *store.Device) session.Client {
		client := whatsApp.NewClient(sessionID)
		client.Store = device

		return client
	}
}
