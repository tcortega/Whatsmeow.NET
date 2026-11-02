package events

import (
	"context"
	"strings"
	"time"

	"go.mau.fi/whatsmeow/proto/waCommon"
	"go.mau.fi/whatsmeow/proto/waE2E"
	"go.mau.fi/whatsmeow/proto/waWeb"
	"go.mau.fi/whatsmeow/store"
	"go.mau.fi/whatsmeow/types"
	waevents "go.mau.fi/whatsmeow/types/events"
	"google.golang.org/protobuf/proto"
	"google.golang.org/protobuf/types/known/timestamppb"

	whatsmeowv1 "github.com/tcortega/Whatsmeow.NET/sidecar/gen/whatsmeow/v1"
	"github.com/tcortega/Whatsmeow.NET/sidecar/internal/server"
)

// toEvent maps a whatsmeow event to the outbox event it records and that event's natural key, or false for an
// event this slice does not record. Connection events are not here: the session slice records them as it changes
// state (session.(*Service).transition). It is pure but for a poll vote's decryption (pollVote), which reads the
// poll's secret from whatsmeow's store. account is the session's device, which names the account, and votes decrypts
// a vote with its message secrets.
//
// The key ties a repeated event to its first recording, so it keeps its seq: whatsmeow dispatches a message again
// from its decrypted-event buffer after a crash between our commit and its buffer clear (message.go:459-477).
func toEvent(evt any, account *store.Device, votes voteDecrypter) (string, *whatsmeowv1.StreamEventsResponse, bool) {
	switch e := evt.(type) {
	case *waevents.Message:
		// A history sync notification is the History slice's (internal/history): never a live message.
		if onlySenderKey(e.Message) || e.Message.GetProtocolMessage().GetHistorySyncNotification() != nil {
			return "", nil, false
		}

		event := MessageEvent(e, account)
		event.PollVote = pollVote(e, votes)

		return key("m", &e.Info), &whatsmeowv1.StreamEventsResponse{Payload: &whatsmeowv1.StreamEventsResponse_Message{
			Message: event,
		}}, true
	case *waevents.UndecryptableMessage:
		return key("u", &e.Info), &whatsmeowv1.StreamEventsResponse{Payload: &whatsmeowv1.StreamEventsResponse_UndecryptableMessage{
			UndecryptableMessage: &whatsmeowv1.UndecryptableMessageEvent{
				Info:            info(&e.Info),
				HidePlaceholder: e.DecryptFailMode == waevents.DecryptFailHide,
				Unavailable:     e.IsUnavailable,
				UnavailableType: unavailableType(e.UnavailableType),
			},
		}}, true
	default:
		return "", nil, false
	}
}

// voteDecrypter decrypts a poll vote with the poll's message secret from the session's store: whatsmeow's own
// DecryptPollVote (msgsecret.go:305-320).
type voteDecrypter interface {
	DecryptPollVote(ctx context.Context, vote *waevents.Message) (*waE2E.PollVoteMessage, error)
}

// pollVote is a poll vote's selection, decrypted with votes; nil for every other message. A vote that does not decrypt
// (no secret for its poll here, one that fails its check, or no decrypter) is marked undecryptable and still recorded,
// so it is answered and delivered (events.proto PollVote). whatsmeow's error is dropped: the event says the vote is
// undecryptable, and the error's text names the voter and the poll's sender (msgsecret.go:191).
func pollVote(e *waevents.Message, votes voteDecrypter) *whatsmeowv1.PollVote {
	if e.Message.GetPollUpdateMessage() == nil {
		return nil
	}

	if votes == nil {
		return &whatsmeowv1.PollVote{Undecryptable: true}
	}

	// No caller's context: whatsmeow gives the handler none. SQLite's busy_timeout bounds the read.
	vote, err := votes.DecryptPollVote(context.Background(), e)
	if err != nil {
		return &whatsmeowv1.PollVote{Undecryptable: true}
	}

	return &whatsmeowv1.PollVote{SelectedOptionHashes: vote.GetSelectedOptions()}
}

