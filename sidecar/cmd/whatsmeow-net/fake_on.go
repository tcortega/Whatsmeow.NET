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
// the same socket and token; its media server keeps its files next to database. It returns the fake's client
// factory, in place of whatsmeow's, and its failing, which fails the recording of a message withhold_first_answer
// marked (ADR 0044).
func serveFake(srv *server.Server, log zerolog.Logger, database string) (session.NewClient, failing) {
	log.Warn().Msg("fake WhatsApp mode: nothing connects to WhatsApp; the control service scripts it")

	// The fake media server's files outlive the process, as WhatsApp's do, so a MediaRef downloads after a restart.
	whatsApp := fakewhatsapp.New(log, fakewhatsapp.WithMediaDir(database+".fake-media"))
	fakev1.RegisterFakeWhatsAppServiceServer(srv, fakewhatsapp.NewControl(whatsApp))

	return func(sessionID string, device *store.Device, _ func()) session.Client {
		client := whatsApp.NewClient(sessionID)
		client.Store = device

		return client
	}, whatsApp.Failing
}
