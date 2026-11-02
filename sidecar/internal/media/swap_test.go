package media

import (
	"os"
	"strings"
	"testing"
	"time"

	"github.com/rs/zerolog"
	"google.golang.org/grpc/codes"

	whatsmeowv1 "github.com/tcortega/Whatsmeow.NET/sidecar/gen/whatsmeow/v1"
)

// swapBeforeTheRetry fails the first attempt and runs swap while the slice logs its retry, so the second attempt opens
// whatever swap left at the path.
func swapBeforeTheRetry(t *testing.T, h *harness, swap func()) {
	t.Helper()

	h.svc.backoff = []time.Duration{time.Millisecond, time.Millisecond}
	h.stub.failNext = 1
	h.svc.log = zerolog.New(lineWriter(func(line string) {
		if strings.Contains(line, "retrying") {
			swap()
		}
	}))
}

// TestAPathSwappedForADirectoryIsRefusedOnTheAttemptThatOpensIt: each attempt opens the path again, and checks what
// it opened, not what a check before saw.
func TestAPathSwappedForADirectoryIsRefusedOnTheAttemptThatOpensIt(t *testing.T) {
	t.Parallel()

	h := start(t)
	path := file(t, h.root, "a.bin", []byte("a"))
	retries := 0

	swapBeforeTheRetry(t, h, func() {
		retries++

		if err := os.RemoveAll(path); err != nil {
			t.Errorf("remove: %v", err)
		}

		if err := os.Mkdir(path, 0o700); err != nil {
			t.Errorf("mkdir: %v", err)
		}
	})

	_, err := h.uploadPath(h.ctx, path, whatsmeowv1.MediaType_MEDIA_TYPE_IMAGE)
	wantReason(t, err, codes.InvalidArgument, "INVALID_ARGUMENT", "header.path")

	// The refusal is final: no retry after it, and the directory never reached an upload.
	if retries != 1 || h.stub.count() != 1 {
		t.Fatalf("retries = %d, attempts = %d; want the one retry the stub's failure caused, and one upload", retries, h.stub.count())
	}
}