// unavailableType is why WhatsApp sent no ciphertext, as events.proto has it; unspecified for none and for a type
// the contract does not have.
func unavailableType(t waevents.UnavailableType) whatsmeowv1.UnavailableType {
	if t == waevents.UnavailableTypeViewOnce {
		return whatsmeowv1.UnavailableType_UNAVAILABLE_TYPE_VIEW_ONCE
	}

	return whatsmeowv1.UnavailableType_UNAVAILABLE_TYPE_UNSPECIFIED
}

// MessageEvent is a whatsmeow message as the contract's MessageEvent: a live one's, and a history message's, which the
// History slice maps with it (internal/history), so a history message carries what a live one does. It carries what
// the message's wrappers said (UnwrapRaw, types/events/events.go:377-425): view-once, ephemeral, and the destination a
// device-sent message names. The raw message goes along only when whatsmeow unwrapped something, so a message without
// wrappers is not recorded twice. A reaction's, an edit's or a revoke's target is read from its key (target), and a
// reply's quote from its context (replyTo), where account, the session's device, names the account.
func MessageEvent(e *waevents.Message, account *store.Device) *whatsmeowv1.MessageEvent {
	event := &whatsmeowv1.MessageEvent{
		Info: info(&e.Info), Message: e.Message, ViewOnce: e.IsViewOnce, Ephemeral: e.IsEphemeral, Target: target(e, account),
		ReplyTo: replyTo(e.Message, account),
	}
	if e.Info.DeviceSentMeta != nil {
		event.DeviceSentDestination = server.Text(e.Info.DeviceSentMeta.DestinationJID)
	}

	if e.RawMessage != nil && e.RawMessage != e.Message {
		event.RawMessage = e.RawMessage
	}

	return event
}

// HistoryMessage is a history message as the contract's MessageEvents: its own (MessageEvent), then, for a poll, each
// vote its WebMessageInfo carries decrypted (pollUpdates, WAWebProtobufsWeb.proto:328, 598-605), in the list's order,
// as a live vote's event (ADR 0047). account is the session's device, which names the account.
func HistoryMessage(e *waevents.Message, account *store.Device) []*whatsmeowv1.MessageEvent {
	events := []*whatsmeowv1.MessageEvent{MessageEvent(e, account)}

	for _, u := range e.SourceWebMsg.GetPollUpdates() {
		if v := historyVote(u, &e.Info, account); v != nil {
			events = append(events, v)
		}
	}

	return events
}

// historyVote is a vote a history poll carries, as a live vote's event: its id and voter from its key, its sender's
// time (mautrix's for a history reaction, pkg/connector/backfill.go:909-930 at 5dd621f), the poll as its target and
// the selection as history has it, without a message, which history does not carry. nil for a key that names no voter.
// The key's unread flag and server time have no counterpart on a live vote, and are dropped.
func historyVote(u *waWeb.PollUpdate, poll *types.MessageInfo, account *store.Device) *whatsmeowv1.MessageEvent {
	k := u.GetPollUpdateMessageKey()

	sender, ok := voter(k, poll.Chat, account)
	if !ok {
		return nil
	}

	vote := types.MessageInfo{
		MessageSource: types.MessageSource{Chat: poll.Chat, Sender: sender, IsFromMe: k.GetFromMe(), IsGroup: poll.IsGroup},
		ID:            k.GetID(),
		Timestamp:     time.UnixMilli(u.GetSenderTimestampMS()),
	}

	return &whatsmeowv1.MessageEvent{
		Info:     info(&vote),
		Target:   named(poll.ID, poll.Sender, account),
		PollVote: &whatsmeowv1.PollVote{SelectedOptionHashes: u.GetVote().GetSelectedOptions()},
	}
}

// voter is who sent the message a history key names, as ParseWebMessage reads a history message's sender
// (client.go:1043-1062): the account when the key says so, by its LID in a LID chat and by its phone number otherwise,
// else the chat in a direct chat, else the key's participant as a user's JID. false for one that names none.
func voter(k *waCommon.MessageKey, chat types.JID, account *store.Device) (types.JID, bool) {
	if k.GetFromMe() {
		own := account.GetJID()
		if chat.Server == types.HiddenUserServer {
			own = account.GetLID()
		}

		return own.ToNonAD(), !own.IsEmpty()
	}

	switch chat.Server {
	case types.DefaultUserServer, types.HiddenUserServer, types.NewsletterServer:
		return chat, true
	default:
		return participant(k.GetParticipant())
	}
}

