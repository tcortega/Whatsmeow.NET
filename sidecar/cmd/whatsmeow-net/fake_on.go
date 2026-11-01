//go:build fakewhatsapp

package main

import (
	"github.com/rs/zerolog"

	fakev1 "github.com/tcortega/Whatsmeow.NET/sidecar/gen/whatsmeow/fake/v1"
	"github.com/tcortega/Whatsmeow.NET/sidecar/internal/fakewhatsapp"
	"github.com/tcortega/Whatsmeow.NET/sidecar/internal/server"
)

// fakeBuild is true in a test build: it can fake WhatsApp with --fake-whatsapp (ADR 0019).
const fakeBuild = true

// serveFake replaces WhatsApp with the fake and serves its control next to whatsmeow.v1, on the same server, so
// the same socket and token. A slice that needs a whatsmeow client gets the fake's from here.
func serveFake(srv *server.Server, log zerolog.Logger) {
	log.Warn().Msg("fake WhatsApp mode: nothing connects to WhatsApp; the control service scripts it")
	fakev1.RegisterFakeWhatsAppServiceServer(srv, fakewhatsapp.NewControl(fakewhatsapp.New(log)))
}
