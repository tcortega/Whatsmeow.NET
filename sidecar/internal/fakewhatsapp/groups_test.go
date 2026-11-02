//go:build fakewhatsapp

package fakewhatsapp

import (
	"context"
	"errors"
	"fmt"
	"slices"
	"testing"
	"time"

	"go.mau.fi/whatsmeow"
	"go.mau.fi/whatsmeow/types"
	"go.mau.fi/whatsmeow/types/events"
	"google.golang.org/protobuf/proto"
	"google.golang.org/protobuf/types/known/timestamppb"

	fakev1 "github.com/tcortega/Whatsmeow.NET/sidecar/gen/whatsmeow/fake/v1"
	"github.com/tcortega/Whatsmeow.NET/sidecar/internal/groups"
	sidecarstore "github.com/tcortega/Whatsmeow.NET/sidecar/internal/store"
)

var _ groups.Client = (*Client)(nil)

// second links session s2 as 1666:1@s.whatsapp.net on linked's WhatsApp and store, and returns its client and its
// events.
func second(ctx context.Context, t *testing.T, whatsApp *WhatsApp, control fakev1.FakeWhatsAppServiceClient, db *sidecarstore.Store) (*Client, chan any) {
	t.Helper()

	got := make(chan any, 16)
	client := whatsApp.NewClient("s2")
	client.Store = db.Devices.NewDevice()
	client.AddEventHandlerWithSuccessStatus(func(evt any) bool { got <- evt; return true })

	if _, err := control.LinkDevice(ctx, &fakev1.LinkDeviceRequest{SessionId: "s2", Jid: "1666:1@s.whatsapp.net"}); err != nil {
		t.Fatalf("LinkDevice: %v", err)
	}

	return client, got
}

func TestAGroupCallChangesTheGroupAndNotifiesEveryMember(t *testing.T) {
	t.Parallel()

	whatsApp, control, ctx, owner, ownerGot, db := linked(t)
	member, memberGot := second(ctx, t, whatsApp, control, db)

	created, err := owner.CreateGroup(ctx, whatsmeow.ReqCreateGroup{Name: "Team", Participants: []types.JID{parse(t, "1666@s.whatsapp.net")}})
	if err != nil {
		t.Fatalf("CreateGroup: %v", err)
	}

	// The member added gets a create notification with the group (group.go:860-884); the creator none [inferred].
	if joined := next[*events.JoinedGroup](t, memberGot); joined.JID != created.JID || joined.Name != "Team" || joined.Sender.String() != "1555@s.whatsapp.net" {
		t.Fatalf("the member got %+v", joined)
	}

	if err := owner.SetGroupName(ctx, created.JID, "Crew"); err != nil {
		t.Fatalf("SetGroupName: %v", err)
	}

	// Every member, the one who made the change too [inferred: send.go:665].
	for _, got := range []chan any{ownerGot, memberGot} {
		if change := next[*events.GroupInfo](t, got); change.Name == nil || change.Name.Name != "Crew" || change.Sender.String() != "1555@s.whatsapp.net" {
			t.Fatalf("a member got %+v", change)
		}
	}

	info, err := member.GetGroupInfo(ctx, created.JID)
	if err != nil || info.Name != "Crew" || len(info.Participants) != 2 || !info.Participants[0].IsSuperAdmin || info.Participants[1].IsAdmin {
		t.Fatalf("the member reads %+v, %v", info, err)
	}

	// Only an admin changes the settings [inferred: 403].
	if err := member.SetGroupLocked(ctx, created.JID, true); !errors.Is(err, whatsmeow.ErrIQForbidden) {
		t.Fatalf("a member's SetGroupLocked = %v, want 403", err)
	}

	if err := owner.SetGroupLocked(ctx, created.JID, true); err != nil {
		t.Fatalf("SetGroupLocked: %v", err)
	}

	// And, once it is locked, the info.
	if err := member.SetGroupName(ctx, created.JID, "Mine"); !errors.Is(err, whatsmeow.ErrIQForbidden) {
		t.Fatalf("a member's SetGroupName in a locked group = %v, want 403", err)
	}

	if err := member.LeaveGroup(ctx, created.JID); err != nil {
		t.Fatalf("LeaveGroup: %v", err)
	}

	if _, err := member.GetGroupInfo(ctx, created.JID); !errors.Is(err, whatsmeow.ErrNotInGroup) {
		t.Fatalf("the member who left reads the group: %v", err)
	}
}

