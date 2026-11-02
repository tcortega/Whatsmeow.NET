package main

import (
	"flag"
	"time"

	"github.com/tcortega/Whatsmeow.NET/sidecar/internal/messaging"
)

// pacing is the send pacing flags: one pacing for every session's sends (ADR 0042).
type pacing struct {
	interval *time.Duration
	burst    *int
}

// pacingFlags declares the send pacing flags on flags.
func pacingFlags(flags *flag.FlagSet) pacing {
	return pacing{
		interval: flags.Duration("send-interval", messaging.DefaultInterval, "a session sends one message every interval once its burst is spent, each wait lengthened by a random part of half an interval"),
		burst:    flags.Int("send-burst", messaging.DefaultBurst, "how many messages a session sends at once before the interval paces them"),
	}
}

// value is the pacing the flags give.
func (p pacing) value() messaging.Pacing {
	return messaging.Pacing{Interval: *p.interval, Burst: *p.burst}
}
