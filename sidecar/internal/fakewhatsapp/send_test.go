//go:build fakewhatsapp

package fakewhatsapp

import (
	"context"
	"errors"
	"testing"
	"time"

	"go.mau.fi/whatsmeow"
	"go.mau.fi/whatsmeow/proto/waE2E"
	"go.mau.fi/whatsmeow/types"
	"go.mau.fi/whatsmeow/types/events"
	"google.golang.org/protobuf/proto"
	"google.golang.org/protobuf/types/known/timestamppb"

	fakev1 "github.com/tcortega/Whatsmeow.NET/sidecar/gen/whatsmeow/fake/v1"
)

// sender is the kind of small interface the Messaging slice declares: whatsmeow's client and the fake's both have it.
type sender interface {
	SendMessage(ctx context.Context, to types.JID, message *waE2E.Message, extra ...whatsmeow.SendRequestExtra) (whatsmeow.SendResponse, error)
	MarkRead(ctx context.Context, ids []types.MessageID, timestamp time.Time, chat, sender types.JID, receiptTypeExtra ...types.ReceiptType) error
}

var (
	_ sender = (*whatsmeow.Client)(nil)
	_ sender = (*Client)(nil)
)

// connectedClient is s1's client, linked and connected, with the index of the last entry already logged.
func connectedClient(t *testing.T) (fakev1.FakeWhatsAppServiceClient, context.Context, *Client) {
	t.Helper()

	_, control, ctx, client, _, _ := linked(t)
	if err := client.ConnectContext(ctx); err != nil {
		t.Fatalf("ConnectContext: %v", err)
	}

	return control, ctx, client
}

func send(ctx context.Context, client *Client, to types.JID, id string) (whatsmeow.SendResponse, error) {
	return client.SendMessage(ctx, to, &waE2E.Message{Conversation: proto.String("hi")}, whatsmeow.SendRequestExtra{ID: id})
}

func TestASendToAPhoneNumberGoesToItsLIDAndIsLogged(t *testing.T) {
	t.Parallel()

	control, ctx, client := connectedClient(t)
	if _, err := control.RegisterUser(ctx, &fakev1.RegisterUserRequest{PhoneJid: "5511900000001@s.whatsapp.net", Lid: "900000001@lid"}); err != nil {
		t.Fatalf("RegisterUser: %v", err)
	}

	before := time.Now().Truncate(time.Second)

	resp, err := send(ctx, client, parse(t, "5511900000001@s.whatsapp.net"), "3EB0000000000000000001")
	if err != nil {
		t.Fatalf("SendMessage: %v", err)
	}

	sent := waitForSent(ctx, t, control, "s1", 0)
	want := &fakev1.Sent{Index: sent.GetIndex(), SessionId: "s1", Kind: &fakev1.Sent_Message{Message: &fakev1.SentMessage{
		Id: "3EB0000000000000000001", Chat: "900000001@lid", Message: &waE2E.Message{Conversation: proto.String("hi")},
		MessageTime: timestamppb.New(resp.Timestamp),
	}}}
	wantSent(t, sent, want)

	if resp.ID != "3EB0000000000000000001" || resp.Chat.String() != "900000001@lid" || resp.Timestamp.Before(before) ||
		resp.Timestamp.Nanosecond() != 0 {
		t.Fatalf("SendMessage = %+v, want the LID, now in whole seconds", resp)
	}

	// A LID and a group are sent to as given.
	for _, to := range []string{"900000002@lid", "120363000000000000@g.us"} {
		if resp, err := send(ctx, client, parse(t, to), "3EB0000000000000000002"); err != nil || resp.Chat.String() != to {
			t.Fatalf("SendMessage to %s = %v, %v", to, resp.Chat, err)
		}
	}
}

func TestASendToAnUnregisteredPhoneNumberIsNotOnWhatsAppAndSendsNothing(t *testing.T) {
	t.Parallel()

	control, ctx, client := connectedClient(t)

	// whatsmeow's own text, with the number in it (send.go:355-357).
	_, err := send(ctx, client, parse(t, "5511900000009@s.whatsapp.net"), "3EB0000000000000000001")
	if !errors.Is(err, whatsmeow.ErrRecipientNotOnWhatsApp) || err.Error() != "recipient is not on WhatsApp: no LID found for 5511900000009@s.whatsapp.net from server" {
		t.Fatalf("SendMessage = %v, want whatsmeow's ErrRecipientNotOnWhatsApp", err)
	}

	// Nothing went out: the next thing sent is the next send.
	if _, err := send(ctx, client, parse(t, "900000002@lid"), "3EB0000000000000000002"); err != nil {
		t.Fatalf("SendMessage: %v", err)
	}

	if got := waitForSent(ctx, t, control, "s1", 0); got.GetMessage().GetId() != "3EB0000000000000000002" {
		t.Fatalf("sent %v first, want the second send", got)
	}
}

