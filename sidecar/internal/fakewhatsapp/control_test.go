//go:build fakewhatsapp

package fakewhatsapp

import (
	"context"
	"net"
	"runtime"
	"strings"
	"testing"
	"time"

	"github.com/rs/zerolog"
	"go.mau.fi/whatsmeow"
	"go.mau.fi/whatsmeow/proto/waE2E"
	"go.mau.fi/whatsmeow/types/events"
	"google.golang.org/grpc"
	"google.golang.org/grpc/credentials/insecure"
	"google.golang.org/grpc/metadata"
	"google.golang.org/grpc/test/bufconn"
	"google.golang.org/protobuf/proto"
	"google.golang.org/protobuf/types/known/timestamppb"

	fakev1 "github.com/tcortega/Whatsmeow.NET/sidecar/gen/whatsmeow/fake/v1"
	"github.com/tcortega/Whatsmeow.NET/sidecar/internal/server"
)

const token = "00112233445566778899aabbccddeeff00112233445566778899aabbccddeeff"

// eventSource is the kind of small interface a slice declares (docs/guidelines/go.md): whatsmeow's client and the
// fake's both satisfy it.
type eventSource interface {
	AddEventHandlerWithSuccessStatus(handler whatsmeow.EventHandlerWithSuccessStatus) uint32
}

var (
	_ eventSource = (*whatsmeow.Client)(nil)
	_ eventSource = (*Client)(nil)
)

// start serves the control service on the real server (token check, error mapping) over an in-memory listener.
func start(t *testing.T) (*WhatsApp, fakev1.FakeWhatsAppServiceClient, context.Context) {
	t.Helper()

	whatsApp := New(zerolog.Nop())
	srv := server.New(token, zerolog.Nop())
	fakev1.RegisterFakeWhatsAppServiceServer(srv, NewControl(whatsApp))

	lis := bufconn.Listen(1 << 20)
	served := make(chan error, 1)

	go func() { served <- srv.Serve(lis) }()

	t.Cleanup(func() {
		srv.Stop()

		if err := <-served; err != nil {
			t.Errorf("serve: %v", err)
		}
	})

	conn, err := grpc.NewClient("passthrough:///bufnet",
		grpc.WithContextDialer(func(ctx context.Context, _ string) (net.Conn, error) { return lis.DialContext(ctx) }),
		grpc.WithTransportCredentials(insecure.NewCredentials()))
	if err != nil {
		t.Fatalf("dial: %v", err)
	}

	t.Cleanup(func() { _ = conn.Close() })

	// A bound for a broken fake, never a wait: every wait below ends on a condition.
	ctx, cancel := context.WithTimeout(context.Background(), 30*time.Second)
	t.Cleanup(cancel)

	return whatsApp, fakev1.NewFakeWhatsAppServiceClient(conn), metadata.AppendToOutgoingContext(ctx, "authorization", "Bearer "+token)
}

// receipt is what whatsmeow sends for a message from someone else once its handlers succeeded: a receipt of type
// inactive, since the client sends no active receipts (receipt.go:280-296).
func receipt(index uint64, session, id string) *fakev1.Sent {
	return &fakev1.Sent{Index: index, SessionId: session, Kind: &fakev1.Sent_Receipt{Receipt: &fakev1.Receipt{Id: id, Type: "inactive"}}}
}

func waitForSent(ctx context.Context, t *testing.T, control fakev1.FakeWhatsAppServiceClient, session string, after uint64) *fakev1.Sent {
	t.Helper()

	resp, err := control.WaitForSent(ctx, &fakev1.WaitForSentRequest{SessionId: session, After: after})
	if err != nil {
		t.Fatalf("WaitForSent: %v", err)
	}

	return resp.GetSent()
}

func wantSent(t *testing.T, got, want *fakev1.Sent) {
	t.Helper()

	if !proto.Equal(got, want) {
		t.Fatalf("sent = %v, want %v", got, want)
	}
}

func TestADeliveredMessageGetsAReceiptAfterTheHandlersSucceed(t *testing.T) {
	t.Parallel()

	whatsApp, control, ctx := start(t)
	handled := make(chan string, 1)
	whatsApp.NewClient("s1").AddEventHandlerWithSuccessStatus(func(evt any) bool {
		handled <- string(evt.(*events.Message).Info.ID)

		return true
	})

	if _, err := control.DeliverMessage(ctx, &fakev1.DeliverMessageRequest{SessionId: "s1", Id: "M1"}); err != nil {
		t.Fatalf("DeliverMessage: %v", err)
	}

	// DeliverMessage returns once the handlers ran.
	select {
	case id := <-handled:
		if id != "M1" {
			t.Fatalf("the handler saw message %q, want M1", id)
		}
	default:
		t.Fatal("DeliverMessage returned before the handler ran")
	}

	wantSent(t, waitForSent(ctx, t, control, "s1", 0), receipt(1, "s1", "M1"))
}

