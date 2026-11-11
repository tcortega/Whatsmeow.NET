//go:build fakewhatsapp

package fakewhatsapp

import (
	"errors"
	"testing"

	"go.mau.fi/whatsmeow"
	"go.mau.fi/whatsmeow/proto/waE2E"
	"go.mau.fi/whatsmeow/types"
	"go.mau.fi/whatsmeow/types/events"
	"google.golang.org/protobuf/proto"

	fakev1 "github.com/tcortega/Whatsmeow.NET/sidecar/gen/whatsmeow/fake/v1"
)

// builder is the part of the Messaging slice's interface that builds reactions, revokes and edits.
type builder interface {
	BuildReaction(chat, sender types.JID, id types.MessageID, reaction string) *waE2E.Message
	BuildRevoke(chat, sender types.JID, id types.MessageID) *waE2E.Message
	BuildEdit(chat types.JID, id types.MessageID, newContent *waE2E.Message) *waE2E.Message
}

var (
	_ builder = (*whatsmeow.Client)(nil)
	_ builder = (*Client)(nil)
)

// untimed is message without the times the builders stamp (send.go:551-635), so two builds compare.
func untimed(message *waE2E.Message) *waE2E.Message {
	m := proto.CloneOf(message)
	if r := m.GetReactionMessage(); r != nil {
		r.SenderTimestampMS = nil
	}

	if p := m.GetEditedMessage().GetMessage().GetProtocolMessage(); p != nil {
		p.TimestampMS = nil
	}

	return m
}

func TestTheBuildersBuildWhatWhatsmeowsBuild(t *testing.T) {
	t.Parallel()

	_, _, _, client, _, _ := linked(t)
	client.Store.LID = parse(t, "900000009:3@lid")
	// whatsmeow's own, on the same device: never connected, only its builders run.
	wm := whatsmeow.NewClient(client.Store, nil)
	content := &waE2E.Message{Conversation: proto.String("fixed")}

	for _, chat := range []string{"120363000000000000@g.us", "111@lid", "5511900000002@s.whatsapp.net"} {
		for _, sender := range []string{"", jid, "900000009@lid", "5511900000002:4@s.whatsapp.net", "111@lid"} {
			var from types.JID
			if sender != "" {
				from = parse(t, sender)
			}

			to := parse(t, chat)
			pairs := [][2]*waE2E.Message{
				{client.BuildReaction(to, from, "ABC", "👍"), wm.BuildReaction(to, from, "ABC", "👍")},
				{client.BuildRevoke(to, from, "ABC"), wm.BuildRevoke(to, from, "ABC")},
				{client.BuildEdit(to, "ABC", content), wm.BuildEdit(to, "ABC", content)},
			}

			for _, p := range pairs {
				if !proto.Equal(untimed(p[0]), untimed(p[1])) {
					t.Errorf("in %s for %q: the fake built %v, whatsmeow %v", chat, sender, p[0], p[1])
				}
			}

			if client.BuildReaction(to, from, "ABC", "👍").GetReactionMessage().GetSenderTimestampMS() == 0 ||
				client.BuildEdit(to, "ABC", content).GetEditedMessage().GetMessage().GetProtocolMessage().GetTimestampMS() == 0 {
				t.Error("the fake stamps no time")
			}
		}
	}
}

func TestRefuseNextSendAnswersTheNextNodeWithItsCode(t *testing.T) {
	t.Parallel()

	control, ctx, client := connectedClient(t)
	if _, err := control.RefuseNextSend(ctx, &fakev1.RefuseNextSendRequest{SessionId: "s1", Code: 479}); err != nil {
		t.Fatalf("RefuseNextSend: %v", err)
	}

	// As whatsmeow reads an answer with an error attribute (send.go:468-470): the node went out.
	_, err := send(ctx, client, parse(t, "900000002@lid"), "3EB0000000000000000001")
	if !errors.Is(err, whatsmeow.ErrServerReturnedError) || err.Error() != "server returned error 479" {
		t.Fatalf("the refused send = %v, want whatsmeow's server error 479", err)
	}

	first := waitForSent(ctx, t, control, "s1", 0)
	if first.GetMessage().GetId() != "3EB0000000000000000001" {
		t.Fatalf("sent %v, want the refused message's node", first)
	}

	// Only the next one.
	if _, err := send(ctx, client, parse(t, "900000002@lid"), "3EB0000000000000000002"); err != nil {
		t.Fatalf("the send after = %v", err)
	}

	if _, err := control.RefuseNextSend(ctx, &fakev1.RefuseNextSendRequest{SessionId: "s1"}); err == nil {
		t.Fatal("a refusal without a code was taken")
	}
}

