package log

import (
	"bytes"
	"strings"
	"testing"

	"github.com/rs/zerolog"
	signallogger "go.mau.fi/libsignal/logger"
)

func TestSignalLogsGoToTheSidecarsLogNotStdout(t *testing.T) {
	var out bytes.Buffer

	SetupSignal(zerolog.New(&out))
	signallogger.Warning("a libsignal warning")

	if !strings.Contains(out.String(), `"component":"libsignal"`) || !strings.Contains(out.String(), "a libsignal warning") {
		t.Fatalf("log = %q, want the warning as a libsignal component line", out.String())
	}
}