// onlySenderKey is a message with nothing but a sender key and its context info: the part of a group stanza that
// carries the sender key next to the skmsg content (send.go:795-831). whatsmeow dispatches each decrypted part as a
// Message with the same info (message.go:1172-1178) and stores the sender key itself (processProtocolParts,
// message.go:938-961), so recording it would take the content's natural key and drop the content.
func onlySenderKey(message *waE2E.Message) bool {
	if message.GetSenderKeyDistributionMessage() == nil {
		return false
	}

	rest := proto.CloneOf(message)
	rest.SenderKeyDistributionMessage = nil
	rest.MessageContextInfo = nil

	return proto.Size(rest) == 0
}

// key is a message's natural key: its kind, chat, sender and id. A redispatch parses the same stanza again, so it
// has the same JIDs.
func key(kind string, info *types.MessageInfo) string {
	return strings.Join([]string{kind, info.Chat.String(), info.Sender.String(), info.ID}, "\x00")
}

func info(info *types.MessageInfo) *whatsmeowv1.MessageInfo {
	return &whatsmeowv1.MessageInfo{
		Source:      source(&info.MessageSource),
		Id:          server.Text(info.ID),
		PushName:    server.Text(info.PushName),
		MessageTime: timestamppb.New(info.Timestamp),
	}
}

func source(source *types.MessageSource) *whatsmeowv1.MessageSource {
	return &whatsmeowv1.MessageSource{
		Chat:         server.Text(source.Chat.String()),
		Sender:       server.Text(source.Sender.String()),
		SenderAlt:    server.Text(source.SenderAlt.String()),
		RecipientAlt: server.Text(source.RecipientAlt.String()),
		IsFromMe:     source.IsFromMe,
	}
}

// target is the message a reaction, an edit or a revoke names by its key; nil for every other message, and for a key
// that names none (events.proto MessageTarget). The account's own message has no sender, however the key names the
// account, so an empty sender always means the account's (ADR 0038).
func target(e *waevents.Message, account *store.Device) *whatsmeowv1.MessageTarget {
	k := actionKey(e.Message)
	if k.GetID() == "" {
		return nil
	}

	sender, ok := keySender(k, &e.Info.MessageSource)
	if !ok {
		return nil
	}

	return named(k.GetID(), sender, account)
}

// named is the message id names, sent by sender, with no sender when that is the account (events.proto
// MessageTarget).
func named(id string, sender types.JID, account *store.Device) *whatsmeowv1.MessageTarget {
	t := &whatsmeowv1.MessageTarget{Id: server.Text(id)}
	if !sender.IsEmpty() && !isAccount(sender, account) {
		t.Sender = server.Text(sender.String())
	}

	return t
}

// replyTo is the message a reply quotes, named as a target is; nil for a message that quotes none, and for a quote
// whose participant names no user (events.proto MessageEvent.reply_to): with no sender it would read as the account's.
func replyTo(m *waE2E.Message, account *store.Device) *whatsmeowv1.MessageTarget {
	c := contextOf(m)
	if c.GetStanzaID() == "" {
		return nil
	}

	sender, ok := participant(c.GetParticipant())
	if !ok {
		return nil
	}

	return named(c.GetStanzaID(), sender, account)
}

// contextOf is the context of the content kinds the client maps, in the client's order (ContentMapping.ContextOf); an
// edit's is its new content's.
func contextOf(m *waE2E.Message) *waE2E.ContextInfo {
	if p := m.GetProtocolMessage(); p != nil && p.Type != nil && p.GetType() == waE2E.ProtocolMessage_MESSAGE_EDIT && p.GetEditedMessage() != nil {
		return contextOf(p.GetEditedMessage())
	}

	for _, c := range []*waE2E.ContextInfo{
		m.GetExtendedTextMessage().GetContextInfo(),
		m.GetImageMessage().GetContextInfo(),
		m.GetVideoMessage().GetContextInfo(),
		m.GetAudioMessage().GetContextInfo(),
		m.GetDocumentMessage().GetContextInfo(),
		m.GetStickerMessage().GetContextInfo(),
		pollCreation(m).GetContextInfo(),
	} {
		if c != nil {
			return c
		}
	}

	return nil
}

