package events

import (
	"testing"
	"time"

	"go.mau.fi/whatsmeow/proto/waCommon"
	"go.mau.fi/whatsmeow/proto/waE2E"
	"go.mau.fi/whatsmeow/proto/waWeb"
	"go.mau.fi/whatsmeow/types"
	waevents "go.mau.fi/whatsmeow/types/events"
	"google.golang.org/protobuf/proto"
	"google.golang.org/protobuf/types/known/timestamppb"

	whatsmeowv1 "github.com/tcortega/Whatsmeow.NET/sidecar/gen/whatsmeow/v1"
)

func TestAHistorySyncNotificationIsNeverALiveMessage(t *testing.T) {
	t.Parallel()

	for _, fromMe := range []bool{true, false} {
		notification := messageEvent("N1")
		notification.Info.IsFromMe = fromMe
		notification.Message = &waE2E.Message{ProtocolMessage: &waE2E.ProtocolMessage{
			Type:                    waE2E.ProtocolMessage_HISTORY_SYNC_NOTIFICATION.Enum(),
			HistorySyncNotification: &waE2E.HistorySyncNotification{DirectPath: proto.String("/blob"), InitialHistBootstrapInlinePayload: []byte{1}},
		}}

		// The History slice queues it (internal/history); a bot must never answer it, and its inline payload has no
		// bound a gRPC message keeps.
		if key, got, ok, err := toEvent(notification, nil, nil); ok || err != nil {
			t.Fatalf("from me %v: recorded under %q as %v, %v; want nothing", fromMe, key, got, err)
		}
	}
}

func TestMessageEventIsTheMappingOfALiveMessage(t *testing.T) {
	t.Parallel()

	m := messageEvent("M1")
	m.Message = &waE2E.Message{ReactionMessage: &waE2E.ReactionMessage{Key: &waCommon.MessageKey{ID: proto.String("T1"), Participant: proto.String(accountLID)}}}
	_, live, _, err := toEvent(m, account(), nil)
	if err != nil {
		t.Fatalf("toEvent: %v", err)
	}

	if got := MessageEvent(m, account()); !proto.Equal(got, live.GetMessage()) || got.GetTarget() == nil {
		t.Fatalf("MessageEvent %v, want the live message's %v", got, live.GetMessage())
	}
}

// A history poll carries its votes decrypted (WebMessageInfo.pollUpdates): each follows the poll as a live vote's
// event, its voter read from the vote's key as ParseWebMessage reads a history message's sender (client.go:1045-1064),
// its poll the target, without a message, which history does not carry. A vote that names no voter is dropped.
func TestAHistoryPollsVotesFollowItAsLiveVotes(t *testing.T) {
	t.Parallel()

	const (
		groupChat = "120363000000000000@g.us"
		member    = "5511912345678@s.whatsapp.net"
		peer      = "222@lid"
		bot       = "867051314767696@bot"
	)

	at := int64(1_600_000_000_123)
	update := func(key *waCommon.MessageKey, hashes ...[]byte) *waWeb.PollUpdate {
		return &waWeb.PollUpdate{
			PollUpdateMessageKey: key, Vote: &waE2E.PollVoteMessage{SelectedOptions: hashes}, SenderTimestampMS: proto.Int64(at),
			ServerTimestampMS: proto.Int64(at + 1), Unread: proto.Bool(true),
		}
	}
	vkey := func(chat, id string, fromMe bool, participant string) *waCommon.MessageKey {
		k := &waCommon.MessageKey{RemoteJID: proto.String(chat), FromMe: proto.Bool(fromMe), ID: proto.String(id)}
		if participant != "" {
			k.Participant = proto.String(participant)
		}

		return k
	}
	poll := func(chat, sender, id string, fromMe bool, updates ...*waWeb.PollUpdate) *waevents.Message {
		return &waevents.Message{
			Info: types.MessageInfo{
				MessageSource: types.MessageSource{Chat: jid(chat), Sender: jid(sender), IsFromMe: fromMe, IsGroup: jid(chat).Server == types.GroupServer},
				ID:            id, Timestamp: time.Unix(1_600_000_000, 0),
			},
			Message:      &waE2E.Message{PollCreationMessage: &waE2E.PollCreationMessage{Name: proto.String("Lunch?")}},
			SourceWebMsg: &waWeb.WebMessageInfo{PollUpdates: updates},
		}
	}
	vote := func(chat, sender, id string, fromMe bool, target *whatsmeowv1.MessageTarget, hashes ...[]byte) *whatsmeowv1.MessageEvent {
		return &whatsmeowv1.MessageEvent{
			Info: &whatsmeowv1.MessageInfo{
				Source: &whatsmeowv1.MessageSource{Chat: chat, Sender: sender, IsFromMe: fromMe}, Id: id, MessageTime: timestamppb.New(time.UnixMilli(at)),
			},
			Target: target, PollVote: &whatsmeowv1.PollVote{SelectedOptionHashes: hashes},
		}
	}

	pizza, sushi := []byte("pizza-hash"), []byte("sushi-hash")
	cases := []struct {
		name string
		poll *waevents.Message
		want []*whatsmeowv1.MessageEvent
	}{
		{
			"a member's group poll, its sender with a device as ParseWebMessage keeps a participant's (client.go:1060-1062): a " +
				"LID member's vote, the account's by its phone number, one that names no voter",
			poll(groupChat, "5511912345678:3@s.whatsapp.net", "P1", false,
				update(vkey(groupChat, "V1", false, "333:2@lid"), pizza, sushi),
				update(vkey(groupChat, "V2", true, ""), sushi),
				update(vkey(groupChat, "V3", false, "")),
			),
			[]*whatsmeowv1.MessageEvent{
				vote(groupChat, "333@lid", "V1", false, &whatsmeowv1.MessageTarget{Id: "P1", Sender: member}, pizza, sushi),
				vote(groupChat, "5511900000001@s.whatsapp.net", "V2", true, &whatsmeowv1.MessageTarget{Id: "P1", Sender: member}, sushi),
			},
		},
		{
			"the account's poll in a LID chat: the other side's vote, and the account's cleared one by its LID",
			poll(peer, "900000001@lid", "P2", true, update(vkey(peer, "V4", false, ""), pizza), update(vkey(peer, "V5", true, ""))),
			[]*whatsmeowv1.MessageEvent{
				vote(peer, peer, "V4", false, &whatsmeowv1.MessageTarget{Id: "P2"}, pizza),
				vote(peer, "900000001@lid", "V5", true, &whatsmeowv1.MessageTarget{Id: "P2"}),
			},
		},
		{
			"the account's poll in a bot chat: the bot's vote names no participant, so it is the chat's (client.go:1058-1059)",
			poll(bot, "5511900000001@s.whatsapp.net", "P4", true, update(vkey(bot, "V6", false, ""), pizza)),
			[]*whatsmeowv1.MessageEvent{vote(bot, bot, "V6", false, &whatsmeowv1.MessageTarget{Id: "P4"}, pizza)},
		},
		{"a poll without votes", poll(peer, peer, "P3", false), nil},
	}

	for _, c := range cases {
		got := HistoryMessage(c.poll, account())

		want := append([]*whatsmeowv1.MessageEvent{MessageEvent(c.poll, account())}, c.want...)
		if len(got) != len(want) {
			t.Fatalf("%s: %d events %v, want %d", c.name, len(got), got, len(want))
		}

		for i := range want {
			if !proto.Equal(got[i], want[i]) {
				t.Fatalf("%s: event %d\n%v\nwant\n%v", c.name, i, got[i], want[i])
			}
		}
	}
}
