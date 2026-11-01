package transport

import (
	"context"
	"regexp"
	"testing"

	"github.com/Microsoft/go-winio"
)

func TestListenPipeServesARandomPipeOnlyThisUserCanOpen(t *testing.T) {
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
		for {
			conn, err := lis.Accept()
			if err != nil {
				return
			}

			_ = conn.Close()
		}
	}()

	// Protected, and one ACE: full access for this user (GA maps to the pipe's full access, FA).
	if got, want := dacl(t, endpoint), regexp.MustCompile(`^D:P[A-Z]*\(A;;(GA|FA);;;`+regexp.QuoteMeta(account(t))+`\)$`); !want.MatchString(got) {
		t.Fatalf("pipe DACL = %s, want %s", got, want)
	}

	conn, err := winio.DialPipeContext(context.Background(), endpoint)
	if err != nil {
		t.Fatalf("dial: %v", err)
	}

	_ = conn.Close()
}
