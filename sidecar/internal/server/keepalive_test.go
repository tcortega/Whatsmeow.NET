package server

import (
	"context"
	"testing"
	"time"

	"github.com/rs/zerolog"
	"golang.org/x/net/http2"
	"google.golang.org/grpc/test/bufconn"

	sidecarlog "github.com/tcortega/Whatsmeow.NET/sidecar/internal/log"
)

// An External-mode client pings an idle connection to notice a peer that is gone (ADR 0046). grpc-go's default policy
// answers more than two pings within 5 minutes with GOAWAY too_many_pings (grpc v1.85.0 internal/transport
// http2_server.go:881, 928-930, defaults.go:40), which would drop every connection the client keeps alive.
func TestPingsAtTheMinimumIntervalOnAnIdleConnectionAreAnswered(t *testing.T) {
	t.Parallel()

	srv := New(token, zerolog.Nop(), sidecarlog.NewRedactor())
	lis := bufconn.Listen(1 << 20)
	served := make(chan error, 1)

	go func() { served <- srv.Serve(lis) }()

	t.Cleanup(func() {
		srv.Stop()

		if err := <-served; err != nil {
			t.Errorf("serve: %v", err)
		}
	})

	conn, err := lis.DialContext(context.Background())
	if err != nil {
		t.Fatalf("dial: %v", err)
	}

	t.Cleanup(func() { _ = conn.Close() })

	if _, err := conn.Write([]byte(http2.ClientPreface)); err != nil {
		t.Fatalf("write the preface: %v", err)
	}

	framer := http2.NewFramer(conn, conn)
	if err := framer.WriteSettings(); err != nil {
		t.Fatalf("write settings: %v", err)
	}

	// No stream is ever opened: an idle connection, as the client's between calls. The fourth early ping is the
	// third strike, whose GOAWAY comes before the fifth ping's ack.
	for ping := range 5 {
		if ping > 0 {
			time.Sleep(keepaliveMinTime + 200*time.Millisecond)
		}

		data := [8]byte{byte(ping)}
		if err := framer.WritePing(false, data); err != nil {
			t.Fatalf("write ping %d: %v", ping, err)
		}

		awaitPingAck(t, framer, data)
	}
}

// awaitPingAck reads frames until the ack of data, and fails on a GOAWAY.
func awaitPingAck(t *testing.T, framer *http2.Framer, data [8]byte) {
	t.Helper()

	for {
		frame, err := framer.ReadFrame()
		if err != nil {
			t.Fatalf("read: %v", err)
		}

		switch f := frame.(type) {
		case *http2.GoAwayFrame:
			t.Fatalf("the server sent GOAWAY %v %q", f.ErrCode, f.DebugData())
		case *http2.SettingsFrame:
			if !f.IsAck() {
				if err := framer.WriteSettingsAck(); err != nil {
					t.Fatalf("ack settings: %v", err)
				}
			}
		case *http2.PingFrame:
			if f.IsAck() && f.Data == data {
				return
			}
		}
	}
}