func TestRateLimitNextSendsFailsThatManySendsBeforeTheirNode(t *testing.T) {
	t.Parallel()

	control, ctx, client := connectedClient(t)
	if _, err := control.RateLimitNextSends(ctx, &fakev1.RateLimitNextSendsRequest{SessionId: "s1", Count: 2}); err != nil {
		t.Fatalf("RateLimitNextSends: %v", err)
	}

	for range 2 {
		var iq *whatsmeow.IQError
		if _, err := send(ctx, client, parse(t, "900000002@lid"), "3EB0000000000000000001"); !errors.As(err, &iq) || iq.Code != 429 {
			t.Fatalf("SendMessage = %v, want an IQ 429", err)
		}
	}

	if _, err := send(ctx, client, parse(t, "900000002@lid"), "3EB0000000000000000001"); err != nil {
		t.Fatalf("the third SendMessage: %v", err)
	}

	if got := waitForSent(ctx, t, control, "s1", 0); got.GetMessage().GetId() != "3EB0000000000000000001" {
		t.Fatalf("sent %v, want only the third send", got)
	}
}

func TestAHeldSendIsLoggedThenAnsweredOnReleaseOrEndsWithItsContextOrTheConnection(t *testing.T) {
	t.Parallel()

	control, ctx, client := connectedClient(t)
	to := parse(t, "900000002@lid")

	type result struct {
		resp whatsmeow.SendResponse
		err  error
	}

	hold := func(sendCtx context.Context, id string) <-chan result {
		if _, err := control.HoldNextSend(ctx, &fakev1.HoldNextSendRequest{SessionId: "s1"}); err != nil {
			t.Fatalf("HoldNextSend: %v", err)
		}

		done := make(chan result, 1)

		go func() {
			resp, err := send(sendCtx, client, to, id)
			done <- result{resp, err}
		}()

		return done
	}

	// Released: accepted. Its node was logged first, while it waited.
	released := hold(ctx, "3EB0000000000000000001")
	first := waitForSent(ctx, t, control, "s1", 0)

	if _, err := control.ReleaseSend(ctx, &fakev1.ReleaseSendRequest{SessionId: "s1"}); err != nil {
		t.Fatalf("ReleaseSend: %v", err)
	}

	if r := <-released; r.err != nil || first.GetMessage().GetId() != "3EB0000000000000000001" {
		t.Fatalf("released send = %v, logged %v", r.err, first)
	}

	// Its context ended: ctx.Err(), as whatsmeow returns (send.go:451-454).
	sendCtx, cancel := context.WithCancel(ctx)
	cancelled := hold(sendCtx, "3EB0000000000000000002")
	second := waitForSent(ctx, t, control, "s1", first.GetIndex())
	cancel()

	if r := <-cancelled; !errors.Is(r.err, context.Canceled) || second.GetMessage().GetId() != "3EB0000000000000000002" {
		t.Fatalf("cancelled send = %v, logged %v", r.err, second)
	}

	// The connection ended: a DisconnectedError (request.go:185-202).
	dropped := hold(ctx, "3EB0000000000000000003")
	third := waitForSent(ctx, t, control, "s1", second.GetIndex())
	client.Disconnect()

	var disconnected *whatsmeow.DisconnectedError
	if r := <-dropped; !errors.As(r.err, &disconnected) || third.GetMessage().GetId() != "3EB0000000000000000003" {
		t.Fatalf("send on a dropped connection = %v, logged %v", r.err, third)
	}

	if _, err := control.ReleaseSend(ctx, &fakev1.ReleaseSendRequest{SessionId: "s1"}); err == nil {
		t.Fatal("ReleaseSend with no held send succeeded")
	}
}

func TestASendIsRefusedAsWhatsmeowRefusesIt(t *testing.T) {
	t.Parallel()

	control, ctx, client := connectedClient(t)
	partial := &waE2E.Message{ExtendedTextMessage: &waE2E.ExtendedTextMessage{Text: proto.String("hi"), EndCardTiles: []*waE2E.VideoEndCard{{}}}}

	if _, err := client.SendMessage(ctx, parse(t, "900000002@lid"), partial, whatsmeow.SendRequestExtra{ID: "3EB0000000000000000001"}); err == nil {
		t.Fatal("a message with unset required fields was sent")
	}

	if _, err := send(ctx, client, parse(t, "900000002:3@lid"), "3EB0000000000000000001"); !errors.Is(err, whatsmeow.ErrRecipientADJID) {
		t.Fatalf("a send to a device = %v, want ErrRecipientADJID", err)
	}

	client.Disconnect()

	if _, err := send(ctx, client, parse(t, "900000002@lid"), "3EB0000000000000000001"); !errors.Is(err, whatsmeow.ErrNotConnected) {
		t.Fatalf("a send while disconnected = %v, want ErrNotConnected", err)
	}

	// Only the disconnect was logged.
	if got := waitForSent(ctx, t, control, "s1", 0); got.GetDisconnect() == nil {
		t.Fatalf("sent %v, want only the disconnect", got)
	}
}

