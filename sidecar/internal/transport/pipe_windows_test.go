package transport

import (
	"context"
	"regexp"
	"testing"

	"github.com/Microsoft/go-winio"
)

func TestListenPipeServesARandomOwnerOnlyPipe(t *testing.T) {
	t.Parallel()

	lis, endpoint, err := Listen(context.Background(), Pipe, "")
	if err != nil {
		t.Fatalf("listen: %v", err)
	}

	defer func() { _ = lis.Close() }()

	if !regexp.MustCompile(`^\\\\\.\\pipe\\whatsmeow-net-[0-9a-f]{32}$`).MatchString(endpoint) {
		t.Fatalf("endpoint = %q", endpoint)
	}

	go func() {
		if conn, err := lis.Accept(); err == nil {
			_ = conn.Close()
		}
	}()

	conn, err := winio.DialPipeContext(context.Background(), endpoint)
	if err != nil {
		t.Fatalf("dial: %v", err)
	}

	_ = conn.Close()
}
