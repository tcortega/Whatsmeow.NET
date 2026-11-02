package messaging

import (
	"context"
	"errors"
	"fmt"

	"go.mau.fi/whatsmeow"
	"go.mau.fi/whatsmeow/proto/waE2E"
	"go.mau.fi/whatsmeow/types"
	"google.golang.org/protobuf/types/known/timestamppb"

	whatsmeowv1 "github.com/tcortega/Whatsmeow.NET/sidecar/gen/whatsmeow/v1"
	"github.com/tcortega/Whatsmeow.NET/sidecar/internal/server"
)

// SendPoll sends a poll (message.proto) through the same queue and send records as a text. The poll is whatsmeow's own
// (BuildPollCreation, msgsecret.go:402-421), with a new message secret, which whatsmeow's SendMessage stores for the
// votes (send.go:403-410), or the secret stored already for its id (storedSecret). A reply and mentions go on the poll,
// as mautrix sends them (matrixpoll.go:74-99 at 5dd621f).
func (svc *Service) SendPoll(ctx context.Context, req *whatsmeowv1.SendPollRequest) (*whatsmeowv1.SendPollResponse, error) {
	if req.GetQuestion() == "" {
		return nil, invalid("question", "the question is empty")
	}

	if len(req.GetOptions()) < 2 {
		return nil, invalid("options", "a poll needs at least two options")
	}

	if err := pollOptions(req.GetOptions()); err != nil {
		return nil, err
	}

	if req.GetMaxSelectable() < 1 || int(req.GetMaxSelectable()) > len(req.GetOptions()) {
		return nil, invalid("max_selectable", "%d is not from 1 to the %d options", req.GetMaxSelectable(), len(req.GetOptions()))
	}

	reply, err := newReplyContext(req.GetReplyTo(), req.GetMentions())
	if err != nil {
		return nil, err
	}

	sent, err := svc.send(ctx, req.GetSessionId(), req.GetChat(), req.GetClientMessageId(), func(ctx context.Context, wa whatsApp, chat types.JID) (*waE2E.Message, error) {
		message := wa.BuildPollCreation(req.GetQuestion(), req.GetOptions(), int(req.GetMaxSelectable()))
		message.PollCreationMessage.ContextInfo = reply.info

		secret, err := svc.storedSecret(ctx, req.GetSessionId(), chat, req.GetClientMessageId())
		if err != nil {
			return nil, err
		}

		if secret != nil {
			message.MessageContextInfo.MessageSecret = secret
		}

		return svc.named(req.GetSessionId(), reply, message)(ctx, wa, chat)
	})
	if err != nil {
		return nil, err
	}

	return &whatsmeowv1.SendPollResponse{Chat: sent.chat, MessageTime: timestamppb.New(sent.at)}, nil
}

// storedSecret is the message secret whatsmeow's store has for the account's message id in chat, or nil. whatsmeow's
// SendMessage stores a message's secret before it looks the recipient's devices up (send.go:403-410, 1198), and its
// store keeps the first secret of an id (store.go:882-885), so a poll whose send sent nothing (classify) can have its
// secret stored, and sent again it must carry that one: votes on it are decrypted with it. The account is named by
// either of its JIDs, as whatsmeow named it in chat (account); the store reads a phone number's chat as its LID too
// (store.go:887-905).
func (svc *Service) storedSecret(ctx context.Context, sessionID string, chat types.JID, id string) ([]byte, error) {
	device, err := svc.sessions.Device(sessionID)
	if err != nil {
		return nil, fmt.Errorf("poll: %w", err)
	}

	for _, account := range []types.JID{device.GetLID(), device.GetJID()} {
		if account.IsEmpty() {
			continue
		}

		secret, _, err := device.MsgSecrets.GetMessageSecret(ctx, chat, account, id)
		if err != nil {
			return nil, fmt.Errorf("poll: read its stored secret: %w", err)
		}

		if secret != nil {
			return secret, nil
		}
	}

	return nil, nil
}