func TestAFailedHandlerWithholdsTheReceipt(t *testing.T) {
	t.Parallel()

	whatsApp, control, ctx := start(t)
	whatsApp.NewClient("s1").AddEventHandlerWithSuccessStatus(func(evt any) bool {
		return evt.(*events.Message).Info.ID != "M1"
	})

	for _, id := range []string{"M1", "M2"} {
		if _, err := control.DeliverMessage(ctx, &fakev1.DeliverMessageRequest{SessionId: "s1", Id: id}); err != nil {
			t.Fatalf("DeliverMessage %s: %v", id, err)
		}
	}

	// M1 went first; the first thing sent being M2's receipt shows M1 never got one, without waiting for its absence.
	wantSent(t, waitForSent(ctx, t, control, "s1", 0), receipt(1, "s1", "M2"))
}

func TestHandlersRunInOrderAndTheFirstFailureStopsThem(t *testing.T) {
	t.Parallel()

	whatsApp, control, ctx := start(t)
	client := whatsApp.NewClient("s1")

	var ran []string

	client.AddEventHandlerWithSuccessStatus(func(any) bool { ran = append(ran, "first"); return false })
	client.AddEventHandlerWithSuccessStatus(func(any) bool { ran = append(ran, "second"); return true })

	if _, err := control.DeliverMessage(ctx, &fakev1.DeliverMessageRequest{SessionId: "s1", Id: "M1"}); err != nil {
		t.Fatalf("DeliverMessage: %v", err)
	}

	if strings.Join(ran, ",") != "first" {
		t.Fatalf("handlers ran: %v, want only the first", ran)
	}
}

func TestAPanickingHandlerGetsAReceiptLikeWhatsmeow(t *testing.T) {
	t.Parallel()

	whatsApp, control, ctx := start(t)
	whatsApp.NewClient("s1").AddEventHandlerWithSuccessStatus(func(any) bool { panic("handler bug") })

	if _, err := control.DeliverMessage(ctx, &fakev1.DeliverMessageRequest{SessionId: "s1", Id: "M1"}); err != nil {
		t.Fatalf("DeliverMessage: %v", err)
	}

	// whatsmeow's dispatchEvent recovers the panic and reports no failure, so the receipt goes out (ARCHITECTURE §7.3).
	wantSent(t, waitForSent(ctx, t, control, "s1", 0), receipt(1, "s1", "M1"))
}

func TestWaitForSentWaitsForTheNextEntry(t *testing.T) {
	t.Parallel()

	whatsApp, control, ctx := start(t)
	whatsApp.NewClient("s1").AddEventHandlerWithSuccessStatus(func(any) bool { return true })

	got := make(chan *fakev1.Sent, 1)
	failed := make(chan error, 1)

	go func() {
		resp, err := control.WaitForSent(ctx, &fakev1.WaitForSentRequest{SessionId: "s1"})
		if err != nil {
			failed <- err

			return
		}

		got <- resp.GetSent()
	}()

	// Deliver only once the wait is blocked in the fake, so it is the wait that sees the entry arrive.
	for whatsApp.waiters() == 0 {
		select {
		case sent := <-got:
			t.Fatalf("WaitForSent returned %v before anything was sent", sent)
		case err := <-failed:
			t.Fatalf("WaitForSent: %v", err)
		default:
			runtime.Gosched()
		}
	}

	if _, err := control.DeliverMessage(ctx, &fakev1.DeliverMessageRequest{SessionId: "s1", Id: "M1"}); err != nil {
		t.Fatalf("DeliverMessage: %v", err)
	}

	select {
	case sent := <-got:
		wantSent(t, sent, receipt(1, "s1", "M1"))
	case err := <-failed:
		t.Fatalf("WaitForSent: %v", err)
	}
}

func TestWaitForSentReturnsOnlyItsSessionAfterTheCursor(t *testing.T) {
	t.Parallel()

	whatsApp, control, ctx := start(t)
	for _, session := range []string{"s1", "s2"} {
		whatsApp.NewClient(session).AddEventHandlerWithSuccessStatus(func(any) bool { return true })
	}

	for _, delivery := range []struct{ session, id string }{{"s1", "M1"}, {"s2", "M2"}, {"s1", "M3"}} {
		if _, err := control.DeliverMessage(ctx, &fakev1.DeliverMessageRequest{SessionId: delivery.session, Id: delivery.id}); err != nil {
			t.Fatalf("DeliverMessage %s: %v", delivery.id, err)
		}
	}

	wantSent(t, waitForSent(ctx, t, control, "s2", 0), receipt(2, "s2", "M2"))
	wantSent(t, waitForSent(ctx, t, control, "s1", 1), receipt(3, "s1", "M3"))
}

