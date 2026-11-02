package main

import (
	"flag"
	"io"
	"path/filepath"
	"strings"
	"testing"
	"time"

	"github.com/tcortega/Whatsmeow.NET/sidecar/internal/messaging"
)

func TestThePacingFlagsGiveThePacingAndDefaultToOneASecondAfterFive(t *testing.T) {
	t.Parallel()

	for _, c := range []struct {
		args []string
		want messaging.Pacing
	}{
		{nil, messaging.Pacing{Interval: time.Second, Burst: 5}},
		{[]string{"--send-interval", "2500ms", "--send-burst", "3"}, messaging.Pacing{Interval: 2500 * time.Millisecond, Burst: 3}},
		{[]string{"--send-interval", "1h", "--send-burst", "1000"}, messaging.Pacing{Interval: time.Hour, Burst: 1000}},
		{[]string{"--send-interval", "1ns", "--send-burst", "1"}, messaging.Pacing{Interval: time.Nanosecond, Burst: 1}},
	} {
		flags := flag.NewFlagSet("whatsmeow-net", flag.ContinueOnError)
		flags.SetOutput(io.Discard)
		sends := pacingFlags(flags)

		if err := flags.Parse(c.args); err != nil {
			t.Fatalf("%v: %v", c.args, err)
		}

		if got := sends.value(); got != c.want || got.Check() != nil {
			t.Errorf("%v: %+v (%v), want %+v, within the bounds", c.args, got, got.Check(), c.want)
		}
	}
}

func TestAPacingOutOfItsBoundsIsAUsageError(t *testing.T) {
	t.Parallel()

	for _, args := range [][]string{
		{"--send-burst", "0"},
		{"--send-burst", "1001"},
		{"--send-interval", "0s"},
		{"--send-interval", "-1s"},
		{"--send-interval", "1h0m0.001s"},
	} {
		var stdout, stderr strings.Builder

		// The token never arrives: the refusal comes before the sidecar reads stdin.
		code := run(append([]string{"--nonce", "n", "--database", filepath.Join(t.TempDir(), "db")}, args...), strings.NewReader(""), &stdout, &stderr)

		if code != exitUsage || stdout.Len() != 0 || !strings.Contains(stderr.String(), "--send-interval and --send-burst") {
			t.Errorf("%v: exit code %d, stdout %q, stderr %q; want %d, no ready line and the refusal", args, code, stdout.String(), stderr.String(), exitUsage)
		}
	}
}
