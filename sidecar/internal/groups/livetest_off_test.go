//go:build !livetest

package groups

import (
	"testing"

	"google.golang.org/grpc/codes"
	"google.golang.org/grpc/status"

	livetestv1 "github.com/tcortega/Whatsmeow.NET/sidecar/gen/whatsmeow/livetest/v1"
)

func TestAReleaseBuildHasNoLiveControl(t *testing.T) {
	t.Parallel()

	h := start(t)

	_, err := livetestv1.NewLiveTestServiceClient(h.conn).SetGroupPhoto(h.ctx, &livetestv1.SetGroupPhotoRequest{SessionId: "s1", Group: group})
	if status.Code(err) != codes.Unimplemented {
		t.Fatalf("SetGroupPhoto: %v; want UNIMPLEMENTED", err)
	}
}