// SendPollVote votes on a poll (message.proto), through the same queue and send records as a text. The vote is
// whatsmeow's own (BuildPollVote, msgsecret.go:391-445), encrypted with the poll's secret from whatsmeow's store, in
// the send's turn; a poll without one there fails before anything is recorded or sent. The account's own poll is
// named as whatsmeow names the account when it sends to the chat (account), which is how its SendMessage stored the
// secret.
func (svc *Service) SendPollVote(ctx context.Context, req *whatsmeowv1.SendPollVoteRequest) (*whatsmeowv1.SendPollVoteResponse, error) {
	if req.GetPollId() == "" {
		return nil, invalid("poll_id", "no poll")
	}

	var sender types.JID

	if req.GetPollSender() != "" {
		var err error
		if sender, err = user("poll_sender", req.GetPollSender()); err != nil {
			return nil, err
		}
	}

	if err := pollOptions(req.GetOptions()); err != nil {
		return nil, err
	}

	sent, err := svc.send(ctx, req.GetSessionId(), req.GetChat(), req.GetClientMessageId(), func(ctx context.Context, wa whatsApp, chat types.JID) (*waE2E.Message, error) {
		poll, err := svc.poll(ctx, req.GetSessionId(), wa, chat, sender, req.GetPollId())
		if err != nil {
			return nil, err
		}

		message, err := wa.BuildPollVote(ctx, poll, req.GetOptions())
		switch {
		case errors.Is(err, whatsmeow.ErrOriginalMessageSecretNotFound):
			return nil, fmt.Errorf("vote: %w", server.ErrPollSecretNotFound)
		case errors.Is(err, whatsmeow.ErrNotLoggedIn):
			return nil, svc.unnamed(req.GetSessionId(), err)
		case err != nil:
			return nil, fmt.Errorf("vote: %w", err)
		}

		return message, nil
	})
	if err != nil {
		return nil, err
	}

	return &whatsmeowv1.SendPollVoteResponse{Chat: sent.chat, MessageTime: timestamppb.New(sent.at)}, nil
}

// unnamed is why whatsmeow had no JID of the account to vote as, ErrNotLoggedIn (msgsecret.go:207-208): it votes as the
// account's LID on a LID's poll and by its phone number otherwise (429-432). With no device JID either, the device was
// deleted, as a logout deletes it (store.go:286-298), so the vote fails with LOGGED_OUT, as a send does (classify).
// Otherwise the session has no LID, which no reason says (ADR 0041): INTERNAL, without whatsmeow's text, which says
// there is no device JID.
func (svc *Service) unnamed(sessionID string, err error) error {
	device, deviceErr := svc.sessions.Device(sessionID)
	if deviceErr != nil {
		return fmt.Errorf("vote: %w", deviceErr)
	}

	if device.GetJID().IsEmpty() {
		_, reason := classify(err)

		return fmt.Errorf("vote: %w", reason)
	}

	return errors.New("vote: the session has no LID, which whatsmeow votes on a LID's poll as") //nolint:err113 // INTERNAL, ADR 0041
}

// poll is the info of the poll id in chat whose sender is sender, as BuildPollVote takes it: the account's own poll
// for no sender or the account's own JID, named as the account is in the chat.
func (svc *Service) poll(ctx context.Context, sessionID string, wa whatsApp, chat, sender types.JID, id string) (*types.MessageInfo, error) {
	device, err := svc.sessions.Device(sessionID)
	if err != nil {
		return nil, fmt.Errorf("vote: %w", err)
	}

	own := sender.IsEmpty() || isAccount(sender, device.GetJID(), device.GetLID())
	if own {
		if sender, err = svc.account(ctx, sessionID, wa, chat); err != nil {
			return nil, err
		}
	}

	return &types.MessageInfo{
		MessageSource: types.MessageSource{Chat: chat, Sender: sender, IsFromMe: own, IsGroup: chat.Server == types.GroupServer},
		ID:            id,
	}, nil
}

// isAccount is a JID of the account, by phone number or LID and from any device, as whatsmeow tells the account's own
// messages (BuildMessageKey, send.go:511-523).
func isAccount(j, pn, lid types.JID) bool {
	return (!pn.IsEmpty() && j.User == pn.User) || (!lid.IsEmpty() && j.User == lid.User)
}

// pollOptions checks a poll's or a vote's option names: none empty, none repeated. A vote names an option by the
// SHA-256 of its name (HashPollOptions, msgsecret.go:369-376), so a repeated name is the same option.
func pollOptions(options []string) error {
	seen := make(map[string]bool, len(options))

	for _, option := range options {
		if option == "" {
			return invalid("options", "an option is empty")
		}

		if seen[option] {
			return invalid("options", "an option is repeated")
		}

		seen[option] = true
	}

	return nil
}