func TestDeliveringToASessionWithoutAClientFails(t *testing.T) {
	t.Parallel()

	_, control, ctx := start(t)

	_, err := control.DeliverMessage(ctx, &fakev1.DeliverMessageRequest{SessionId: "s1", Id: "M1"})
	if err == nil || !strings.Contains(err.Error(), ErrNoClient.Error()) {
		t.Fatalf("DeliverMessage = %v, want %v", err, ErrNoClient)
	}
}

func TestDeliverMessageDispatchesItsInfoAndText(t *testing.T) {
	t.Parallel()

	whatsApp, control, ctx := start(t)
	handled := make(chan *events.Message, 1)
	whatsApp.NewClient("s1").AddEventHandlerWithSuccessStatus(func(evt any) bool {
		handled <- evt.(*events.Message)

		return true
	})

	at := time.Unix(1_700_000_000, 0)
	if _, err := control.DeliverMessage(ctx, &fakev1.DeliverMessageRequest{
		SessionId: "s1", Id: "M1", Chat: "120363000000000000@g.us", Sender: "111@lid", SenderAlt: "5511999999999@s.whatsapp.net",
		RecipientAlt: "222@lid", IsFromMe: true, PushName: "Ana", Text: "hi", MessageTime: timestamppb.New(at),
	}); err != nil {
		t.Fatalf("DeliverMessage: %v", err)
	}

	var evt *events.Message

	select {
	case evt = <-handled:
	case <-ctx.Done():
		t.Fatal("DeliverMessage dispatched nothing")
	}

	info := evt.Info
	if info.Chat.String() != "120363000000000000@g.us" || !info.IsGroup || info.Sender.String() != "111@lid" ||
		info.SenderAlt.String() != "5511999999999@s.whatsapp.net" || info.RecipientAlt.String() != "222@lid" || !info.IsFromMe ||
		info.ID != "M1" || info.PushName != "Ana" || !info.Timestamp.Equal(at) || evt.Message.GetConversation() != "hi" {
		t.Fatalf("dispatched %+v with %v", info, evt.Message)
	}
}

func TestDeliverUndecryptableMessageDispatchesItAndRecordsNoReceipt(t *testing.T) {
	t.Parallel()

	whatsApp, control, ctx := start(t)
	handled := make(chan any, 2)
	whatsApp.NewClient("s1").AddEventHandlerWithSuccessStatus(func(evt any) bool {
		handled <- evt

		return true
	})

	at := time.Unix(1_700_000_000, 0)
	if _, err := control.DeliverUndecryptableMessage(ctx, &fakev1.DeliverUndecryptableMessageRequest{
		SessionId: "s1", Id: "M1", Chat: "111@s.whatsapp.net", Sender: "111@s.whatsapp.net", HidePlaceholder: true, MessageTime: timestamppb.New(at),
	}); err != nil {
		t.Fatalf("DeliverUndecryptableMessage: %v", err)
	}

	var got any

	select {
	case got = <-handled:
	case <-ctx.Done():
		t.Fatal("DeliverUndecryptableMessage dispatched nothing")
	}

	evt, ok := got.(*events.UndecryptableMessage)
	if !ok || evt.Info.ID != "M1" || evt.Info.Chat.String() != "111@s.whatsapp.net" || evt.Info.Sender.String() != "111@s.whatsapp.net" ||
		evt.DecryptFailMode != events.DecryptFailHide || !evt.Info.Timestamp.Equal(at) {
		t.Fatalf("dispatched %+v", evt)
	}

	// The resend that decrypts is a normal message: its receipt is the session's first entry.
	if _, err := control.DeliverMessage(ctx, &fakev1.DeliverMessageRequest{SessionId: "s1", Id: "M1"}); err != nil {
		t.Fatalf("DeliverMessage: %v", err)
	}

	wantSent(t, waitForSent(ctx, t, control, "s1", 0), receipt(1, "s1", "M1"))
}

func TestDeliveringAnUndecryptableMessageToASessionWithoutAClientFails(t *testing.T) {
	t.Parallel()

	_, control, ctx := start(t)
	if _, err := control.DeliverUndecryptableMessage(ctx, &fakev1.DeliverUndecryptableMessageRequest{SessionId: "nope", Id: "M1"}); err == nil {
		t.Fatal("DeliverUndecryptableMessage to a session without a client succeeded")
	}
}

