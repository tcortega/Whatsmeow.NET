//go:build fakewhatsapp

package fakewhatsapp

import (
	"bytes"
	"context"
	"errors"
	"testing"

	"go.mau.fi/whatsmeow"
	"go.mau.fi/whatsmeow/proto/waAdv"
	"go.mau.fi/whatsmeow/proto/waE2E"
	"go.mau.fi/whatsmeow/types"
	"go.mau.fi/whatsmeow/types/events"
	"google.golang.org/protobuf/proto"

	fakev1 "github.com/tcortega/Whatsmeow.NET/sidecar/gen/whatsmeow/fake/v1"
)

// pollBuilder is the part of the Messaging slice's interface that builds polls and votes.
type pollBuilder interface {
	BuildPollCreation(name string, optionNames []string, selectableOptionCount int) *waE2E.Message
	BuildPollVote(ctx context.Context, pollInfo *types.MessageInfo, optionNames []string) (*waE2E.Message, error)
}

var (
	_ pollBuilder = (*whatsmeow.Client)(nil)
	_ pollBuilder = (*Client)(nil)
)

const ownLID = "900000009:1@lid"

// secretOf is the message secret the session's store has for the message id sent by sender in chat, or nil.
func secretOf(t *testing.T, client *Client, chat, sender, id string) []byte {
	t.Helper()

	secret, _, err := client.Store.MsgSecrets.GetMessageSecret(context.Background(), parse(t, chat), parse(t, sender), id)
	if err != nil {
		t.Fatalf("GetMessageSecret: %v", err)
	}

	return secret
}

func TestThePollBuildersAreWhatsmeows(t *testing.T) {
	t.Parallel()

	_, _, ctx, client, _, _ := linked(t)
	client.Store.LID = parse(t, ownLID)
	wm := whatsmeow.NewClient(client.Store, nil)

	poll, want := client.BuildPollCreation("Lunch?", []string{"Pizza", "Sushi"}, 1), wm.BuildPollCreation("Lunch?", []string{"Pizza", "Sushi"}, 1)
	if len(poll.GetMessageContextInfo().GetMessageSecret()) != 32 || bytes.Equal(poll.GetMessageContextInfo().GetMessageSecret(), want.GetMessageContextInfo().GetMessageSecret()) {
		t.Fatalf("the fake's poll secret is %x, want a new 32 bytes", poll.GetMessageContextInfo().GetMessageSecret())
	}

	poll.MessageContextInfo, want.MessageContextInfo = nil, nil
	if !proto.Equal(poll, want) {
		t.Fatalf("the fake built %v, whatsmeow %v", poll, want)
	}

	if err := client.Store.MsgSecrets.PutMessageSecret(ctx, parse(t, "222@lid"), parse(t, ownLID), "POLL1", bytes.Repeat([]byte{7}, 32)); err != nil {
		t.Fatalf("PutMessageSecret: %v", err)
	}

	info := &types.MessageInfo{MessageSource: types.MessageSource{Chat: parse(t, "222@lid"), Sender: parse(t, "900000009@lid"), IsFromMe: true}, ID: "POLL1"}

	vote, err := client.BuildPollVote(ctx, info, []string{"Sushi"})
	if err != nil {
		t.Fatalf("BuildPollVote: %v", err)
	}

	decrypted, err := wm.DecryptPollVote(ctx, &events.Message{Info: types.MessageInfo{MessageSource: info.MessageSource}, Message: vote})
	if err != nil || !bytes.Equal(decrypted.GetSelectedOptions()[0], whatsmeow.HashPollOptions([]string{"Sushi"})[0]) {
		t.Fatalf("the fake's vote decrypts to %v, %v; want Sushi's hash", decrypted, err)
	}

	if _, err := client.BuildPollVote(ctx, &types.MessageInfo{MessageSource: info.MessageSource, ID: "POLL2"}, nil); err == nil {
		t.Fatal("a vote on a poll without its secret was built")
	}
}

