package server

import (
	"context"
	"net"
	"sync"
	"testing"
	"time"

	"github.com/rs/zerolog"
	"google.golang.org/grpc"
	"google.golang.org/grpc/codes"
	"google.golang.org/grpc/credentials/insecure"
	"google.golang.org/grpc/status"
	"google.golang.org/grpc/test/bufconn"

	whatsmeowv1 "github.com/tcortega/Whatsmeow.NET/sidecar/gen/whatsmeow/v1"
	sidecarlog "github.com/tcortega/Whatsmeow.NET/sidecar/internal/log"
)

const rotated = "ffeeddccbbaa99887766554433221100ffeeddccbbaa99887766554433221100"

// tokenFile is a token file whose content the test changes, and counts its reads.
type tokenFile struct {
	mu    sync.Mutex
	token string
	reads int
}

func (f *tokenFile) read() (string, error) {
	f.mu.Lock()
	defer f.mu.Unlock()

	f.reads++

	return f.token, nil
}

func (f *tokenFile) set(token string) {
	f.mu.Lock()
	defer f.mu.Unlock()

	f.token = token
}

func (f *tokenFile) count() int {
	f.mu.Lock()
	defer f.mu.Unlock()

	return f.reads
}

// A standalone sidecar's secret rotates while it runs: a call with the file's new token is accepted, after which the
// old one is not, and a flood of wrong tokens reads the file at most once per reload interval (ADR 0046).
func TestATokenRotatedInTheFileIsAcceptedAndTheOldOneNoLonger(t *testing.T) {
	t.Parallel()

	file := &tokenFile{token: token}
	now := time.Unix(1_700_000_000, 0)
	clock := func() time.Time { return now }

	srv := New(token, zerolog.Nop(), sidecarlog.NewRedactor())
	srv.ReloadToken(file.read)
	srv.now = clock
	whatsmeowv1.RegisterSidecarServiceServer(srv, whatsmeowv1.UnimplementedSidecarServiceServer{})

	lis := bufconn.Listen(1 << 20)
	served := make(chan error, 1)

	go func() { served <- srv.Serve(lis) }()

	t.Cleanup(func() {
		srv.Stop()

		if err := <-served; err != nil {
			t.Errorf("serve: %v", err)
		}
	})

	conn, err := grpc.NewClient("passthrough:///bufnet",
		grpc.WithContextDialer(func(ctx context.Context, _ string) (net.Conn, error) { return lis.DialContext(ctx) }),
		grpc.WithTransportCredentials(insecure.NewCredentials()))
	if err != nil {
		t.Fatalf("dial: %v", err)
	}

	t.Cleanup(func() { _ = conn.Close() })

	client := whatsmeowv1.NewSidecarServiceClient(conn)
	call := func(value string) error {
		_, err := client.GetInfo(withToken("Bearer "+value), &whatsmeowv1.GetInfoRequest{})

		return err //nolint:wrapcheck // the test reads the status
	}

	// Wrong tokens before any rotation: the file is read once in the interval, however many come.
	for range 5 {
		wantErrorInfo(t, call(rotated), codes.Unauthenticated, "UNAUTHENTICATED")
	}

	if reads := file.count(); reads != 1 {
		t.Fatalf("the file was read %d times for 5 refused calls in one interval, want 1", reads)
	}

	file.set(rotated)
	now = now.Add(tokenReload)

	// Past the token check, the service answers: it implements nothing.
	if err := call(rotated); status.Code(err) != codes.Unimplemented {
		t.Fatalf("the rotated token = %v, want past the token check", err)
	}

	wantErrorInfo(t, call(token), codes.Unauthenticated, "UNAUTHENTICATED")
}