// A send both held and refused is refused at once, and takes both with it: nothing stays held or refused for the next.
func TestAHeldAndRefusedSendIsRefusedAndLeavesNeither(t *testing.T) {
	t.Parallel()

	control, ctx, client := connectedClient(t)
	if _, err := control.HoldNextSend(ctx, &fakev1.HoldNextSendRequest{SessionId: "s1"}); err != nil {
		t.Fatalf("HoldNextSend: %v", err)
	}

	if _, err := control.RefuseNextSend(ctx, &fakev1.RefuseNextSendRequest{SessionId: "s1", Code: 479}); err != nil {
		t.Fatalf("RefuseNextSend: %v", err)
	}

	if _, err := send(ctx, client, parse(t, "900000002@lid"), "3EB0000000000000000001"); !errors.Is(err, whatsmeow.ErrServerReturnedError) {
		t.Fatalf("the held and refused send = %v, want whatsmeow's server error", err)
	}

	if _, err := control.ReleaseSend(ctx, &fakev1.ReleaseSendRequest{SessionId: "s1"}); err == nil {
		t.Fatal("a send stayed held after the refused one")
	}

	if _, err := send(ctx, client, parse(t, "900000002@lid"), "3EB0000000000000000002"); err != nil {
		t.Fatalf("the send after = %v", err)
	}
}

// A send takes the refusal before it shows in WaitForSent, so a test that saw it there and scripts the next refusal
// never has this send take that one instead.
func TestASendTakesTheRefusalBeforeItShowsInWaitForSent(t *testing.T) {
	t.Parallel()

	whatsApp, control, ctx, client, _, _ := linked(t)
	if err := client.ConnectContext(ctx); err != nil {
		t.Fatalf("ConnectContext: %v", err)
	}

	if _, err := control.RefuseNextSend(ctx, &fakev1.RefuseNextSendRequest{SessionId: "s1", Code: 479}); err != nil {
		t.Fatalf("RefuseNextSend: %v", err)
	}

	to := parse(t, "900000002@lid")
	ended := make(chan error, 1)

	// The goroutine only reports what the send returned; the test goroutine fails on it.
	go func() {
		_, err := send(ctx, client, to, "3EB0000000000000000001")
		ended <- err
	}()

	// Each look reads the log and the refusal under one lock, so the first look that sees the send sees the refusal
	// as it was when the send showed. The test's context bounds the looks.
	for {
		shown, refusalLeft := sendShown(whatsApp, "s1")
		if !shown {
			if ctx.Err() != nil {
				t.Fatalf("the send never showed in WaitForSent: %v", ctx.Err())
			}

			continue
		}

		if refusalLeft {
			t.Fatal("the send showed in WaitForSent before it took the refusal")
		}

		break
	}

	if err := <-ended; !errors.Is(err, whatsmeow.ErrServerReturnedError) || err.Error() != "server returned error 479" {
		t.Fatalf("the refused send = %v, want whatsmeow's server error 479", err)
	}
}

// sendShown reports, under the fake's lock, whether the session's log has a message node and whether its refusal is
// still scripted.
func sendShown(w *WhatsApp, sessionID string) (shown, refusalLeft bool) {
	w.mu.Lock()
	defer w.mu.Unlock()

	for _, sent := range w.sent {
		if sent.GetSessionId() == sessionID && sent.GetMessage() != nil {
			shown = true
		}
	}

	s, ok := w.scripts[sessionID]

	return shown, ok && s.refuseSend != 0
}

func TestADeliveredReceiptCarriesTheSenderOfItsMessages(t *testing.T) {
	t.Parallel()

	whatsApp, control, ctx := start(t)
	got := make(chan *events.Receipt, 1)
	whatsApp.NewClient("s1").AddEventHandlerWithSuccessStatus(func(evt any) bool {
		got <- evt.(*events.Receipt)

		return true
	})

	if _, err := control.DeliverReceipt(ctx, &fakev1.DeliverReceiptRequest{
		SessionId: "s1", Chat: "120363000000000000@g.us", Sender: "111:2@lid", IsFromMe: true, MessageIds: []string{"M1"},
		Type: "read-self", MessageSender: "5511900000002@s.whatsapp.net",
	}); err != nil {
		t.Fatalf("DeliverReceipt: %v", err)
	}

	if evt := <-got; evt.MessageSender.String() != "5511900000002@s.whatsapp.net" {
		t.Fatalf("dispatched %+v, want the messages' sender", evt)
	}
}
