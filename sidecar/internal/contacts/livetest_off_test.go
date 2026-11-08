//go:build !livetest

package contacts

import (
	"testing"

	"google.golang.org/grpc/codes"
	"google.golang.org/grpc/status"

	livetestv1 "github.com/tcortega/Whatsmeow.NET/sidecar/gen/whatsmeow/livetest/v1"
)

func TestAReleaseBuildHasNoLiveContactControl(t *testing.T) {
	t.Parallel()

	h := start(t)

	_, err := livetestv1.NewLiveContactServiceClient(h.conn).SetAbout(h.ctx, &livetestv1.SetAboutRequest{SessionId: "s1", Text: "wmn about"})
	if status.Code(err) != codes.Unimplemented {
		t.Fatalf("SetAbout: %v; want UNIMPLEMENTED", err)
	}

	if h.stub.lookups != 0 {
		t.Fatalf("whatsmeow got %d calls; want none", h.stub.lookups)
	}
}
