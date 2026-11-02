package messaging

import (
	"testing"
	"time"

	"go.mau.fi/whatsmeow/types"
	waevents "go.mau.fi/whatsmeow/types/events"
	"google.golang.org/protobuf/proto"
	"google.golang.org/protobuf/types/known/timestamppb"

	whatsmeowv1 "github.com/tcortega/Whatsmeow.NET/sidecar/gen/whatsmeow/v1"
)

// receiptEvent is a receipt as whatsmeow dispatches it (receipt.go:66-104): one or more message ids of a chat.
func receiptEvent(kind types.ReceiptType, ids ...string) *waevents.Receipt {
	return &waevents.Receipt{
		MessageSource: types.MessageSource{Chat: jid("111@s.whatsapp.net"), Sender: jid("111@s.whatsapp.net")},
		MessageIDs:    ids,
		Timestamp:     time.Unix(1_700_000_100, 0),
		Type:          kind,
	}
}

func TestEveryReceiptTypeMapsToItsReceiptEvent(t *testing.T) {
	t.Parallel()

	at := time.Unix(1_700_000_100, 0)
	source := types.MessageSource{
		Chat: jid("120363000000000000@g.us"), Sender: jid("111:2@lid"), IsFromMe: true,
		SenderAlt: jid("5511999999999@s.whatsapp.net"), RecipientAlt: jid("222@lid"),
	}
	want := &whatsmeowv1.MessageSource{
		Chat: "120363000000000000@g.us", Sender: "111:2@lid", SenderAlt: "5511999999999@s.whatsapp.net",
		RecipientAlt: "222@lid", IsFromMe: true,
	}

	// whatsmeow's receipt types at the pin (types/presence.go:38-61), and one it does not have.
	cases := []struct {
		kind types.ReceiptType
		want whatsmeowv1.ReceiptType
	}{
		{types.ReceiptTypeDelivered, whatsmeowv1.ReceiptType_RECEIPT_TYPE_DELIVERED},
		{types.ReceiptTypeSender, whatsmeowv1.ReceiptType_RECEIPT_TYPE_SENDER},
		{types.ReceiptTypeRetry, whatsmeowv1.ReceiptType_RECEIPT_TYPE_RETRY},
		{types.ReceiptTypeRead, whatsmeowv1.ReceiptType_RECEIPT_TYPE_READ},
		{types.ReceiptTypeReadSelf, whatsmeowv1.ReceiptType_RECEIPT_TYPE_READ_SELF},
		{types.ReceiptTypePlayed, whatsmeowv1.ReceiptType_RECEIPT_TYPE_PLAYED},
		{types.ReceiptTypePlayedSelf, whatsmeowv1.ReceiptType_RECEIPT_TYPE_PLAYED_SELF},
		{types.ReceiptTypeServerError, whatsmeowv1.ReceiptType_RECEIPT_TYPE_SERVER_ERROR},
		{types.ReceiptTypeInactive, whatsmeowv1.ReceiptType_RECEIPT_TYPE_INACTIVE},
		{types.ReceiptTypePeerMsg, whatsmeowv1.ReceiptType_RECEIPT_TYPE_PEER_MSG},
		{types.ReceiptTypeHistorySync, whatsmeowv1.ReceiptType_RECEIPT_TYPE_HISTORY_SYNC},
		{types.ReceiptType("future"), whatsmeowv1.ReceiptType_RECEIPT_TYPE_UNSPECIFIED},
	}

	for _, c := range cases {
		key, got, ok := Receipt("s1", &waevents.Receipt{MessageSource: source, MessageIDs: []string{"M1", "M2"}, Timestamp: at, Type: c.kind})
		wantKey := "r\x00120363000000000000@g.us\x00111:2@lid\x00" + string(c.kind) + "\x001700000100\x00M1\x00M2"
		wantEvent := &whatsmeowv1.StreamEventsResponse{Payload: &whatsmeowv1.StreamEventsResponse_Receipt{Receipt: &whatsmeowv1.ReceiptEvent{
			Source: want, MessageIds: []string{"M1", "M2"}, ReceiptTime: timestamppb.New(at), Type: c.want,
		}}}

		if !ok || key != wantKey || !proto.Equal(got, wantEvent) {
			t.Errorf("%q: key %q, %v, %v; want %q, %v", c.kind, key, got, ok, wantKey, wantEvent)
		}
	}
}

func TestAReceiptsKeyTiesARedeliveryToItAndALaterReceiptIsItsOwn(t *testing.T) {
	t.Parallel()

	first, _, _ := Receipt("s1", receiptEvent(types.ReceiptTypeRead, "M1"))
	again, _, _ := Receipt("s1", receiptEvent(types.ReceiptTypeRead, "M1"))
	later := receiptEvent(types.ReceiptTypeRead, "M1")
	later.Timestamp = later.Timestamp.Add(time.Second)
	afterwards, _, _ := Receipt("s1", later)
	delivered, _, _ := Receipt("s1", receiptEvent(types.ReceiptTypeDelivered, "M1"))

	if first != again || first == afterwards || first == delivered || afterwards == delivered {
		t.Fatalf("keys %q, %q, %q, %q: want the redelivery alike and the rest distinct", first, again, afterwards, delivered)
	}
}

func TestOnlyAReceiptWithMessagesIsMapped(t *testing.T) {
	t.Parallel()

	// whatsmeow dispatches none without an id (receipt.go:85-88): one would cover no message.
	for _, evt := range []any{receiptEvent(types.ReceiptTypeRead), &waevents.Message{}, &waevents.Connected{}} {
		if _, _, ok := Receipt("s1", evt); ok {
			t.Errorf("%T %v was mapped", evt, evt)
		}
	}
}
