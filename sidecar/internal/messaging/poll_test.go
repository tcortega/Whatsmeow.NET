package messaging

import (
	"bytes"
	"context"
	"testing"

	"go.mau.fi/whatsmeow"
	"go.mau.fi/whatsmeow/proto/waE2E"
	"go.mau.fi/whatsmeow/types"
	"go.mau.fi/whatsmeow/types/events"
	"google.golang.org/grpc/codes"
	"google.golang.org/protobuf/proto"

	whatsmeowv1 "github.com/tcortega/Whatsmeow.NET/sidecar/gen/whatsmeow/v1"
)

// withoutSecret is a poll without the message secret whatsmeow's BuildPollCreation makes anew for each poll
// (msgsecret.go:402-421), so two builds compare.
func withoutSecret(message *waE2E.Message) *waE2E.Message {
	m := proto.CloneOf(message)
	m.MessageContextInfo.MessageSecret = nil

	return m
}

// secret stores a poll's message secret in the session's store, as whatsmeow does for a poll it sends or receives
// (send.go:403-410, message.go:973-994).
func (h *harness) secret(chat, sender, pollID string) {
	h.t.Helper()

	err := h.stub.wa.Store.MsgSecrets.PutMessageSecret(context.Background(), jid(chat), jid(sender), pollID, bytes.Repeat([]byte{7}, 32))
	if err != nil {
		h.t.Fatalf("store the poll's secret: %v", err)
	}
}

// decrypt is the vote in message as the account's other devices decrypt it, with whatsmeow's own DecryptPollVote:
// a message from voter in chat.
func (h *harness) decrypt(message *waE2E.Message, chat, voter string) [][]byte {
	h.t.Helper()

	vote, err := h.stub.wa.DecryptPollVote(context.Background(), &events.Message{
		Info:    types.MessageInfo{MessageSource: types.MessageSource{Chat: jid(chat), Sender: jid(voter), IsFromMe: true, IsGroup: jid(chat).Server == types.GroupServer}},
		Message: message,
	})
	if err != nil {
		h.t.Fatalf("the vote does not decrypt with the poll's secret: %v", err)
	}

	return vote.GetSelectedOptions()
}

func TestSendPollSendsWhatsmeowsPollCreation(t *testing.T) {
	t.Parallel()

	h := start(t)

	resp, err := h.client.SendPoll(h.ctx, &whatsmeowv1.SendPollRequest{
		SessionId: "s1", Chat: "111@lid", ClientMessageId: id(1), Question: "Lunch?", Options: []string{"Pizza", "Sushi", "Tacos"}, MaxSelectable: 2,
	})
	if err != nil || resp.GetChat() != "111@lid" || !resp.GetMessageTime().AsTime().Equal(at) {
		t.Fatalf("SendPoll = %v, %v; want 111@lid at %v", resp, err, at)
	}

	sent := h.only(t)
	want := h.stub.wa.BuildPollCreation("Lunch?", []string{"Pizza", "Sushi", "Tacos"}, 2)

	if sent.to != jid("111@lid") || sent.id != id(1) || !proto.Equal(withoutSecret(sent.message), withoutSecret(want)) {
		t.Fatalf("whatsmeow got %v, want whatsmeow's own poll %v", sent.message, want)
	}

	if len(sent.message.GetMessageContextInfo().GetMessageSecret()) != 32 {
		t.Fatalf("the poll's secret is %x, want whatsmeow's 32 bytes", sent.message.GetMessageContextInfo().GetMessageSecret())
	}
}

// whatsmeow's SendMessage stores a poll's secret before its device lookup (send.go:403-410, 1198) and its store keeps
// the first secret of an id (store.go:882-885), so a poll sent again after a send that sent nothing carries the secret
// stored first, the one votes on it are decrypted with.
func TestAPollSentAgainCarriesTheSecretStoredForItsId(t *testing.T) {
	t.Parallel()

	cases := []struct {
		name, chat, storedChat, sender string
	}{
		{"a LID chat", "111@lid", "111@lid", "900000001@lid"},
		{"a phone number's chat, which whatsmeow sent to its LID", "5511900000009@s.whatsapp.net", "900000009@lid", "900000001@lid"},
		{"a group", group, group, "5511900000001@s.whatsapp.net"},
		{"a LID-addressed group", lidGroup, lidGroup, "900000001@lid"},
	}

	for _, c := range cases {
		t.Run(c.name, func(t *testing.T) {
			t.Parallel()

			h := start(t)
			if err := h.stub.wa.Store.LIDs.PutLIDMapping(h.ctx, jid("900000009@lid"), jid("5511900000009@s.whatsapp.net")); err != nil {
				t.Fatalf("PutLIDMapping: %v", err)
			}

			h.secret(c.storedChat, c.sender, id(1))

			if _, err := h.client.SendPoll(h.ctx, &whatsmeowv1.SendPollRequest{
				SessionId: "s1", Chat: c.chat, ClientMessageId: id(1), Question: "Lunch?", Options: []string{"Pizza", "Sushi"}, MaxSelectable: 1,
			}); err != nil {
				t.Fatalf("SendPoll: %v", err)
			}

			if got := h.only(t).message.GetMessageContextInfo().GetMessageSecret(); !bytes.Equal(got, bytes.Repeat([]byte{7}, 32)) {
				t.Fatalf("the poll's secret is %x, want the one stored for its id", got)
			}
		})
	}
}

