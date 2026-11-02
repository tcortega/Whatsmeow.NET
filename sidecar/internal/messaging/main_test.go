package messaging

import (
	"os"
	"testing"

	"github.com/rs/zerolog"

	"github.com/tcortega/Whatsmeow.NET/sidecar/internal/log"
)

func TestMain(m *testing.M) {
	// As cmd/whatsmeow-net does at startup: the parallel tests create devices at once.
	log.SetupSignal(zerolog.Nop())
	os.Exit(m.Run())
}
