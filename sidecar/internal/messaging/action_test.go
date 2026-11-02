package messaging

import (
	"strings"
	"testing"

	"go.mau.fi/whatsmeow/proto/waE2E"
	"go.mau.fi/whatsmeow/types"
	"google.golang.org/grpc/codes"
	"google.golang.org/grpc/status"
	"google.golang.org/protobuf/proto"

	whatsmeowv1 "github.com/tcortega/Whatsmeow.NET/sidecar/gen/whatsmeow/v1"
)

const (
	group  = "120363000000000000@g.us"
	member = "5511900000002:7@s.whatsapp.net"
)

// withoutTimes is message without the times whatsmeow's builders stamp (send.go:551-635), so two builds compare.
func withoutTimes(message *waE2E.Message) *waE2E.Message {
	m := proto.CloneOf(message)
	if r := m.GetReactionMessage(); r != nil {
		r.SenderTimestampMS = nil
	}

	if p := m.GetEditedMessage().GetMessage().GetProtocolMessage(); p != nil {
		p.TimestampMS = nil
	}

	return m
}

func (h *harness) only(t *testing.T) call {
	t.Helper()

	sent := h.stub.sent()
	if len(sent) != 1 {
		t.Fatalf("whatsmeow got %v, want one send", h.stub.ids())
	}

	return sent[0]
}

func TestSendReactionSendsWhatsmeowsReaction(t *testing.T) {
	t.Parallel()

	cases := []struct {
		name, chat, sender, emoji string
	}{
		{"a member's group message", group, member, "👍"},
		{"the account's own message", "111@lid", "", "👍"},
		{"the account's own message by its JID", group, self, "🐈"},
		{"a removal", "111@lid", "111@lid", ""},
	}

	for _, c := range cases {
		t.Run(c.name, func(t *testing.T) {
			t.Parallel()

			h := start(t)

			resp, err := h.client.SendReaction(h.ctx, &whatsmeowv1.SendReactionRequest{
				SessionId: "s1", Chat: c.chat, ClientMessageId: id(1), TargetId: "ABC", TargetSender: c.sender, Emoji: c.emoji,
			})
			if err != nil || resp.GetChat() != c.chat || !resp.GetMessageTime().AsTime().Equal(at) {
				t.Fatalf("SendReaction = %v, %v; want %s at %v", resp, err, c.chat, at)
			}

			var sender types.JID
			if c.sender != "" {
				sender = jid(c.sender)
			}

			sent := h.only(t)
			want := h.stub.wa.BuildReaction(jid(c.chat), sender, "ABC", c.emoji)

			if sent.to != jid(c.chat) || sent.id != id(1) || !proto.Equal(withoutTimes(sent.message), withoutTimes(want)) || sent.message.GetReactionMessage().GetSenderTimestampMS() == 0 {
				t.Fatalf("whatsmeow got %v to %v, want whatsmeow's own reaction %v", sent.message, sent.to, want)
			}
		})
	}
}

func TestSendEditSendsWhatsmeowsEditOfTheText(t *testing.T) {
	t.Parallel()

	h := start(t)

	resp, err := h.client.SendEdit(h.ctx, &whatsmeowv1.SendEditRequest{SessionId: "s1", Chat: "111@lid", ClientMessageId: id(1), TargetId: "ABC", Text: "fixed"})
	if err != nil || resp.GetChat() != "111@lid" || !resp.GetMessageTime().AsTime().Equal(at) {
		t.Fatalf("SendEdit = %v, %v; want 111@lid at %v", resp, err, at)
	}

	sent := h.only(t)
	want := h.stub.wa.BuildEdit(jid("111@lid"), "ABC", &waE2E.Message{Conversation: proto.String("fixed")})

	if sent.id != id(1) || !proto.Equal(withoutTimes(sent.message), withoutTimes(want)) {
		t.Fatalf("whatsmeow got %v, want whatsmeow's own edit %v", sent.message, want)
	}
}

func TestSendRevokeSendsWhatsmeowsRevokeOfTheOwnOrAMembersMessage(t *testing.T) {
	t.Parallel()

	for _, sender := range []string{"", self, member} {
		h := start(t)

		if _, err := h.client.SendRevoke(h.ctx, &whatsmeowv1.SendRevokeRequest{SessionId: "s1", Chat: group, ClientMessageId: id(1), TargetId: "ABC", TargetSender: sender}); err != nil {
			t.Fatalf("SendRevoke of %q's: %v", sender, err)
		}

		var from types.JID
		if sender != "" {
			from = jid(sender)
		}

		if sent := h.only(t); !proto.Equal(sent.message, h.stub.wa.BuildRevoke(jid(group), from, "ABC")) {
			t.Fatalf("whatsmeow got %v, want whatsmeow's own revoke of %q's message", sent.message, sender)
		}
	}
}

