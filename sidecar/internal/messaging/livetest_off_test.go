//go:build !livetest

package messaging

import (
	"testing"

	"google.golang.org/grpc/codes"

	whatsmeowv1 "github.com/tcortega/Whatsmeow.NET/sidecar/gen/whatsmeow/v1"
)

func TestAnIDOtherThanWhatsmeowsIsInvalidAndSendsNothing(t *testing.T) {
	t.Parallel()

	h := start(t)

	for _, clientID := range []string{"3eb0000000000000000001", "3EB00000000000000000001", "ABCD000000000000000001"} {
		_, err := h.client.SendText(h.ctx, &whatsmeowv1.SendTextRequest{SessionId: "s1", Chat: "111@lid", ClientMessageId: clientID, Text: "hi"})
		wantReason(t, err, codes.InvalidArgument, "INVALID_ARGUMENT", map[string]string{"field": "client_message_id"})
	}

	if len(h.stub.sent()) != 0 {
		t.Fatal("a send with a malformed id reached whatsmeow")
	}
}
