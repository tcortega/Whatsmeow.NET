package chatstate

import (
	"testing"
	"time"

	"go.mau.fi/whatsmeow/proto/waSyncAction"
	"go.mau.fi/whatsmeow/types"
	"go.mau.fi/whatsmeow/types/events"
	"google.golang.org/protobuf/proto"
	"google.golang.org/protobuf/types/known/timestamppb"

	whatsmeowv1 "github.com/tcortega/Whatsmeow.NET/sidecar/gen/whatsmeow/v1"
)

func TestEachChatStateEventIsMappedUnkeyedAndOthersAreNot(t *testing.T) {
	t.Parallel()

	at := time.UnixMilli(1_700_000_000_123)
	until := time.UnixMilli(1_800_000_000_456)
	chat := types.NewADJID("5511987654321", 0, 3)
	groupChat := types.NewJID("120363000000000001", types.GroupServer)
	sender := types.NewADJID("5511912345678", 0, 2)

	event := func(chat string, fullSync bool, change func(*whatsmeowv1.ChatStateEvent)) *whatsmeowv1.StreamEventsResponse {
		e := &whatsmeowv1.ChatStateEvent{Chat: chat, ChangeTime: timestamppb.New(at), FromFullSync: fullSync}
		change(e)

		return &whatsmeowv1.StreamEventsResponse{Payload: &whatsmeowv1.StreamEventsResponse_ChatState{ChatState: e}}
	}

	for _, tc := range []struct {
		name string
		evt  any
		want *whatsmeowv1.StreamEventsResponse
	}{
		{
			"muted until", &events.Mute{JID: chat, Timestamp: at, Action: &waSyncAction.MuteAction{Muted: proto.Bool(true), MuteEndTimestamp: proto.Int64(until.UnixMilli())}},
			event(direct, false, func(e *whatsmeowv1.ChatStateEvent) {
				e.Change = &whatsmeowv1.ChatStateEvent_Mute{Mute: &whatsmeowv1.ChatMute{Muted: true, Until: timestamppb.New(until)}}
			}),
		},
		// -1 is forever (appstate.go:280-282); 0 is too, as mautrix reads it.
		{
			"muted forever", &events.Mute{JID: chat, Timestamp: at, Action: &waSyncAction.MuteAction{Muted: proto.Bool(true), MuteEndTimestamp: proto.Int64(-1)}, FromFullSync: true},
			event(direct, true, func(e *whatsmeowv1.ChatStateEvent) {
				e.Change = &whatsmeowv1.ChatStateEvent_Mute{Mute: &whatsmeowv1.ChatMute{Muted: true}}
			}),
		},
		{
			"muted with no end", &events.Mute{JID: chat, Timestamp: at, Action: &waSyncAction.MuteAction{Muted: proto.Bool(true), MuteEndTimestamp: proto.Int64(0)}},
			event(direct, false, func(e *whatsmeowv1.ChatStateEvent) {
				e.Change = &whatsmeowv1.ChatStateEvent_Mute{Mute: &whatsmeowv1.ChatMute{Muted: true}}
			}),
		},
		{
			"unmuted", &events.Mute{JID: chat, Timestamp: at, Action: &waSyncAction.MuteAction{Muted: proto.Bool(false), MuteEndTimestamp: proto.Int64(until.UnixMilli())}},
			event(direct, false, func(e *whatsmeowv1.ChatStateEvent) {
				e.Change = &whatsmeowv1.ChatStateEvent_Mute{Mute: &whatsmeowv1.ChatMute{}}
			}),
		},
		{
			"pinned", &events.Pin{JID: chat, Timestamp: at, Action: &waSyncAction.PinAction{Pinned: proto.Bool(true)}, FromFullSync: true},
			event(direct, true, func(e *whatsmeowv1.ChatStateEvent) {
				e.Change = &whatsmeowv1.ChatStateEvent_Pin{Pin: &whatsmeowv1.ChatPin{Pinned: true}}
			}),
		},
		{
			"archived", &events.Archive{JID: groupChat, Timestamp: at, Action: &waSyncAction.ArchiveChatAction{Archived: proto.Bool(true)}},
			event(group, false, func(e *whatsmeowv1.ChatStateEvent) {
				e.Change = &whatsmeowv1.ChatStateEvent_Archive{Archive: &whatsmeowv1.ChatArchive{Archived: true}}
			}),
		},
		{
			"marked unread", &events.MarkChatAsRead{JID: chat, Timestamp: at, Action: &waSyncAction.MarkChatAsReadAction{Read: proto.Bool(false)}},
			event(direct, false, func(e *whatsmeowv1.ChatStateEvent) {
				e.Change = &whatsmeowv1.ChatStateEvent_Read{Read: &whatsmeowv1.ChatRead{}}
			}),
		},
		{
			"starred in a group", &events.Star{ChatJID: groupChat, SenderJID: sender, MessageID: "M1", Timestamp: at, Action: &waSyncAction.StarAction{Starred: proto.Bool(true)}},
			event(group, false, func(e *whatsmeowv1.ChatStateEvent) {
				e.Change = &whatsmeowv1.ChatStateEvent_Star{Star: &whatsmeowv1.MessageStar{MessageId: "M1", Sender: member, Starred: true}}
			}),
		},
		{
			"the account's own message unstarred", &events.Star{ChatJID: chat, IsFromMe: true, MessageID: "M2", Timestamp: at, Action: &waSyncAction.StarAction{Starred: proto.Bool(false)}},
			event(direct, false, func(e *whatsmeowv1.ChatStateEvent) {
				e.Change = &whatsmeowv1.ChatStateEvent_Star{Star: &whatsmeowv1.MessageStar{MessageId: "M2", IsFromMe: true}}
			}),
		},
		{"another app state event", &events.Contact{JID: chat}, nil},
		{"a sync error", &events.AppStateSyncError{Name: "regular_low"}, nil},
	} {
		t.Run(tc.name, func(t *testing.T) {
			t.Parallel()

			key, got, ok := Event("s1", tc.evt)
			if ok != (tc.want != nil) || key != "" || !proto.Equal(got, tc.want) {
				t.Fatalf("Event = %q, %v, %v; want \"\", %v", key, got, ok, tc.want)
			}
		})
	}
}
