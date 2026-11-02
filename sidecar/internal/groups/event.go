package groups

import (
	"go.mau.fi/whatsmeow/types"
	"go.mau.fi/whatsmeow/types/events"
	"google.golang.org/protobuf/types/known/timestamppb"

	whatsmeowv1 "github.com/tcortega/Whatsmeow.NET/sidecar/gen/whatsmeow/v1"
	"github.com/tcortega/Whatsmeow.NET/sidecar/internal/server"
)

// Event is the slice's events.Mapper: it maps a group notification whatsmeow dispatched to the GroupEvent the outbox
// records, or false for any other event, and for a change none of whose parts the contract has. It is pure: no I/O.
//
// These are gated: whatsmeow acks a w:gp2 notification only when no handler failed (notification.go:493-506,
// maybeDeferredAck receipt.go:115-128), so the handler's false after a failed write has WhatsApp send it again. The key
// is empty: the event carries no stanza id (types/events/events.go:480-528), and a key from its content would merge
// two real changes alike in the same second, a loss, where a redelivery recorded twice is a duplicate (ADR 0022).
func Event(_ string, evt any) (string, *whatsmeowv1.StreamEventsResponse, bool) {
	var event *whatsmeowv1.GroupEvent

	switch e := evt.(type) {
	case *events.GroupInfo:
		changes := changesOf(e)
		if len(changes) == 0 {
			return "", nil, false
		}

		event = &whatsmeowv1.GroupEvent{
			Group: text(e.JID), Sender: optional(e.Sender), SenderAlt: optional(e.SenderPN),
			Kind: &whatsmeowv1.GroupEvent_Changed_{Changed: &whatsmeowv1.GroupEvent_Changed{Changes: changes}},
		}
		if !e.Timestamp.IsZero() {
			event.Time = timestamppb.New(e.Timestamp)
		}
	case *events.JoinedGroup:
		event = &whatsmeowv1.GroupEvent{
			Group: text(e.JID), Sender: optional(e.Sender), SenderAlt: optional(e.SenderPN),
			Kind: &whatsmeowv1.GroupEvent_Joined_{Joined: &whatsmeowv1.GroupEvent_Joined{Group: toGroup(&e.GroupInfo)}},
		}
	default:
		return "", nil, false
	}

	return "", &whatsmeowv1.StreamEventsResponse{Payload: &whatsmeowv1.StreamEventsResponse_Group{Group: event}}, true
}

// changesOf is the contract's changes of e, in the contract's order. Approval mode, link, unlink, invite link, delete
// and suspension changes have no case and are left out.
func changesOf(e *events.GroupInfo) []*whatsmeowv1.GroupChange {
	var changes []*whatsmeowv1.GroupChange

	add := func(change *whatsmeowv1.GroupChange) { changes = append(changes, change) }

	if e.Name != nil {
		add(&whatsmeowv1.GroupChange{Change: &whatsmeowv1.GroupChange_Renamed_{Renamed: &whatsmeowv1.GroupChange_Renamed{Name: server.Text(e.Name.Name)}}})
	}

	if e.Topic != nil {
		// A removed topic is empty (group.go:921-946).
		add(&whatsmeowv1.GroupChange{Change: &whatsmeowv1.GroupChange_TopicChanged_{TopicChanged: &whatsmeowv1.GroupChange_TopicChanged{Topic: server.Text(e.Topic.Topic)}}})
	}

	if e.Announce != nil {
		add(&whatsmeowv1.GroupChange{Change: &whatsmeowv1.GroupChange_AnnounceChanged_{AnnounceChanged: &whatsmeowv1.GroupChange_AnnounceChanged{Announce: e.Announce.IsAnnounce}}})
	}

	if e.Locked != nil {
		add(&whatsmeowv1.GroupChange{Change: &whatsmeowv1.GroupChange_LockedChanged_{LockedChanged: &whatsmeowv1.GroupChange_LockedChanged{Locked: e.Locked.IsLocked}}})
	}

	// whatsmeow has no case for a member_add_mode child and keeps it in UnknownChanges as it came (group.go:1006-1007);
	// its content is the mode, as in the IQ that sets it and in a group's info (group.go:785-787,
	// 1084-1097) [inferred: WhatsApp's notification names and fills it the same way]. A mode the contract does not have
	// is left out.
	for _, node := range e.UnknownChanges {
		content, _ := node.Content.([]byte)
		if mode := addMode(types.GroupMemberAddMode(content)); node.Tag == "member_add_mode" && mode != whatsmeowv1.GroupMemberAddMode_GROUP_MEMBER_ADD_MODE_UNSPECIFIED {
			add(&whatsmeowv1.GroupChange{Change: &whatsmeowv1.GroupChange_MemberAddModeChanged_{MemberAddModeChanged: &whatsmeowv1.GroupChange_MemberAddModeChanged{Mode: mode}}})

			break
		}
	}

	if e.Ephemeral != nil {
		// not_ephemeral leaves the timer zero (group.go:963-970).
		add(&whatsmeowv1.GroupChange{Change: &whatsmeowv1.GroupChange_DisappearingTimerChanged_{DisappearingTimerChanged: &whatsmeowv1.GroupChange_DisappearingTimerChanged{Timer: timer(e.Ephemeral.DisappearingTimer)}}})
	}

	if len(e.Join) > 0 {
		add(&whatsmeowv1.GroupChange{Change: &whatsmeowv1.GroupChange_Joined{Joined: participants(e.Join)}})
	}

	if len(e.Leave) > 0 {
		add(&whatsmeowv1.GroupChange{Change: &whatsmeowv1.GroupChange_Left{Left: participants(e.Leave)}})
	}

	if len(e.Promote) > 0 {
		add(&whatsmeowv1.GroupChange{Change: &whatsmeowv1.GroupChange_Promoted{Promoted: participants(e.Promote)}})
	}

	if len(e.Demote) > 0 {
		add(&whatsmeowv1.GroupChange{Change: &whatsmeowv1.GroupChange_Demoted{Demoted: participants(e.Demote)}})
	}

	return changes
}

func participants(jids []types.JID) *whatsmeowv1.GroupChange_Participants {
	list := &whatsmeowv1.GroupChange_Participants{Jids: make([]string, len(jids))}
	for i, jid := range jids {
		list.Jids[i] = text(jid)
	}

	return list
}

// optional is the string form of a JID whatsmeow may leave nil, empty for nil or the empty JID.
func optional(jid *types.JID) string {
	if jid == nil {
		return ""
	}

	return text(*jid)
}
