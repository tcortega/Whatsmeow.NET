package messaging

import (
	"testing"
	"time"

	"go.mau.fi/whatsmeow/proto/waE2E"
	"google.golang.org/grpc/codes"
	"google.golang.org/protobuf/proto"
	"google.golang.org/protobuf/types/known/durationpb"

	whatsmeowv1 "github.com/tcortega/Whatsmeow.NET/sidecar/gen/whatsmeow/v1"
)

func TestSendDisappearingTimerSendsTheSettingAsWhatsmeowBuildsIt(t *testing.T) {
	t.Parallel()

	for _, timer := range []time.Duration{24 * time.Hour, 7 * 24 * time.Hour, 90 * 24 * time.Hour, 0} {
		t.Run(timer.String(), func(t *testing.T) {
			t.Parallel()

			h := start(t)
			clock := h.fakeClock()

			resp, err := h.client.SendDisappearingTimer(h.ctx, &whatsmeowv1.SendDisappearingTimerRequest{
				SessionId: "s1", Chat: "111@lid", ClientMessageId: id(1), Timer: durationpb.New(timer),
			})
			if err != nil || resp.GetChat() != "111@lid" || !resp.GetMessageTime().AsTime().Equal(at) {
				t.Fatalf("SendDisappearingTimer = %v, %v; want 111@lid at %v", resp, err, at)
			}

			// whatsmeow's own message for a direct chat (send.go:668-678), timestamped by the slice's clock.
			want := &waE2E.Message{ProtocolMessage: &waE2E.ProtocolMessage{
				Type:                      waE2E.ProtocolMessage_EPHEMERAL_SETTING.Enum(),
				EphemeralExpiration:       proto.Uint32(uint32(timer.Seconds())),
				EphemeralSettingTimestamp: proto.Int64(clock.now().Unix()),
			}}

			if sent := h.only(t); sent.to != jid("111@lid") || sent.id != id(1) || !proto.Equal(sent.message, want) {
				t.Fatalf("whatsmeow got %v to %v as %s, want %v", sent.message, sent.to, sent.id, want)
			}
		})
	}
}

func TestAMalformedDisappearingTimerIsInvalidAndSendsNothing(t *testing.T) {
	t.Parallel()

	h := start(t)
	send := func(chat string, timer *durationpb.Duration) error {
		_, err := h.client.SendDisappearingTimer(h.ctx, &whatsmeowv1.SendDisappearingTimerRequest{SessionId: "s1", Chat: chat, ClientMessageId: id(1), Timer: timer})

		return err //nolint:wrapcheck // the test reads the status
	}

	for n, c := range []struct {
		err   error
		field string
	}{
		// A group's timer is GroupService's.
		{send(group, durationpb.New(24*time.Hour)), "chat"},
		{send("111:2@lid", durationpb.New(24*time.Hour)), "chat"},
		{send("111@lid", nil), "timer"},
		{send("111@lid", durationpb.New(48*time.Hour)), "timer"},
		{send("111@lid", durationpb.New(-24*time.Hour)), "timer"},
		{send("111@lid", durationpb.New(24*time.Hour+time.Millisecond)), "timer"},
	} {
		t.Logf("case %d", n)
		wantReason(t, c.err, codes.InvalidArgument, "INVALID_ARGUMENT", map[string]string{"field": c.field})
	}

	if exists, _, _, _ := h.record(1); exists || len(h.stub.sent()) != 0 {
		t.Fatal("a malformed setting left a record or reached whatsmeow")
	}
}