// pollCreation is a poll of any version the client maps, in the client's order (ContentMapping.PollOf): V1, V2, V3,
// V5 and V6 are one type (WAWebProtobufsE2E.proto); V4 is a FutureProofMessage, which mautrix does not map either
// (pkg/msgconv/from-whatsapp.go:174-185 at 5dd621f). nil for every other message.
func pollCreation(m *waE2E.Message) *waE2E.PollCreationMessage {
	for _, p := range []*waE2E.PollCreationMessage{
		m.GetPollCreationMessage(), m.GetPollCreationMessageV2(), m.GetPollCreationMessageV3(), m.GetPollCreationMessageV5(), m.GetPollCreationMessageV6(),
	} {
		if p != nil {
			return p
		}
	}

	return nil
}

// actionKey is the key of a reaction, a revoke, an edit or a poll vote, which whatsmeow unwrapped from its EditedMessage
// (types/events/events.go:377-425); nil for every other message. A protocol message is a revoke only when its type is
// set: REVOKE is the type's zero value.
func actionKey(m *waE2E.Message) *waCommon.MessageKey {
	if r := m.GetReactionMessage(); r != nil {
		return r.GetKey()
	}

	if v := m.GetPollUpdateMessage(); v != nil {
		return v.GetPollCreationMessageKey()
	}

	p := m.GetProtocolMessage()
	if p == nil || p.Type == nil {
		return nil
	}

	switch p.GetType() { //nolint:exhaustive // only these two name a message to act on
	case waE2E.ProtocolMessage_REVOKE, waE2E.ProtocolMessage_MESSAGE_EDIT:
		return p.GetKey()
	default:
		return nil
	}
}

// keySender is the sender of the message a key names, without a device, as mautrix reads it (KeyToMessageID,
// pkg/msgconv/wa-poll.go:97-132 at 5dd621f): the actor when the key says the actor sent it, else its participant,
// else, in a direct chat, the other side: the chat when the account acted, else the account, which is the empty JID.
// Outside a direct chat such a key names nobody, nor does a participant that is not a user's JID: false.
func keySender(k *waCommon.MessageKey, src *types.MessageSource) (types.JID, bool) {
	if k.GetFromMe() {
		return src.Sender.ToNonAD(), true
	}

	if p := k.GetParticipant(); p != "" {
		return participant(p)
	}

	switch src.Chat.Server {
	case types.DefaultUserServer, types.HiddenUserServer, types.BotServer:
		if src.IsFromMe {
			return src.Chat.ToNonAD(), true
		}

		return types.EmptyJID, true
	default:
		return types.JID{}, false
	}
}

// participant is a key's or a quote's participant as a user's JID without a device, c.us read as s.whatsapp.net as
// mautrix reads it (pkg/msgconv/wa-poll.go:97-132 at 5dd621f); false for one that is not a JID, has no user, or whose
// server is not one of a user's, the servers whatsmeow keeps a user's devices for (send.go:482). One without a server
// would be the empty JID, which is the account's, and a group's is no sender.
func participant(p string) (types.JID, bool) {
	j, err := types.ParseJID(p)
	if err != nil || j.User == "" {
		return types.JID{}, false
	}

	if j.Server == types.LegacyUserServer {
		j.Server = types.DefaultUserServer
	}

	switch j.Server {
	case types.DefaultUserServer, types.HiddenUserServer, types.BotServer, types.HostedServer, types.HostedLIDServer:
		return j.ToNonAD(), true
	default:
		return types.JID{}, false
	}
}

// isAccount is a JID of the account, by its phone number or LID and from any device, as whatsmeow tells the account's
// own messages (BuildMessageKey, send.go:511-523; parseMessageSource, message.go:110-111).
func isAccount(j types.JID, account *store.Device) bool {
	pn, lid := account.GetJID(), account.GetLID()

	return (!pn.IsEmpty() && j.User == pn.User) || (!lid.IsEmpty() && j.User == lid.User)
}