// As whatsmeow's SendMessage stores an outgoing message's secret, by the chat it went to and the account as it named
// itself there (send.go:304-410): the LID in a LID chat and in a phone number's, which it sends to the LID.
func TestASentMessagesSecretIsStoredAsWhatsmeowStoresIt(t *testing.T) {
	t.Parallel()

	_, control, ctx, client, _, db := linked(t)
	client.Store.LID = parse(t, ownLID)

	if err := client.ConnectContext(ctx); err != nil {
		t.Fatalf("ConnectContext: %v", err)
	}

	if _, err := control.RegisterUser(ctx, &fakev1.RegisterUserRequest{PhoneJid: "5511900000001@s.whatsapp.net", Lid: "900000001@lid"}); err != nil {
		t.Fatalf("RegisterUser: %v", err)
	}

	for n, to := range []string{"900000002@lid", "5511900000001@s.whatsapp.net"} {
		id := []string{"3EB0000000000000000001", "3EB0000000000000000002"}[n]

		resp, err := client.SendMessage(ctx, parse(t, to), client.BuildPollCreation("Lunch?", []string{"Pizza", "Sushi"}, 1), whatsmeow.SendRequestExtra{ID: id})
		if err != nil {
			t.Fatalf("SendMessage: %v", err)
		}

		var chat, sender string
		if err := db.DB.QueryRowContext(ctx, "SELECT chat_jid, sender_jid FROM whatsmeow_message_secrets WHERE message_id = ?", id).Scan(&chat, &sender); err != nil {
			t.Fatalf("the secret of the poll to %s: %v", to, err)
		}

		if chat != resp.Chat.String() || sender != "900000009@lid" {
			t.Errorf("the poll to %s has its secret under %s from %s, want %s from the account's LID", to, chat, sender, resp.Chat)
		}
	}
}

// As whatsmeow's SendMessage stores the secret before sendDM and sendGroup marshal the message and look its devices up
// (send.go:403-410, 875, 782, 1198): a send rate limited there has its secret stored already.
func TestARateLimitedSendHasItsSecretStoredFirst(t *testing.T) {
	t.Parallel()

	_, control, ctx, client, _, _ := linked(t)
	client.Store.LID = parse(t, ownLID)

	if err := client.ConnectContext(ctx); err != nil {
		t.Fatalf("ConnectContext: %v", err)
	}

	if _, err := control.RateLimitNextSends(ctx, &fakev1.RateLimitNextSendsRequest{SessionId: "s1", Count: 1}); err != nil {
		t.Fatalf("RateLimitNextSends: %v", err)
	}

	poll := client.BuildPollCreation("Lunch?", []string{"Pizza", "Sushi"}, 1)

	var iq *whatsmeow.IQError
	if _, err := client.SendMessage(ctx, parse(t, "900000002@lid"), poll, whatsmeow.SendRequestExtra{ID: "3EB0000000000000000001"}); !errors.As(err, &iq) || iq.Code != 429 {
		t.Fatalf("SendMessage = %v, want an IQ 429", err)
	}

	if got := secretOf(t, client, "900000002@lid", "900000009@lid", "3EB0000000000000000001"); !bytes.Equal(got, poll.GetMessageContextInfo().GetMessageSecret()) {
		t.Fatalf("stored secret %x, want the rate-limited poll's %x", got, poll.GetMessageContextInfo().GetMessageSecret())
	}
}

// As whatsmeow stores a received message's secret before it dispatches the message (message.go:948-949, 973-994).
func TestADeliveredMessagesSecretIsStored(t *testing.T) {
	t.Parallel()

	_, control, ctx, client, _, _ := linked(t)
	poll := whatsmeow.NewClient(client.Store, nil).BuildPollCreation("Lunch?", []string{"Pizza", "Sushi"}, 1)

	if _, err := control.DeliverMessage(ctx, &fakev1.DeliverMessageRequest{SessionId: "s1", Id: "POLL1", Chat: "222@lid", Sender: "222:2@lid", Message: poll}); err != nil {
		t.Fatalf("DeliverMessage: %v", err)
	}

	if got := secretOf(t, client, "222@lid", "222@lid", "POLL1"); !bytes.Equal(got, poll.GetMessageContextInfo().GetMessageSecret()) {
		t.Fatalf("stored secret %x, want the poll's %x", got, poll.GetMessageContextInfo().GetMessageSecret())
	}
}