func TestMarkReadLogsTheReadReceipt(t *testing.T) {
	t.Parallel()

	control, ctx, client := connectedClient(t)

	if err := client.MarkRead(ctx, []types.MessageID{"M1", "M2"}, time.Now(), parse(t, "120363000000000000@g.us"), parse(t, "111:2@lid")); err != nil {
		t.Fatalf("MarkRead: %v", err)
	}

	// In a direct chat whatsmeow sets no participant (receipt.go:223-225).
	if err := client.MarkRead(ctx, []types.MessageID{"M3"}, time.Now(), parse(t, "111@lid"), parse(t, "111@lid")); err != nil {
		t.Fatalf("MarkRead: %v", err)
	}

	group := waitForSent(ctx, t, control, "s1", 0)
	wantSent(t, group, &fakev1.Sent{Index: group.GetIndex(), SessionId: "s1", Kind: &fakev1.Sent_Read{Read: &fakev1.ReadReceipt{
		Chat: "120363000000000000@g.us", Participant: "111@lid", MessageIds: []string{"M1", "M2"},
	}}})

	direct := waitForSent(ctx, t, control, "s1", group.GetIndex())
	wantSent(t, direct, &fakev1.Sent{Index: direct.GetIndex(), SessionId: "s1", Kind: &fakev1.Sent_Read{Read: &fakev1.ReadReceipt{
		Chat: "111@lid", MessageIds: []string{"M3"},
	}}})
}

func TestADeliveredReceiptIsAckedOnlyAfterTheHandlersSucceed(t *testing.T) {
	t.Parallel()

	whatsApp, control, ctx := start(t)
	fail := true
	got := make(chan *events.Receipt, 2)
	whatsApp.NewClient("s1").AddEventHandlerWithSuccessStatus(func(evt any) bool {
		got <- evt.(*events.Receipt)

		return !fail
	})

	at := time.Unix(1_700_000_100, 0)
	req := &fakev1.DeliverReceiptRequest{
		SessionId: "s1", Chat: "120363000000000000@g.us", Sender: "111:2@lid", SenderAlt: "5511999999999@s.whatsapp.net",
		IsFromMe: false, MessageIds: []string{"M1", "M2"}, Type: "read", ReceiptTime: timestamppb.New(at),
	}

	if _, err := control.DeliverReceipt(ctx, req); err != nil {
		t.Fatalf("DeliverReceipt: %v", err)
	}

	evt := <-got
	if evt.Chat.String() != "120363000000000000@g.us" || evt.Sender.String() != "111:2@lid" || evt.SenderAlt.String() != "5511999999999@s.whatsapp.net" ||
		!evt.IsGroup || evt.Type != types.ReceiptTypeRead || !evt.Timestamp.Equal(at) || len(evt.MessageIDs) != 2 || evt.MessageIDs[1] != "M2" {
		t.Fatalf("dispatched %+v", evt)
	}

	// The failed one is not acked: the next receipt's ack is the first thing sent.
	fail = false
	req.MessageIds, req.Type = []string{"M3"}, ""

	if _, err := control.DeliverReceipt(ctx, req); err != nil {
		t.Fatalf("DeliverReceipt: %v", err)
	}

	wantSent(t, waitForSent(ctx, t, control, "s1", 0), &fakev1.Sent{Index: 1, SessionId: "s1", Kind: &fakev1.Sent_Ack{Ack: &fakev1.Ack{Class: "receipt", Id: "M3"}}})
}

func TestARetryReceiptIsAckedWhateverTheHandlersReturn(t *testing.T) {
	t.Parallel()

	whatsApp, control, ctx := start(t)
	whatsApp.NewClient("s1").AddEventHandlerWithSuccessStatus(func(any) bool { return false })

	if _, err := control.DeliverReceipt(ctx, &fakev1.DeliverReceiptRequest{SessionId: "s1", Chat: "111@lid", Sender: "111@lid", MessageIds: []string{"M1"}, Type: "retry"}); err != nil {
		t.Fatalf("DeliverReceipt: %v", err)
	}

	wantSent(t, waitForSent(ctx, t, control, "s1", 0), &fakev1.Sent{Index: 1, SessionId: "s1", Kind: &fakev1.Sent_Ack{Ack: &fakev1.Ack{Class: "receipt", Id: "M1", Type: "retry"}}})

	if _, err := control.DeliverReceipt(ctx, &fakev1.DeliverReceiptRequest{SessionId: "s2", MessageIds: []string{"M1"}}); err == nil {
		t.Fatal("DeliverReceipt to a session without a client succeeded")
	}
}