func TestAPollCarriesItsReplyAndMentionsOnThePoll(t *testing.T) {
	t.Parallel()

	h := start(t)

	_, err := h.client.SendPoll(h.ctx, &whatsmeowv1.SendPollRequest{
		SessionId: "s1", Chat: group, ClientMessageId: id(1), Question: "Lunch?", Options: []string{"Pizza", "Sushi"}, MaxSelectable: 1,
		ReplyTo: &whatsmeowv1.MessageReply{Id: "ABC", Sender: member, QuotedText: "hungry?"}, Mentions: []string{member},
	})
	if err != nil {
		t.Fatalf("SendPoll: %v", err)
	}

	want := &waE2E.ContextInfo{
		StanzaID: proto.String("ABC"), Participant: proto.String("5511900000002@s.whatsapp.net"),
		QuotedMessage: &waE2E.Message{Conversation: proto.String("hungry?")}, QuotedType: waE2E.ContextInfo_EXPLICIT.Enum(),
		MentionedJID: []string{"5511900000002@s.whatsapp.net"},
	}

	if got := h.only(t).message.GetPollCreationMessage().GetContextInfo(); !proto.Equal(got, want) {
		t.Fatalf("the poll's context is %v, want %v", got, want)
	}
}

func TestSendPollVoteEncryptsTheVoteWithThePollsSecret(t *testing.T) {
	t.Parallel()

	cases := []struct {
		name, chat, pollSender, stored, voter string
		options                               []string
		fromMe                                bool
		participant                           string
	}{
		// whatsmeow names the account by its LID in a LID chat (send.go:340-363), and votes as that (msgsecret.go:429-432).
		{"the account's own poll", "111@lid", "", "900000001@lid", "900000001@lid", []string{"Sushi"}, true, ""},
		{"the account's own poll by its JID", "111@lid", self, "900000001@lid", "900000001@lid", []string{"Sushi"}, true, ""},
		{"a member's group poll", group, member, "5511900000002@s.whatsapp.net", "5511900000001@s.whatsapp.net", []string{"Pizza", "Sushi"}, false, "5511900000002:7@s.whatsapp.net"},
		{"the account's own poll in a LID-addressed group", lidGroup, "", "900000001@lid", "900000001@lid", []string{"Pizza"}, true, "900000001@lid"},
		{"a cleared vote", "111@lid", "", "900000001@lid", "900000001@lid", nil, true, ""},
	}

	for _, c := range cases {
		t.Run(c.name, func(t *testing.T) {
			t.Parallel()

			h := start(t)
			h.secret(c.chat, c.stored, "POLL1")

			resp, err := h.client.SendPollVote(h.ctx, &whatsmeowv1.SendPollVoteRequest{
				SessionId: "s1", Chat: c.chat, ClientMessageId: id(1), PollId: "POLL1", PollSender: c.pollSender, Options: c.options,
			})
			if err != nil || resp.GetChat() != c.chat || !resp.GetMessageTime().AsTime().Equal(at) {
				t.Fatalf("SendPollVote = %v, %v; want %s at %v", resp, err, c.chat, at)
			}

			sent := h.only(t)
			key := sent.message.GetPollUpdateMessage().GetPollCreationMessageKey()

			if sent.id != id(1) || key.GetID() != "POLL1" || key.GetFromMe() != c.fromMe || key.GetRemoteJID() != c.chat || key.GetParticipant() != c.participant {
				t.Fatalf("whatsmeow got %v, want a vote naming POLL1 (from me %v, participant %q)", sent.message, c.fromMe, c.participant)
			}

			if got, want := h.decrypt(sent.message, c.chat, c.voter), whatsmeow.HashPollOptions(c.options); !equalHashes(got, want) {
				t.Fatalf("the vote selects %x, want %x", got, want)
			}
		})
	}
}

func equalHashes(a, b [][]byte) bool {
	if len(a) != len(b) {
		return false
	}

	for i := range a {
		if !bytes.Equal(a[i], b[i]) {
			return false
		}
	}

	return true
}

