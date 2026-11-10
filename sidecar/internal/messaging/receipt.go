package messaging

import (
	"slices"
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
// SynchronousAck it does only when no handler failed (receipt.go:120-133; ARCHITECTURE §13 invariant 2). A retry
// receipt is recorded too, though whatsmeow acks it regardless (receipt.go:35-39). It is pure: no I/O.
//
// A status update's receipt is not recorded: status updates are a non-goal (ARCHITECTURE §1), and whatsmeow answers
// one the sidecar does not record (ADR 0057). An empty message id is left out, and a receipt with no other is not
// recorded: it covers no message, and the client cannot read one (#119). whatsmeow dispatches one for each member of a
// grouped receipt, whose id it reads from the participants' key, else their message_id (receipt.go:46-68), so it is
// empty only when a participants node has neither.
func Receipt(_ string, evt any) (string, *whatsmeowv1.StreamEventsResponse, bool) {
	e, ok := evt.(*waevents.Receipt)
	if !ok || e.Chat == types.StatusBroadcastJID {
		return "", nil, false
	}

	ids := slices.DeleteFunc(slices.Clone(e.MessageIDs), func(id string) bool { return id == "" })
	// whatsmeow dispatches none without an id (receipt.go:90-93), but a grouped one whose participants node has neither
	// key nor message_id covers the empty id only.
	if len(ids) == 0 {
		return "", nil, false
	}

	return receiptKey(e, ids), &whatsmeowv1.StreamEventsResponse{Payload: &whatsmeowv1.StreamEventsResponse_Receipt{
		Receipt: &whatsmeowv1.ReceiptEvent{
			Source: &whatsmeowv1.MessageSource{
				Chat:         server.Text(e.Chat.String()),
				Sender:       server.Text(e.Sender.String()),
				SenderAlt:    server.Text(e.SenderAlt.String()),
				RecipientAlt: server.Text(e.RecipientAlt.String()),
				IsFromMe:     e.IsFromMe,
			},
			MessageIds:    server.Texts(ids),
			ReceiptTime:   timestamppb.New(e.Timestamp),
			Type:          receiptType(e.Type),
			MessageSender: server.Text(e.MessageSender.String()),
		},
	}}, true
}

// receiptKey is a receipt's natural key: its chat, sender, type, time and the message ids it covers, so WhatsApp's
// redelivery of a receipt that was not acked keeps its seq, and a later receipt of the same kind for the same message
// is one of its own. The ids come last, so their number never makes two keys alike.
func receiptKey(e *waevents.Receipt, ids []string) string {
	parts := make([]string, 0, 5+len(ids))
	parts = append(parts, "r", e.Chat.String(), e.Sender.String(), string(e.Type), strconv.FormatInt(e.Timestamp.Unix(), 10))
	parts = append(parts, ids...)

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
