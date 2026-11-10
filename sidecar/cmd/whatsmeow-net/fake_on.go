//go:build fakewhatsapp

package main

import (
	"context"
	"os"
	"time"

	"github.com/rs/zerolog"
	"go.mau.fi/whatsmeow/store"

	fakev1 "github.com/tcortega/Whatsmeow.NET/sidecar/gen/whatsmeow/fake/v1"
	whatsmeowv1 "github.com/tcortega/Whatsmeow.NET/sidecar/gen/whatsmeow/v1"
	"github.com/tcortega/Whatsmeow.NET/sidecar/internal/fakewhatsapp"
	"github.com/tcortega/Whatsmeow.NET/sidecar/internal/outbox"
	"github.com/tcortega/Whatsmeow.NET/sidecar/internal/server"
	"github.com/tcortega/Whatsmeow.NET/sidecar/internal/session"
)

// fakeBuild is true in a test build: it can fake WhatsApp with --fake-whatsapp (ADR 0019).
const fakeBuild = true

// serveFake replaces WhatsApp with the fake and serves its control next to whatsmeow.v1, on the same server, so
// the same socket and token; its media server keeps its files next to database, and its RecordEvent writes to box.
// It returns the fake's client factory, in place of whatsmeow's, and its failing, which fails the recording of a
// message withhold_first_answer marked (ADR 0044).
func serveFake(ctx context.Context, srv *server.Server, log zerolog.Logger, database string, box *outbox.Outbox) (session.NewClient, failing) {
	log.Warn().Msg("fake WhatsApp mode: nothing connects to WhatsApp; the control service scripts it")

	if dir := os.Getenv(dumpDirVariable); dir != "" {
		go dumpOnRequest(ctx, dir, 300*time.Millisecond, log)
	}

	// The fake media server's files outlive the process, as WhatsApp's do, so a MediaRef downloads after a restart.
	record := func(ctx context.Context, sessionID string, event *whatsmeowv1.StreamEventsResponse) error {
		_, err := box.Record(ctx, sessionID, "", event)

		return err //nolint:wrapcheck // RecordEvent wraps it
	}
	whatsApp := fakewhatsapp.New(log, fakewhatsapp.WithMediaDir(database+".fake-media"), fakewhatsapp.WithRecorder(record))
	fakev1.RegisterFakeWhatsAppServiceServer(srv, fakewhatsapp.NewControl(whatsApp))

	return func(sessionID string, device *store.Device, _ func()) session.Client {
		client := whatsApp.NewClient(sessionID)
		client.Store = device

		return client
	}, whatsApp.Failing
}
