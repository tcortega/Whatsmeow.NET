// Package log holds the sidecar's logging setup.
package log

import (
	"github.com/rs/zerolog"
	signallogger "go.mau.fi/libsignal/logger"
)

// SetupSignal points libsignal's process-wide logger at log. Call it once at startup, before any goroutine uses
// whatsmeow. Without it, libsignal sets its default logger lazily on first use, which is a data race between two
// goroutines that create or use devices at once, and its default logger prints to stdout, where the sidecar writes
// only its ready line (ARCHITECTURE §7.8) [observed: libsignal v0.2.2 logger/Logger.go:34-38,
// logger/DefaultLogger.go:16-28].
func SetupSignal(log zerolog.Logger) {
	var signal signallogger.Loggable = &signalLog{log: log.With().Str("component", "libsignal").Logger()}
	signallogger.Setup(&signal)
}

// signalLog is libsignal's Loggable over zerolog.
type signalLog struct {
	log zerolog.Logger
}

func (s *signalLog) Debug(caller, message string) { s.log.Debug().Str("caller", caller).Msg(message) }

func (s *signalLog) Info(caller, message string) { s.log.Info().Str("caller", caller).Msg(message) }

func (s *signalLog) Warning(caller, message string) { s.log.Warn().Str("caller", caller).Msg(message) }

func (s *signalLog) Error(caller, message string) { s.log.Error().Str("caller", caller).Msg(message) }

func (*signalLog) Configure(string) {}
