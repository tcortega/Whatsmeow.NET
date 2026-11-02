//go:build fakewhatsapp

package fakewhatsapp

import (
	"context"
	"errors"
	"fmt"

	"go.mau.fi/whatsmeow/types"
	"go.mau.fi/whatsmeow/types/events"

	fakev1 "github.com/tcortega/Whatsmeow.NET/sidecar/gen/whatsmeow/fake/v1"
)

// AddGroup puts a group on WhatsApp's side.
func (c *Control) AddGroup(_ context.Context, req *fakev1.AddGroupRequest) (*fakev1.AddGroupResponse, error) {
	jid, err := types.ParseJID(req.GetJid())
	if err != nil || jid.Server != types.GroupServer {
		return nil, fmt.Errorf("add group: jid %q is not a group JID", req.GetJid())
	}

	g := &group{
		jid: jid, name: req.GetName(), topic: req.GetTopic(), announce: req.GetAnnounce(), locked: req.GetLocked(), lid: req.GetLidAddressed(),
	}

	if req.GetTopic() != "" {
		g.topicID = "TOPIC0"
	}

	if req.GetCreateTime() != nil {
		g.created = req.GetCreateTime().AsTime()
	}

	if req.GetOwner() != "" {
		if g.owner, err = types.ParseJID(req.GetOwner()); err != nil {
			return nil, fmt.Errorf("add group: owner %q: %w", req.GetOwner(), err)
		}
	}

	for _, m := range req.GetMembers() {
		id, err := types.ParseJID(m.GetJid())
		if err != nil {
			return nil, fmt.Errorf("add group: member %q: %w", m.GetJid(), err)
		}

		g.members = append(g.members, member{id: id.ToNonAD(), admin: m.GetType() == "admin", super: m.GetType() == "superadmin"})
	}

	c.whatsApp.mu.Lock()
	c.whatsApp.groups[jid] = g
	c.whatsApp.mu.Unlock()

	return &fakev1.AddGroupResponse{}, nil
}

// DeliverGroupNotification dispatches a change notification on the client's node queue, acked only when every handler
// succeeded (notification.go:493-506).
func (c *Control) DeliverGroupNotification(_ context.Context, req *fakev1.DeliverGroupNotificationRequest) (*fakev1.DeliverGroupNotificationResponse, error) {
	client, err := c.whatsApp.client(req.GetSessionId())
	if err != nil {
		return nil, err
	}

	jids := func(field string, values []string) ([]types.JID, error) {
		var all []types.JID

		for _, value := range values {
			jid, err := types.ParseJID(value)
			if err != nil {
				return nil, fmt.Errorf("deliver a group notification: %s %q: %w", field, value, err)
			}

			all = append(all, jid)
		}

		return all, nil
	}

	group, err := jids("group", []string{req.GetGroup()})
	if err != nil {
		return nil, err
	}

	evt := &events.GroupInfo{JID: group[0], Timestamp: req.GetTime().AsTime()}

	if req.GetSender() != "" {
		sender, err := jids("sender", []string{req.GetSender()})
		if err != nil {
			return nil, err
		}

		evt.Sender = &sender[0]
	}

	if req.GetSenderPn() != "" {
		pn, err := jids("sender_pn", []string{req.GetSenderPn()})
		if err != nil {
			return nil, err
		}

		evt.SenderPN = &pn[0]
	}

	if req.Name != nil {
		evt.Name = &types.GroupName{Name: req.GetName(), NameSetAt: evt.Timestamp}
	}

	if req.Topic != nil {
		evt.Topic = &types.GroupTopic{Topic: req.GetTopic(), TopicID: "TOPIC-NOTIFIED", TopicSetAt: evt.Timestamp, TopicDeleted: req.GetTopic() == ""}
	}

	if req.Announce != nil {
		evt.Announce = &types.GroupAnnounce{IsAnnounce: req.GetAnnounce()}
	}

	if req.Locked != nil {
		evt.Locked = &types.GroupLocked{IsLocked: req.GetLocked()}
	}

	if req.GetEphemeral() {
		evt.Ephemeral = &types.GroupEphemeral{IsEphemeral: true, DisappearingTimer: 86400}
	}

	if req.GetMembershipApproval() {
		evt.MembershipApprovalMode = &types.GroupMembershipApprovalMode{IsJoinApprovalRequired: true}
	}

	for _, list := range []struct {
		field  string
		values []string
		to     *[]types.JID
	}{
		{"join", req.GetJoin(), &evt.Join},
		{"leave", req.GetLeave(), &evt.Leave},
		{"promote", req.GetPromote(), &evt.Promote},
		{"demote", req.GetDemote(), &evt.Demote},
	} {
		if *list.to, err = jids(list.field, list.values); err != nil {
			return nil, err
		}
	}

	c.whatsApp.notify(client, req.GetId(), evt)

	return &fakev1.DeliverGroupNotificationResponse{}, nil
}

// RefuseGroupAdd makes WhatsApp refuse to add a user to any group.
func (c *Control) RefuseGroupAdd(_ context.Context, req *fakev1.RefuseGroupAddRequest) (*fakev1.RefuseGroupAddResponse, error) {
	jid, err := types.ParseJID(req.GetJid())
	if err != nil || jid.Server != types.DefaultUserServer || req.GetCode() == 0 {
		return nil, fmt.Errorf("refuse group add: jid %q is not a phone-number JID, or code is 0", req.GetJid())
	}

	c.whatsApp.mu.Lock()
	c.whatsApp.refusals[jid] = refusal{code: int(req.GetCode()), invite: req.GetInvite()}
	c.whatsApp.mu.Unlock()

	return &fakev1.RefuseGroupAddResponse{}, nil
}

// FailNextGroupQuery scripts the session's next group call to fail with an IQ error of the code.
func (c *Control) FailNextGroupQuery(_ context.Context, req *fakev1.FailNextGroupQueryRequest) (*fakev1.FailNextGroupQueryResponse, error) {
	if req.GetCode() == 0 {
		return nil, errors.New("fail the next group query: code is 0")
	}

	c.whatsApp.setScript(req.GetSessionId(), func(s *script) { s.failGroupQuery = req.GetCode() })

	return &fakev1.FailNextGroupQueryResponse{}, nil
}