func TestAddingAnswersForEachParticipant(t *testing.T) {
	t.Parallel()

	_, control, ctx, owner, _, _ := linked(t)
	if _, err := control.RefuseGroupAdd(ctx, &fakev1.RefuseGroupAddRequest{Jid: "5511900000003@s.whatsapp.net", Code: 403, Invite: true}); err != nil {
		t.Fatalf("RefuseGroupAdd: %v", err)
	}

	created, err := owner.CreateGroup(ctx, whatsmeow.ReqCreateGroup{Name: "Team", Participants: []types.JID{parse(t, "5511900000003@s.whatsapp.net")}})
	if err != nil {
		t.Fatalf("CreateGroup: %v", err)
	}

	// The answer lists the refused participant with its error and the invite offered [inferred].
	if refused := created.Participants[1]; refused.Error != 403 || refused.AddRequest == nil || refused.AddRequest.Code == "" {
		t.Fatalf("the refused participant is %+v", refused)
	}

	results, err := owner.UpdateGroupParticipants(ctx, created.JID, []types.JID{
		parse(t, "5511900000002@s.whatsapp.net"), parse(t, "5511900000003@s.whatsapp.net"), parse(t, "1555@s.whatsapp.net"),
	}, whatsmeow.ParticipantChangeAdd)
	if err != nil {
		t.Fatalf("UpdateGroupParticipants: %v", err)
	}

	codes := make([]int, len(results))
	for i, r := range results {
		codes[i] = r.Error
	}

	// Added, refused, already a member [inferred: 409].
	if fmt.Sprint(codes) != "[0 403 409]" {
		t.Fatalf("the codes are %v", codes)
	}
}

func TestAResetLinkStopsWorkingAndTheNewOneJoins(t *testing.T) {
	t.Parallel()

	whatsApp, control, ctx, owner, _, db := linked(t)
	member, memberGot := second(ctx, t, whatsApp, control, db)

	created, err := owner.CreateGroup(ctx, whatsmeow.ReqCreateGroup{Name: "Team"})
	if err != nil {
		t.Fatalf("CreateGroup: %v", err)
	}

	old, err := owner.GetGroupInviteLink(ctx, created.JID, false)
	if err != nil {
		t.Fatalf("GetGroupInviteLink: %v", err)
	}

	if again, err := owner.GetGroupInviteLink(ctx, created.JID, false); err != nil || again != old {
		t.Fatalf("the link read again is %q, %v; want %q", again, err, old)
	}

	reset, err := owner.GetGroupInviteLink(ctx, created.JID, true)
	if err != nil || reset == old {
		t.Fatalf("the reset link is %q, %v", reset, err)
	}

	// As whatsmeow names them (group.go:516-519).
	if _, err := member.JoinGroupWithLink(ctx, old); !errors.Is(err, whatsmeow.ErrInviteLinkRevoked) {
		t.Fatalf("joining with the old link = %v", err)
	}

	if _, err := member.JoinGroupWithLink(ctx, "https://chat.whatsapp.com/NOPE"); !errors.Is(err, whatsmeow.ErrInviteLinkInvalid) {
		t.Fatalf("joining with an unknown link = %v", err)
	}

	if _, err := member.GetGroupInviteLink(ctx, created.JID, false); !errors.Is(err, whatsmeow.ErrNotInGroup) {
		t.Fatalf("a non-member's link = %v", err)
	}

	joined, err := member.JoinGroupWithLink(ctx, reset)
	if err != nil || joined != created.JID {
		t.Fatalf("JoinGroupWithLink = %v, %v", joined, err)
	}

	if evt := next[*events.JoinedGroup](t, memberGot); evt.Reason != "invite" || evt.JID != created.JID {
		t.Fatalf("the member got %+v", evt)
	}

	// A member, not an admin: the link is 401 (group.go:432).
	if _, err := member.GetGroupInviteLink(ctx, created.JID, false); !errors.Is(err, whatsmeow.ErrGroupInviteLinkUnauthorized) {
		t.Fatalf("a member's link = %v", err)
	}
}

