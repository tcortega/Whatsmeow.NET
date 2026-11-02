package messaging

import (
	"strconv"
	"strings"

	"go.mau.fi/whatsmeow/types"
	waevents "go.mau.fi/whatsmeow/types/events"
	"google.golang.org/protobuf/types/known/timestamppb"

	whatsmeowv1 "github.com/tcortega/Whatsmeow.NET/sidecar/gen/whatsmeow/v1"
	"github.com/tcortega/Whatsmeow.NET/sidecar/internal/server"
)

// Receipt is the Events slice's mapper for the receipts WhatsApp sends (events.Mapper): a whatsmeow Receipt becomes a
// ReceiptEvent under its natural key. The Events slice records it before whatsmeow acks the receipt, which with
// SynchronousAck it does only when no handler failed (receipt.go:115-128; ARCHITECTURE §13 invariant 2). A retry
// receipt is recorded too, though whatsmeow acks it regardless (receipt.go:34-38). It is pure: no I/O.
func Receipt(_ string, evt any) (string, *whatsmeowv1.StreamEventsResponse, bool) {
	e, ok := evt.(*waevents.Receipt)
	// whatsmeow dispatches none without an id (receipt.go:85-88): one would cover no message.
	if !ok || len(e.MessageIDs) == 0 {
		return "", nil, false
	}

	return receiptKey(e), &whatsmeowv1.StreamEventsResponse{Payload: &whatsmeowv1.StreamEventsResponse_Receipt{
		Receipt: &whatsmeowv1.ReceiptEvent{
			Source: &whatsmeowv1.MessageSource{
				Chat:         server.Text(e.Chat.String()),
				Sender:       server.Text(e.Sender.String()),
				SenderAlt:    server.Text(e.SenderAlt.String()),
				RecipientAlt: server.Text(e.RecipientAlt.String()),
				IsFromMe:     e.IsFromMe,
			},
			MessageIds:  server.Texts(e.MessageIDs),
			ReceiptTime: timestamppb.New(e.Timestamp),
			Type:        receiptType(e.Type),
		},
	}}, true
}

// receiptKey is a receipt's natural key: its chat, sender, type, time and message ids, so WhatsApp's redelivery of a
// receipt that was not acked keeps its seq, and a later receipt of the same kind for the same message is one of its
// own. The ids come last, so their number never makes two keys alike.
func receiptKey(e *waevents.Receipt) string {
	parts := make([]string, 0, 5+len(e.MessageIDs))
	parts = append(parts, "r", e.Chat.String(), e.Sender.String(), string(e.Type), strconv.FormatInt(e.Timestamp.Unix(), 10))
	parts = append(parts, e.MessageIDs...)

	return strings.Join(parts, "\x00")
}

// receiptType maps whatsmeow's receipt types at the pin (types/presence.go:38-61); one it adds later is unspecified.
func receiptType(t types.ReceiptType) whatsmeowv1.ReceiptType {
	switch t {
	case types.ReceiptTypeDelivered:
		return whatsmeowv1.ReceiptType_RECEIPT_TYPE_DELIVERED
	case types.ReceiptTypeSender:
		return whatsmeowv1.ReceiptType_RECEIPT_TYPE_SENDER
	case types.ReceiptTypeRetry:
		return whatsmeowv1.ReceiptType_RECEIPT_TYPE_RETRY
	case types.ReceiptTypeRead:
		return whatsmeowv1.ReceiptType_RECEIPT_TYPE_READ
	case types.ReceiptTypeReadSelf:
		return whatsmeowv1.ReceiptType_RECEIPT_TYPE_READ_SELF
	case types.ReceiptTypePlayed:
		return whatsmeowv1.ReceiptType_RECEIPT_TYPE_PLAYED
	case types.ReceiptTypePlayedSelf:
		return whatsmeowv1.ReceiptType_RECEIPT_TYPE_PLAYED_SELF
	case types.ReceiptTypeServerError:
		return whatsmeowv1.ReceiptType_RECEIPT_TYPE_SERVER_ERROR
	case types.ReceiptTypeInactive:
		return whatsmeowv1.ReceiptType_RECEIPT_TYPE_INACTIVE
	case types.ReceiptTypePeerMsg:
		return whatsmeowv1.ReceiptType_RECEIPT_TYPE_PEER_MSG
	case types.ReceiptTypeHistorySync:
		return whatsmeowv1.ReceiptType_RECEIPT_TYPE_HISTORY_SYNC
	default:
		return whatsmeowv1.ReceiptType_RECEIPT_TYPE_UNSPECIFIED
	}
}