func TestAMalformedActionIsInvalidAndSendsNothing(t *testing.T) {
	t.Parallel()

	h := start(t)
	reaction := func(change func(*whatsmeowv1.SendReactionRequest)) error {
		req := &whatsmeowv1.SendReactionRequest{SessionId: "s1", Chat: group, ClientMessageId: id(1), TargetId: "ABC", Emoji: "👍"}
		change(req)
		_, err := h.client.SendReaction(h.ctx, req)

		return err //nolint:wrapcheck // the test reads the status
	}
	revoke := func(change func(*whatsmeowv1.SendRevokeRequest)) error {
		req := &whatsmeowv1.SendRevokeRequest{SessionId: "s1", Chat: group, ClientMessageId: id(1), TargetId: "ABC"}
		change(req)
		_, err := h.client.SendRevoke(h.ctx, req)

		return err //nolint:wrapcheck // the test reads the status
	}
	edit := func(change func(*whatsmeowv1.SendEditRequest)) error {
		req := &whatsmeowv1.SendEditRequest{SessionId: "s1", Chat: group, ClientMessageId: id(1), TargetId: "ABC", Text: "fixed"}
		change(req)
		_, err := h.client.SendEdit(h.ctx, req)

		return err //nolint:wrapcheck // the test reads the status
	}

	cases := []struct {
		err   error
		field string
	}{
		{reaction(func(r *whatsmeowv1.SendReactionRequest) { r.TargetId = "" }), "target_id"},
		{reaction(func(r *whatsmeowv1.SendReactionRequest) { r.TargetSender = group }), "target_sender"},
		{reaction(func(r *whatsmeowv1.SendReactionRequest) { r.TargetSender = "not a jid@" }), "target_sender"},
		{reaction(func(r *whatsmeowv1.SendReactionRequest) { r.ClientMessageId = "1" }), "client_message_id"},
		{revoke(func(r *whatsmeowv1.SendRevokeRequest) { r.TargetId = "" }), "target_id"},
		{revoke(func(r *whatsmeowv1.SendRevokeRequest) { r.TargetSender = group }), "target_sender"},
		{revoke(func(r *whatsmeowv1.SendRevokeRequest) { r.Chat = "111:2@lid" }), "chat"},
		{edit(func(r *whatsmeowv1.SendEditRequest) { r.TargetId = "" }), "target_id"},
		{edit(func(r *whatsmeowv1.SendEditRequest) { r.Text = "" }), "text"},
	}

	for n, c := range cases {
		wantReason(t, c.err, codes.InvalidArgument, "INVALID_ARGUMENT", map[string]string{"field": c.field})

		// The texts name the field, never a JID.
		if strings.Contains(status.Convert(c.err).Message(), "@") && c.field != "chat" {
			t.Errorf("case %d: %q carries a JID", n, status.Convert(c.err).Message())
		}
	}

	if exists, _, _, _ := h.record(1); exists || len(h.stub.sent()) != 0 {
		t.Fatal("a malformed action left a record or reached whatsmeow")
	}
}

func TestActionsShareTheQueueAndTheRecordsOfTheOtherSends(t *testing.T) {
	t.Parallel()

	h := start(t)
	release := h.hold()

	first := h.async(h.ctx, 1)
	h.next()

	reacted := make(chan error, 1)

	go func() {
		_, err := h.client.SendReaction(h.ctx, &whatsmeowv1.SendReactionRequest{SessionId: "s1", Chat: "111@lid", ClientMessageId: id(2), TargetId: "ABC", Emoji: "👍"})
		reacted <- err
	}()

	h.wait(h.queued, "the reaction queued")
	close(release)

	if r := h.outcome(first); r.err != nil {
		t.Fatalf("the text failed: %v", r.err)
	}

	if err := <-reacted; err != nil {
		t.Fatalf("the reaction failed: %v", err)
	}

	// A repeat of an id is answered from its record, whatever kind either send is.
	again, err := h.client.SendRevoke(h.ctx, &whatsmeowv1.SendRevokeRequest{SessionId: "s1", Chat: "111@lid", ClientMessageId: id(2), TargetId: "ABC"})
	if err != nil || again.GetChat() != "111@lid" || !again.GetMessageTime().AsTime().Equal(at) {
		t.Fatalf("the repeat = %v, %v; want the reaction's result", again, err)
	}

	if sent := h.stub.sent(); len(sent) != 2 || sent[0].id != id(1) || sent[1].message.GetReactionMessage() == nil || sent[1].id != id(2) {
		t.Fatalf("whatsmeow got %v, want the text, then the reaction, once", h.stub.ids())
	}
}