func TestASenderKeyDeliveryDispatchesTheSenderKeyThenTheContentAndAnswersOnce(t *testing.T) {
	t.Parallel()

	whatsApp, control, ctx := start(t)
	handled := make(chan *events.Message, 2)
	whatsApp.NewClient("s1").AddEventHandlerWithSuccessStatus(func(evt any) bool {
		handled <- evt.(*events.Message)

		return true
	})

	if _, err := control.DeliverMessage(ctx, &fakev1.DeliverMessageRequest{
		SessionId: "s1", Id: "M1", Chat: "120363000000000000@g.us", Sender: "111@lid", Text: "hi", SenderKey: true,
	}); err != nil {
		t.Fatalf("DeliverMessage: %v", err)
	}

	first, second := <-handled, <-handled
	if first.Message.GetSenderKeyDistributionMessage() == nil || first.Message.GetConversation() != "" ||
		second.Message.GetConversation() != "hi" || first.Info.ID != "M1" || second.Info.ID != "M1" {
		t.Fatalf("dispatched %v then %v; want the sender key, then the content, both M1", first.Message, second.Message)
	}

	wantSent(t, waitForSent(ctx, t, control, "s1", 0), receipt(1, "s1", "M1"))
}

func TestAnOwnMessageGetsASenderReceipt(t *testing.T) {
	t.Parallel()

	whatsApp, control, ctx := start(t)
	whatsApp.NewClient("s1").AddEventHandlerWithSuccessStatus(func(any) bool { return true })

	// whatsmeow answers a message another of the account's devices sent with type sender (receipt.go:282-286).
	if _, err := control.DeliverMessage(ctx, &fakev1.DeliverMessageRequest{SessionId: "s1", Id: "M1", IsFromMe: true}); err != nil {
		t.Fatalf("DeliverMessage: %v", err)
	}

	wantSent(t, waitForSent(ctx, t, control, "s1", 0),
		&fakev1.Sent{Index: 1, SessionId: "s1", Kind: &fakev1.Sent_Receipt{Receipt: &fakev1.Receipt{Id: "M1", Type: "sender"}}})
}

func TestARawDeliveryIsUnwrappedAsWhatsmeowUnwrapsIt(t *testing.T) {
	t.Parallel()

	whatsApp, control, ctx := start(t)
	handled := make(chan *events.Message, 2)
	whatsApp.NewClient("s1").AddEventHandlerWithSuccessStatus(func(evt any) bool {
		handled <- evt.(*events.Message)

		return true
	})

	image := &waE2E.ImageMessage{Caption: proto.String("once")}
	document := &waE2E.DocumentMessage{FileName: proto.String("a.pdf"), Caption: proto.String("read me")}

	for _, raw := range []*waE2E.Message{
		{ViewOnceMessageV2: &waE2E.FutureProofMessage{Message: &waE2E.Message{ImageMessage: image}}},
		{DocumentWithCaptionMessage: &waE2E.FutureProofMessage{Message: &waE2E.Message{DocumentMessage: document}}},
	} {
		if _, err := control.DeliverMessage(ctx, &fakev1.DeliverMessageRequest{SessionId: "s1", Id: "M1", Chat: "111@lid", Sender: "111@lid", Message: raw}); err != nil {
			t.Fatalf("DeliverMessage: %v", err)
		}
	}

	viewOnce, captioned := <-handled, <-handled
	if !proto.Equal(viewOnce.Message, &waE2E.Message{ImageMessage: image}) || !viewOnce.IsViewOnce || !viewOnce.IsViewOnceV2 ||
		viewOnce.RawMessage.GetViewOnceMessageV2() == nil {
		t.Fatalf("the view-once delivery dispatched %+v", viewOnce)
	}

	if !proto.Equal(captioned.Message, &waE2E.Message{DocumentMessage: document}) || !captioned.IsDocumentWithCaption {
		t.Fatalf("the captioned document dispatched %+v", captioned)
	}
}

func TestAnUnavailableDeliveryDispatchesItsType(t *testing.T) {
	t.Parallel()

	whatsApp, control, ctx := start(t)
	handled := make(chan *events.UndecryptableMessage, 1)
	whatsApp.NewClient("s1").AddEventHandlerWithSuccessStatus(func(evt any) bool {
		handled <- evt.(*events.UndecryptableMessage)

		return true
	})

	if _, err := control.DeliverUndecryptableMessage(ctx, &fakev1.DeliverUndecryptableMessageRequest{
		SessionId: "s1", Id: "M1", Chat: "111@lid", Sender: "111@lid", Unavailable: true, UnavailableType: "view_once",
	}); err != nil {
		t.Fatalf("DeliverUndecryptableMessage: %v", err)
	}

	if evt := <-handled; !evt.IsUnavailable || evt.UnavailableType != events.UnavailableTypeViewOnce || evt.Info.ID != "M1" {
		t.Fatalf("dispatched %+v", evt)
	}
}
