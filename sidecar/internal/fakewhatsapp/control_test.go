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
	"go.mau.fi/whatsmeow/types/events"
	"google.golang.org/grpc"
	"google.golang.org/grpc/credentials/insecure"
	"google.golang.org/grpc/metadata"
	"google.golang.org/grpc/test/bufconn"
	"google.golang.org/protobuf/proto"

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

func ack(index uint64, session, id string) *fakev1.Sent {
	return &fakev1.Sent{Index: index, SessionId: session, Kind: &fakev1.Sent_Ack{Ack: &fakev1.Ack{Class: "message", Id: id}}}
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

func TestADeliveredMessageIsAckedAfterTheHandlersSucceed(t *testing.T) {
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

	wantSent(t, waitForSent(ctx, t, control, "s1", 0), ack(1, "s1", "M1"))
}

func TestAFailedHandlerWithholdsTheAck(t *testing.T) {
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

	// M1 went first; the first thing sent being M2's ack shows M1 was never acked, without waiting for its absence.
	wantSent(t, waitForSent(ctx, t, control, "s1", 0), ack(1, "s1", "M2"))
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

func TestAPanickingHandlerIsAckedLikeWhatsmeow(t *testing.T) {
	t.Parallel()

	whatsApp, control, ctx := start(t)
	whatsApp.NewClient("s1").AddEventHandlerWithSuccessStatus(func(any) bool { panic("handler bug") })

	if _, err := control.DeliverMessage(ctx, &fakev1.DeliverMessageRequest{SessionId: "s1", Id: "M1"}); err != nil {
		t.Fatalf("DeliverMessage: %v", err)
	}

	// whatsmeow's dispatchEvent recovers the panic and reports no failure, so the ack goes out (ARCHITECTURE §7.3).
	wantSent(t, waitForSent(ctx, t, control, "s1", 0), ack(1, "s1", "M1"))
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
		wantSent(t, sent, ack(1, "s1", "M1"))
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

	wantSent(t, waitForSent(ctx, t, control, "s2", 0), ack(2, "s2", "M2"))
	wantSent(t, waitForSent(ctx, t, control, "s1", 1), ack(3, "s1", "M3"))
}

func TestDeliveringToASessionWithoutAClientFails(t *testing.T) {
	t.Parallel()

	_, control, ctx := start(t)

	_, err := control.DeliverMessage(ctx, &fakev1.DeliverMessageRequest{SessionId: "s1", Id: "M1"})
	if err == nil || !strings.Contains(err.Error(), ErrNoClient.Error()) {
		t.Fatalf("DeliverMessage = %v, want %v", err, ErrNoClient)
	}
}
