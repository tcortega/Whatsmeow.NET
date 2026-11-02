//go:build !windows

package media

import (
	"os"
	"syscall"
	"testing"

	"google.golang.org/grpc/codes"

	whatsmeowv1 "github.com/tcortega/Whatsmeow.NET/sidecar/gen/whatsmeow/v1"
)

// TestAPathSwappedForAFIFOIsRefusedAtOnce: opening a FIFO with no writer blocks, past the call's context, unless it
// is opened non-blocking (open(2) O_NONBLOCK). The attempt opens it so, sees it is not a regular file, and refuses it.
func TestAPathSwappedForAFIFOIsRefusedAtOnce(t *testing.T) {
	t.Parallel()

	h := start(t)
	path := file(t, h.root, "a.bin", []byte("a"))

	swapBeforeTheRetry(t, h, func() {
		if err := os.Remove(path); err != nil {
			t.Errorf("remove: %v", err)
		}

		if err := syscall.Mkfifo(path, 0o600); err != nil {
			t.Errorf("mkfifo: %v", err)
		}
	})

	_, err := h.uploadPath(h.ctx, path, whatsmeowv1.MediaType_MEDIA_TYPE_IMAGE)
	wantReason(t, err, codes.InvalidArgument, "INVALID_ARGUMENT", "header.path")
}
