package events

import (
	"testing"

	"go.mau.fi/whatsmeow/proto/waCommon"
	"go.mau.fi/whatsmeow/proto/waE2E"
	"google.golang.org/protobuf/proto"
)

func TestAHistorySyncNotificationIsNeverALiveMessage(t *testing.T) {
	t.Parallel()

	for _, fromMe := range []bool{true, false} {
		notification := messageEvent("N1")
		notification.Info.IsFromMe = fromMe
		notification.Message = &waE2E.Message{ProtocolMessage: &waE2E.ProtocolMessage{
			Type:                    waE2E.ProtocolMessage_HISTORY_SYNC_NOTIFICATION.Enum(),
			HistorySyncNotification: &waE2E.HistorySyncNotification{DirectPath: proto.String("/blob"), InitialHistBootstrapInlinePayload: []byte{1}},
		}}

		// The History slice queues it (internal/history); a bot must never answer it, and its inline payload has no
		// bound a gRPC message keeps.
		if key, got, ok := toEvent(notification, nil, nil); ok {
			t.Fatalf("from me %v: recorded under %q as %v, want nothing", fromMe, key, got)
		}
	}
}

func TestMessageEventIsTheMappingOfALiveMessage(t *testing.T) {
	t.Parallel()

	m := messageEvent("M1")
	m.Message = &waE2E.Message{ReactionMessage: &waE2E.ReactionMessage{Key: &waCommon.MessageKey{ID: proto.String("T1"), Participant: proto.String(accountLID)}}}
	_, live, _ := toEvent(m, account(), nil)

	if got := MessageEvent(m, account()); !proto.Equal(got, live.GetMessage()) || got.GetTarget() == nil {
		t.Fatalf("MessageEvent %v, want the live message's %v", got, live.GetMessage())
	}
}