func TestAVoteOnAPollWithoutItsSecretFailsAndSendsNothing(t *testing.T) {
	t.Parallel()

	h := start(t)
	vote := &whatsmeowv1.SendPollVoteRequest{SessionId: "s1", Chat: "111@lid", ClientMessageId: id(1), PollId: "POLL1", Options: []string{"Sushi"}}

	_, err := h.client.SendPollVote(h.ctx, vote)
	wantReason(t, err, codes.FailedPrecondition, "POLL_SECRET_NOT_FOUND", nil)

	if exists, _, _, _ := h.record(1); exists || len(h.stub.sent()) != 0 {
		t.Fatal("a vote without the poll's secret left a record or reached whatsmeow")
	}

	// Nothing was sent, so the id is free: once the secret is here, the same vote goes.
	h.secret("111@lid", "900000001@lid", "POLL1")

	if _, err := h.client.SendPollVote(h.ctx, vote); err != nil || len(h.stub.sent()) != 1 {
		t.Fatalf("the repeat = %v with %v sent; want the vote sent once", err, h.stub.ids())
	}
}

// whatsmeow votes as the account's LID on a LID's poll and by its phone number otherwise, and fails with
// ErrNotLoggedIn when the device has no such JID (msgsecret.go:207-208, 429-432): with neither, the device was deleted,
// as a logout deletes it (store.go:286-298), which a send reads as LOGGED_OUT too (classify); with only the LID
// missing no reason says why, so it stays INTERNAL (ADR 0041). Either way nothing is sent.
func TestAVoteWhatsmeowHasNoJIDToSendAsSendsNothing(t *testing.T) {
	t.Parallel()

	cases := []struct {
		name, pollSender string
		deleted          bool
		code             codes.Code
		reason           string
	}{
		{"the device deleted, as at a logout", member, true, codes.FailedPrecondition, "LOGGED_OUT"},
		{"a LID's poll on a session without a LID", "900000002@lid", false, codes.Internal, "INTERNAL"},
	}

	for _, c := range cases {
		t.Run(c.name, func(t *testing.T) {
			t.Parallel()

			h := start(t)
			h.secret(group, c.pollSender, "POLL1")

			h.stub.wa.Store.LID = types.EmptyJID
			if c.deleted {
				h.stub.wa.Store.ID = nil
			}

			_, err := h.client.SendPollVote(h.ctx, &whatsmeowv1.SendPollVoteRequest{
				SessionId: "s1", Chat: group, ClientMessageId: id(1), PollId: "POLL1", PollSender: c.pollSender, Options: []string{"Pizza"},
			})
			wantReason(t, err, c.code, c.reason, nil)

			if exists, _, _, _ := h.record(1); exists || len(h.stub.sent()) != 0 {
				t.Fatal("the vote left a record or reached whatsmeow")
			}
		})
	}
}

func TestARepeatOfARecordedOwnGroupPollVoteAsksForNoGroupInfo(t *testing.T) {
	t.Parallel()

	h := start(t)
	h.secret(group, "5511900000001@s.whatsapp.net", "POLL1")
	vote := &whatsmeowv1.SendPollVoteRequest{SessionId: "s1", Chat: group, ClientMessageId: id(1), PollId: "POLL1", Options: []string{"Sushi"}}

	for range 2 {
		if _, err := h.client.SendPollVote(h.ctx, vote); err != nil {
			t.Fatalf("SendPollVote: %v", err)
		}
	}

	if calls := h.stub.groupInfoCalls(); calls != 1 || len(h.stub.sent()) != 1 {
		t.Fatalf("%d group info requests and %v sent, want one of each", calls, h.stub.ids())
	}
}

