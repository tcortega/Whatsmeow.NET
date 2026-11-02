//go:build livetest

package messaging

import (
	"testing"

	"google.golang.org/grpc/codes"

	whatsmeowv1 "github.com/tcortega/Whatsmeow.NET/sidecar/gen/whatsmeow/v1"
)

func TestTheLiveTestBuildSendsAnIDWhatsmeowWouldNotMake(t *testing.T) {
	t.Parallel()

	h := start(t)

	const phoneLike = "A1B2C3D4E5F60718293A4B5C6D7E8F90"

	if _, err := h.client.SendText(h.ctx, &whatsmeowv1.SendTextRequest{SessionId: "s1", Chat: "111@lid", ClientMessageId: phoneLike, Text: "hi"}); err != nil {
		t.Fatalf("SendText: %v", err)
	}

	if got := h.stub.ids(); len(got) != 1 || got[0] != phoneLike {
		t.Fatalf("whatsmeow got %v; want the id as given", got)
	}
}

func TestTheLiveTestBuildStillRefusesAnIDThatIsNotLettersAndDigits(t *testing.T) {
	t.Parallel()

	h := start(t)

	for _, clientID := range []string{"", "3EB0-0001", "x\x00y"} {
		_, err := h.client.SendText(h.ctx, &whatsmeowv1.SendTextRequest{SessionId: "s1", Chat: "111@lid", ClientMessageId: clientID, Text: "hi"})
		wantReason(t, err, codes.InvalidArgument, "INVALID_ARGUMENT", map[string]string{"field": "client_message_id"})
	}
}