func TestALidAddressedGroupAnswersEachParticipantByLidWithThePhoneNumberBeside(t *testing.T) {
	t.Parallel()

	_, control, ctx, client, _, _ := linked(t)
	if _, err := control.RegisterUser(ctx, &fakev1.RegisterUserRequest{PhoneJid: pn, Lid: lid}); err != nil {
		t.Fatalf("RegisterUser: %v", err)
	}

	if _, err := control.AddGroup(ctx, &fakev1.AddGroupRequest{
		Jid: "120363000000000009@g.us", Name: "Team", LidAddressed: true, Owner: pn,
		Members: []*fakev1.AddGroupRequest_Member{{Jid: pn, Type: "superadmin"}, {Jid: "1555@s.whatsapp.net"}},
	}); err != nil {
		t.Fatalf("AddGroup: %v", err)
	}

	info, err := client.GetGroupInfo(ctx, parse(t, "120363000000000009@g.us"))
	if err != nil {
		t.Fatalf("GetGroupInfo: %v", err)
	}

	// A member the directory has no LID for keeps the phone number.
	want := []types.GroupParticipant{
		{JID: parse(t, lid), LID: parse(t, lid), PhoneNumber: parse(t, pn), IsAdmin: true, IsSuperAdmin: true},
		{JID: parse(t, "1555@s.whatsapp.net"), PhoneNumber: parse(t, "1555@s.whatsapp.net")},
	}
	if !slices.Equal(info.Participants, want) || info.OwnerJID.String() != lid || info.OwnerPN.String() != pn {
		t.Fatalf("GetGroupInfo = %+v", info)
	}
}

func TestDeliverGroupNotificationDispatchesTheChange(t *testing.T) {
	t.Parallel()

	_, control, ctx, _, got, _ := linked(t)
	at := time.Date(2026, 11, 2, 10, 0, 0, 0, time.UTC)

	if _, err := control.DeliverGroupNotification(ctx, &fakev1.DeliverGroupNotificationRequest{
		SessionId: "s1", Group: "120363000000000008@g.us", Sender: lid, SenderPn: pn, Time: timestamppb.New(at),
		Name: proto.String("Crew"), Locked: proto.Bool(false), Join: []string{"222@lid"},
	}); err != nil {
		t.Fatalf("DeliverGroupNotification: %v", err)
	}

	change := next[*events.GroupInfo](t, got)
	if change.JID.String() != "120363000000000008@g.us" || change.Sender.String() != lid || change.SenderPN.String() != pn || !change.Timestamp.Equal(at) ||
		change.Name.Name != "Crew" || change.Locked == nil || change.Locked.IsLocked || fmt.Sprint(change.Join) != "[222@lid]" || change.Topic != nil {
		t.Fatalf("the client got %+v", change)
	}
}

func TestAGroupNotificationIsAckedOnlyWhenEveryHandlerSucceeded(t *testing.T) {
	t.Parallel()

	whatsApp, control, ctx := start(t)
	fail := true
	whatsApp.NewClient("s1").AddEventHandlerWithSuccessStatus(func(any) bool { return !fail })

	req := &fakev1.DeliverGroupNotificationRequest{SessionId: "s1", Id: "GN-FAILED", Group: "120363000000000008@g.us", Name: proto.String("Crew")}
	if _, err := control.DeliverGroupNotification(ctx, req); err != nil {
		t.Fatalf("DeliverGroupNotification: %v", err)
	}

	// The failed one is not acked (notification.go:493-506, receipt.go:115-128): the next one's ack, of the stanza's
	// class, id and type (sendAck, receipt.go:151-181), is the first thing sent.
	fail = false
	req.Id = "GN-NEXT"

	if _, err := control.DeliverGroupNotification(ctx, req); err != nil {
		t.Fatalf("DeliverGroupNotification: %v", err)
	}

	wantSent(t, waitForSent(ctx, t, control, "s1", 0), &fakev1.Sent{
		Index: 1, SessionId: "s1", Kind: &fakev1.Sent_Ack{Ack: &fakev1.Ack{Class: "notification", Id: "GN-NEXT", Type: "w:gp2"}},
	})
}

func TestFailNextGroupQueryFailsTheNextGroupCallOnly(t *testing.T) {
	t.Parallel()

	_, control, ctx, client, _, _ := linked(t)
	if _, err := control.FailNextGroupQuery(ctx, &fakev1.FailNextGroupQueryRequest{SessionId: "s1", Code: 429}); err != nil {
		t.Fatalf("FailNextGroupQuery: %v", err)
	}

	var iqErr *whatsmeow.IQError
	if _, err := client.GetJoinedGroups(ctx); !errors.As(err, &iqErr) || iqErr.Code != 429 {
		t.Fatalf("the first call = %v, want 429", err)
	}

	if _, err := client.GetJoinedGroups(ctx); err != nil {
		t.Fatalf("the next call = %v", err)
	}
}