func TestAMalformedPollOrVoteIsInvalidAndSendsNothing(t *testing.T) {
	t.Parallel()

	h := start(t)
	poll := func(change func(*whatsmeowv1.SendPollRequest)) error {
		req := &whatsmeowv1.SendPollRequest{SessionId: "s1", Chat: group, ClientMessageId: id(1), Question: "Lunch?", Options: []string{"Pizza", "Sushi"}, MaxSelectable: 1}
		change(req)
		_, err := h.client.SendPoll(h.ctx, req)

		return err //nolint:wrapcheck // the test reads the status
	}
	vote := func(change func(*whatsmeowv1.SendPollVoteRequest)) error {
		req := &whatsmeowv1.SendPollVoteRequest{SessionId: "s1", Chat: group, ClientMessageId: id(1), PollId: "POLL1", PollSender: member, Options: []string{"Pizza"}}
		change(req)
		_, err := h.client.SendPollVote(h.ctx, req)

		return err //nolint:wrapcheck // the test reads the status
	}

	cases := []struct {
		err   error
		field string
	}{
		{poll(func(r *whatsmeowv1.SendPollRequest) { r.Question = "" }), "question"},
		{poll(func(r *whatsmeowv1.SendPollRequest) { r.Options = []string{"Pizza"} }), "options"},
		{poll(func(r *whatsmeowv1.SendPollRequest) { r.Options = nil }), "options"},
		{poll(func(r *whatsmeowv1.SendPollRequest) { r.Options = []string{"Pizza", "Pizza"} }), "options"},
		{poll(func(r *whatsmeowv1.SendPollRequest) { r.Options = []string{"Pizza", ""} }), "options"},
		{poll(func(r *whatsmeowv1.SendPollRequest) { r.MaxSelectable = 0 }), "max_selectable"},
		{poll(func(r *whatsmeowv1.SendPollRequest) { r.MaxSelectable = 3 }), "max_selectable"},
		{poll(func(r *whatsmeowv1.SendPollRequest) { r.Mentions = []string{group} }), "mentions"},
		{poll(func(r *whatsmeowv1.SendPollRequest) { r.ReplyTo = &whatsmeowv1.MessageReply{} }), "reply_to.id"},
		{poll(func(r *whatsmeowv1.SendPollRequest) { r.ClientMessageId = "3EB0-0001" }), "client_message_id"},
		{vote(func(r *whatsmeowv1.SendPollVoteRequest) { r.PollId = "" }), "poll_id"},
		{vote(func(r *whatsmeowv1.SendPollVoteRequest) { r.PollSender = group }), "poll_sender"},
		{vote(func(r *whatsmeowv1.SendPollVoteRequest) { r.Options = []string{"Pizza", "Pizza"} }), "options"},
		{vote(func(r *whatsmeowv1.SendPollVoteRequest) { r.Options = []string{""} }), "options"},
		{vote(func(r *whatsmeowv1.SendPollVoteRequest) { r.Chat = "1@newsletter" }), "chat"},
	}

	for _, c := range cases {
		wantReason(t, c.err, codes.InvalidArgument, "INVALID_ARGUMENT", map[string]string{"field": c.field})
	}

	if exists, _, _, _ := h.record(1); exists || len(h.stub.sent()) != 0 {
		t.Fatal("a malformed poll or vote left a record or reached whatsmeow")
	}
}

func TestPollsAndVotesShareTheQueueAndTheRecordsOfTheOtherSends(t *testing.T) {
	t.Parallel()

	h := start(t)
	h.secret("111@lid", "900000001@lid", "POLL1")
	release := h.hold()

	first := h.async(h.ctx, 1)
	h.next()

	polled, voted := make(chan error, 1), make(chan error, 1)

	go func() {
		_, err := h.client.SendPoll(h.ctx, &whatsmeowv1.SendPollRequest{SessionId: "s1", Chat: "111@lid", ClientMessageId: id(2), Question: "Lunch?", Options: []string{"Pizza", "Sushi"}, MaxSelectable: 1})
		polled <- err
	}()

	h.wait(h.queued, "the poll queued")

	go func() {
		_, err := h.client.SendPollVote(h.ctx, &whatsmeowv1.SendPollVoteRequest{SessionId: "s1", Chat: "111@lid", ClientMessageId: id(3), PollId: "POLL1", Options: []string{"Sushi"}})
		voted <- err
	}()

	h.wait(h.queued, "the vote queued")
	close(release)

	if r := h.outcome(first); r.err != nil {
		t.Fatalf("the text failed: %v", r.err)
	}

	if err, err2 := <-polled, <-voted; err != nil || err2 != nil {
		t.Fatalf("the poll = %v, the vote = %v; want both sent", err, err2)
	}

	// A repeat of an id is answered from its record, whatever kind either send is.
	for _, n := range []int{2, 3} {
		again, err := h.client.SendPoll(h.ctx, &whatsmeowv1.SendPollRequest{SessionId: "s1", Chat: "111@lid", ClientMessageId: id(n), Question: "Other?", Options: []string{"A", "B"}, MaxSelectable: 1})
		if err != nil || again.GetChat() != "111@lid" || !again.GetMessageTime().AsTime().Equal(at) {
			t.Fatalf("the repeat of %d = %v, %v; want its recorded result", n, again, err)
		}
	}

	sent := h.stub.sent()
	if len(sent) != 3 || sent[0].id != id(1) || sent[1].message.GetPollCreationMessage() == nil || sent[2].message.GetPollUpdateMessage() == nil {
		t.Fatalf("whatsmeow got %v, want the text, the poll, then the vote, once each", h.stub.ids())
	}
}