func TestDeliverPollVoteDeliversAVoteThatDecryptsWithThePollsSecret(t *testing.T) {
	t.Parallel()

	cases := []struct {
		name, chat, pollSender, voter string
	}{
		{"a vote on the account's poll in a direct chat", "222@lid", "900000009@lid", "222:2@lid"},
		{"a member's vote on another member's group poll", "120363000000000000@g.us", "333@lid", "444:1@lid"},
		{"a member's vote on their own group poll", "120363000000000000@g.us", "444@lid", "444:1@lid"},
	}

	for _, c := range cases {
		t.Run(c.name, func(t *testing.T) {
			t.Parallel()

			_, control, ctx, client, got, _ := linked(t)
			client.Store.LID = parse(t, ownLID)
			<-got // PairSuccess

			if err := client.Store.MsgSecrets.PutMessageSecret(ctx, parse(t, c.chat), parse(t, c.pollSender), "POLL1", bytes.Repeat([]byte{7}, 32)); err != nil {
				t.Fatalf("PutMessageSecret: %v", err)
			}

			if _, err := control.DeliverPollVote(ctx, &fakev1.DeliverPollVoteRequest{
				SessionId: "s1", Id: "V1", Chat: c.chat, Sender: c.voter, PollId: "POLL1", PollSender: c.pollSender, Options: []string{"Sushi"},
			}); err != nil {
				t.Fatalf("DeliverPollVote: %v", err)
			}

			evt, ok := (<-got).(*events.Message)
			if !ok || evt.Info.ID != "V1" || evt.Info.Sender != parse(t, c.voter) || evt.Message.GetPollUpdateMessage().GetPollCreationMessageKey().GetID() != "POLL1" {
				t.Fatalf("dispatched %v, want the vote V1 from %s", evt, c.voter)
			}

			vote, err := whatsmeow.NewClient(client.Store, nil).DecryptPollVote(ctx, evt)
			if err != nil || len(vote.GetSelectedOptions()) != 1 || !bytes.Equal(vote.GetSelectedOptions()[0], whatsmeow.HashPollOptions([]string{"Sushi"})[0]) {
				t.Fatalf("the vote decrypts to %v, %v; want Sushi's hash", vote, err)
			}
		})
	}
}

// The voter's device has the account's poll only as WhatsApp delivered it, so its vote is encrypted with the secret the
// poll carried, whatever this device stored: whatsmeow keeps the first secret stored for an id (store.go:882-885).
func TestDeliverPollVoteOnTheAccountsPollUsesTheSecretThePollCarried(t *testing.T) {
	t.Parallel()

	_, control, ctx, client, got, db := linked(t)
	client.Store.LID = parse(t, ownLID)

	if err := client.ConnectContext(ctx); err != nil {
		t.Fatalf("ConnectContext: %v", err)
	}

	const id = "3EB0000000000000000001"

	if err := client.Store.MsgSecrets.PutMessageSecret(ctx, parse(t, "222@lid"), parse(t, "900000009@lid"), id, bytes.Repeat([]byte{7}, 32)); err != nil {
		t.Fatalf("PutMessageSecret: %v", err)
	}

	poll := client.BuildPollCreation("Lunch?", []string{"Pizza", "Sushi"}, 1)
	if _, err := client.SendMessage(ctx, parse(t, "222@lid"), poll, whatsmeow.SendRequestExtra{ID: id}); err != nil {
		t.Fatalf("SendMessage: %v", err)
	}

	if _, err := control.DeliverPollVote(ctx, &fakev1.DeliverPollVoteRequest{
		SessionId: "s1", Id: "V1", Chat: "222@lid", Sender: "222:2@lid", PollId: id, PollSender: "900000009@lid", Options: []string{"Sushi"},
	}); err != nil {
		t.Fatalf("DeliverPollVote: %v", err)
	}

	evt := next[*events.Message](t, got)

	// A device that has the poll as it was sent decrypts the vote with whatsmeow's own code.
	voter := db.Devices.NewDevice()
	voter.ID = new(parse(t, "5511900000222:2@s.whatsapp.net"))
	voter.Account = &waAdv.ADVSignedDeviceIdentity{
		Details: []byte{}, AccountSignature: make([]byte, 64), AccountSignatureKey: make([]byte, 32), DeviceSignature: make([]byte, 64),
	}

	if err := voter.Save(ctx); err != nil {
		t.Fatalf("save the voter's device: %v", err)
	}

	if err := voter.MsgSecrets.PutMessageSecret(ctx, parse(t, "222@lid"), parse(t, "900000009@lid"), id, poll.GetMessageContextInfo().GetMessageSecret()); err != nil {
		t.Fatalf("PutMessageSecret: %v", err)
	}

	vote, err := whatsmeow.NewClient(voter, nil).DecryptPollVote(ctx, evt)
	if err != nil || len(vote.GetSelectedOptions()) != 1 || !bytes.Equal(vote.GetSelectedOptions()[0], whatsmeow.HashPollOptions([]string{"Sushi"})[0]) {
		t.Fatalf("with the secret the poll carried, the vote decrypts to %v, %v; want Sushi's hash", vote, err)
	}
}

func TestDeliverPollVoteRefusesAPollWithoutItsSecret(t *testing.T) {
	t.Parallel()

	_, control, ctx, _, _, _ := linked(t)

	if _, err := control.DeliverPollVote(ctx, &fakev1.DeliverPollVoteRequest{SessionId: "s1", Id: "V1", Chat: "222@lid", Sender: "222:2@lid", PollId: "POLL1", PollSender: "222@lid"}); err == nil {
		t.Fatal("a vote on a poll without its secret was delivered")
	}
}
