package transport

import (
	"context"
	"regexp"
	"testing"

	"github.com/Microsoft/go-winio"
	"golang.org/x/sys/windows"
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

	// Opening the pipe by name races with the listener's next instance ("All pipe instances are busy"), so the
	// DACL is read through a client connection, which DialPipeContext waits for.
	conn, err := winio.DialPipeContext(context.Background(), endpoint)
	if err != nil {
		t.Fatalf("dial: %v", err)
	}

	defer func() { _ = conn.Close() }()

	handle, ok := conn.(interface{ Fd() uintptr })
	if !ok {
		t.Fatalf("the pipe connection %T has no handle", conn)
	}

	sd, err := windows.GetSecurityInfo(windows.Handle(handle.Fd()), windows.SE_KERNEL_OBJECT, windows.DACL_SECURITY_INFORMATION)
	if err != nil {
		t.Fatalf("read the pipe DACL: %v", err)
	}

	// Protected, and one ACE: full access for this user (GA maps to the pipe's full access, FA).
	if got, want := sd.String(), regexp.MustCompile(`^D:P[A-Z]*\(A;;(GA|FA);;;`+regexp.QuoteMeta(account(t))+`\)$`); !want.MatchString(got) {
		t.Fatalf("pipe DACL = %s, want %s", got, want)
	}
}
