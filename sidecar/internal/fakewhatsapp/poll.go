//go:build fakewhatsapp

package fakewhatsapp

import (
	"context"
	"fmt"

	"go.mau.fi/whatsmeow"
	"go.mau.fi/whatsmeow/proto/waCommon"
	"go.mau.fi/whatsmeow/proto/waE2E"
	"go.mau.fi/whatsmeow/types"
	"go.mau.fi/whatsmeow/types/events"
	"google.golang.org/protobuf/proto"

	fakev1 "github.com/tcortega/Whatsmeow.NET/sidecar/gen/whatsmeow/fake/v1"
)

// secrets is a never-connected whatsmeow client of the session's device, for whatsmeow's own message secret code:
// it reads and writes nothing but the device's store (msgsecret.go:158-445, message.go:964-985), and NewClient starts
// nothing (client.go:267-325).
func (c *Client) secrets() *whatsmeow.Client { return whatsmeow.NewClient(c.Store, nil) }

// BuildPollCreation is whatsmeow's own (msgsecret.go:402-421).
func (c *Client) BuildPollCreation(name string, optionNames []string, selectableOptionCount int) *waE2E.Message {
	return c.secrets().BuildPollCreation(name, optionNames, selectableOptionCount)
}

// BuildPollVote is whatsmeow's own (msgsecret.go:391-445), with the poll's secret from the device's store.
func (c *Client) BuildPollVote(ctx context.Context, pollInfo *types.MessageInfo, optionNames []string) (*waE2E.Message, error) {
	return c.secrets().BuildPollVote(ctx, pollInfo, optionNames) //nolint:wrapcheck // whatsmeow's own
}

// storeSecret stores an outgoing message's secret as whatsmeow's SendMessage does (send.go:403-410): by the chat it
// went to and the account as it named itself there (ownID).
func (c *Client) storeSecret(ctx context.Context, to types.JID, id types.MessageID, message *waE2E.Message) {
	secret := message.GetMessageContextInfo().GetMessageSecret()
	if secret == nil {
		return
	}

	if err := c.Store.MsgSecrets.PutMessageSecret(ctx, to, c.ownID(to), id, secret); err != nil {
		c.whatsApp.log.Warn().Err(err).Str("session_id", c.sessionID).Msg("fake WhatsApp: storing a sent message's secret failed")
	}
}

// ownID is how whatsmeow's SendMessage names the account in to (send.go:304-365): its LID in a LID chat (a phone
// number's chat was redirected to its LID already) and in a LID-addressed group; its phone number otherwise.
func (c *Client) ownID(to types.JID) types.JID {
	lid := c.Store.GetLID()

	switch to.Server {
	case types.HiddenUserServer, types.BotServer:
		return lid
	case types.GroupServer:
		c.whatsApp.mu.Lock()
		g, ok := c.whatsApp.groups[to]
		byLID := ok && g.lid
		c.whatsApp.mu.Unlock()

		if byLID {
			return lid
		}
	}

	return c.Store.GetJID()
}

// DeliverPollVote delivers a poll vote encrypted as the voter's device encrypts it (fake.proto).
func (c *Control) DeliverPollVote(ctx context.Context, req *fakev1.DeliverPollVoteRequest) (*fakev1.DeliverPollVoteResponse, error) {
	source, err := messageSource(req.GetChat(), req.GetSender(), "", "")
	if err != nil {
		return nil, fmt.Errorf("deliver poll vote %q: %w", req.GetId(), err)
	}

	pollSender, err := types.ParseJID(req.GetPollSender())
	if err != nil {
		return nil, fmt.Errorf("deliver poll vote %q: poll sender: %w", req.GetId(), err)
	}

	client, err := c.whatsApp.client(req.GetSessionId())
	if err != nil {
		return nil, fmt.Errorf("deliver poll vote %q: %w", req.GetId(), err)
	}

	plaintext, err := proto.Marshal(&waE2E.PollVoteMessage{SelectedOptions: whatsmeow.HashPollOptions(req.GetOptions())})
	if err != nil {
		return nil, fmt.Errorf("deliver poll vote %q: %w", req.GetId(), err)
	}

	payload, iv, err := client.secrets().DangerousInternals().EncryptMsgSecret(ctx, source.Sender, source.Chat, pollSender, req.GetPollId(), whatsmeow.EncSecretPollVote, plaintext) //nolint:staticcheck // the voter's encryption, which no exported call makes
	if err != nil {
		return nil, fmt.Errorf("deliver poll vote %q: %w", req.GetId(), err)
	}

	source.IsFromMe = req.GetIsFromMe()
	source.IsGroup = source.Chat.Server == types.GroupServer
	evt := &events.Message{
		Info: types.MessageInfo{MessageSource: source, ID: req.GetId(), Timestamp: timeOf(req.GetMessageTime())},
		Message: &waE2E.Message{PollUpdateMessage: &waE2E.PollUpdateMessage{
			PollCreationMessageKey: client.voterKey(source, pollSender, req.GetPollId()),
			Vote:                   &waE2E.PollEncValue{EncPayload: payload, EncIV: iv},
		}},
	}

	if err := c.whatsApp.deliverMessage(ctx, req.GetSessionId(), evt); err != nil {
		return nil, err
	}

	return &fakev1.DeliverPollVoteResponse{}, nil
}

// voterKey is the poll's key as the voter names it, which whatsmeow reads back (msgsecret.go:130-151): the voter's own
// poll; else in a group the poll's sender as participant, and in a direct chat the other side, this account, as the
// remote JID.
func (c *Client) voterKey(voter types.MessageSource, pollSender types.JID, id string) *waCommon.MessageKey {
	key := &waCommon.MessageKey{ID: proto.String(id), FromMe: proto.Bool(voter.Sender.User == pollSender.User), RemoteJID: proto.String(voter.Chat.String())}

	switch {
	case voter.IsGroup && !key.GetFromMe():
		key.Participant = proto.String(pollSender.String())
	case !voter.IsGroup:
		key.RemoteJID = proto.String(pollSender.String())
		if key.GetFromMe() {
			key.RemoteJID = proto.String(c.ownID(voter.Chat).ToNonAD().String())
		}
	}

	return key
}