func TestAReplyAndMentionsGoInTheMessagesContextInfo(t *testing.T) {
	t.Parallel()

	reply := &whatsmeowv1.MessageReply{Id: "ABC", Sender: member, QuotedText: "the question"}
	mentions := []string{member, "900000002@lid"}
	context := &waE2E.ContextInfo{
		StanzaID: proto.String("ABC"), Participant: proto.String("5511900000002@s.whatsapp.net"),
		QuotedMessage: &waE2E.Message{Conversation: proto.String("the question")}, QuotedType: waE2E.ContextInfo_EXPLICIT.Enum(),
		MentionedJID: []string{"5511900000002@s.whatsapp.net", "900000002@lid"},
	}

	h := start(t)

	if _, err := h.client.SendText(h.ctx, &whatsmeowv1.SendTextRequest{SessionId: "s1", Chat: group, ClientMessageId: id(1), Text: "@5511900000002 yes", ReplyTo: reply, Mentions: mentions}); err != nil {
		t.Fatalf("SendText: %v", err)
	}

	want := &waE2E.Message{ExtendedTextMessage: &waE2E.ExtendedTextMessage{Text: proto.String("@5511900000002 yes"), ContextInfo: context}}
	if sent := h.only(t); !proto.Equal(sent.message, want) {
		t.Fatalf("whatsmeow got %v, want %v", sent.message, want)
	}

	// On media, the context goes on the media message; a captioned document's, inside its wrapper.
	for n, metadata := range []func(*whatsmeowv1.SendMediaRequest){
		image(&whatsmeowv1.ImageMetadata{Caption: "look"}),
		document(&whatsmeowv1.DocumentMetadata{Caption: "read"}),
	} {
		h := start(t)
		req := &whatsmeowv1.SendMediaRequest{SessionId: "s1", Chat: group, ClientMessageId: id(1), ReplyTo: reply, Mentions: mentions}

		if n == 0 {
			req.Media = ref(whatsmeowv1.MediaType_MEDIA_TYPE_IMAGE, nil)
		} else {
			req.Media = ref(whatsmeowv1.MediaType_MEDIA_TYPE_DOCUMENT, nil)
		}

		metadata(req)

		if _, err := h.client.SendMedia(h.ctx, req); err != nil {
			t.Fatalf("SendMedia: %v", err)
		}

		message := h.only(t).message
		got := message.GetImageMessage().GetContextInfo()

		if n == 1 {
			got = message.GetDocumentWithCaptionMessage().GetMessage().GetDocumentMessage().GetContextInfo()
		}

		if !proto.Equal(got, context) {
			t.Fatalf("media %d carries %v, want %v", n, got, context)
		}
	}
}

func TestAReplyToTheAccountsOwnMessageNamesTheAccount(t *testing.T) {
	t.Parallel()

	h := start(t)

	if _, err := h.client.SendText(h.ctx, &whatsmeowv1.SendTextRequest{SessionId: "s1", Chat: "111@lid", ClientMessageId: id(1), Text: "more", ReplyTo: &whatsmeowv1.MessageReply{Id: "ABC"}}); err != nil {
		t.Fatalf("SendText: %v", err)
	}

	want := &waE2E.Message{ExtendedTextMessage: &waE2E.ExtendedTextMessage{Text: proto.String("more"), ContextInfo: &waE2E.ContextInfo{
		StanzaID: proto.String("ABC"), Participant: proto.String("5511900000001@s.whatsapp.net"),
		QuotedMessage: &waE2E.Message{Conversation: proto.String("")}, QuotedType: waE2E.ContextInfo_EXPLICIT.Enum(),
	}}}
	if sent := h.only(t); !proto.Equal(sent.message, want) {
		t.Fatalf("whatsmeow got %v, want %v", sent.message, want)
	}
}

func TestAMalformedReplyOrMentionIsInvalidAndSendsNothing(t *testing.T) {
	t.Parallel()

	h := start(t)
	send := func(reply *whatsmeowv1.MessageReply, mentions ...string) error {
		_, err := h.client.SendText(h.ctx, &whatsmeowv1.SendTextRequest{SessionId: "s1", Chat: group, ClientMessageId: id(1), Text: "hi", ReplyTo: reply, Mentions: mentions})

		return err //nolint:wrapcheck // the test reads the status
	}
	media := func(reply *whatsmeowv1.MessageReply, mentions ...string) error {
		_, err := h.client.SendMedia(h.ctx, &whatsmeowv1.SendMediaRequest{
			SessionId: "s1", Chat: group, ClientMessageId: id(1), Media: ref(whatsmeowv1.MediaType_MEDIA_TYPE_IMAGE, nil),
			Metadata: &whatsmeowv1.SendMediaRequest_Image{Image: &whatsmeowv1.ImageMetadata{}}, ReplyTo: reply, Mentions: mentions,
		})

		return err //nolint:wrapcheck // the test reads the status
	}

	cases := []struct {
		err   error
		field string
	}{
		{send(&whatsmeowv1.MessageReply{}), "reply_to.id"},
		{send(&whatsmeowv1.MessageReply{Id: "ABC", Sender: group}), "reply_to.sender"},
		{send(nil, member, group), "mentions"},
		{send(nil, ""), "mentions"},
		{media(&whatsmeowv1.MessageReply{}), "reply_to.id"},
		{media(nil, "not a jid@"), "mentions"},
	}

	for n, c := range cases {
		wantReason(t, c.err, codes.InvalidArgument, "INVALID_ARGUMENT", map[string]string{"field": c.field})

		if strings.Contains(status.Convert(c.err).Message(), "@") {
			t.Errorf("case %d: %q carries a JID", n, status.Convert(c.err).Message())
		}
	}

	if exists, _, _, _ := h.record(1); exists || len(h.stub.sent()) != 0 {
		t.Fatal("a malformed reply left a record or reached whatsmeow")
	}
}
