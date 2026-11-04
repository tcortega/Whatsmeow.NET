//go:build fakewhatsapp

package fakewhatsapp

import (
	"cmp"
	"context"
	"errors"
	"fmt"
	"slices"
	"strings"
	"time"

	"go.mau.fi/whatsmeow"
	waBinary "go.mau.fi/whatsmeow/binary"
	"go.mau.fi/whatsmeow/types"
	"go.mau.fi/whatsmeow/types/events"

	fakev1 "github.com/tcortega/Whatsmeow.NET/sidecar/gen/whatsmeow/fake/v1"
)

// member is one member of a group on WhatsApp's side, by phone number (or by LID for a user the directory lacks).
type member struct {
	id           types.JID
	admin, super bool
}

// group is one group on WhatsApp's side.
type group struct {
	jid                   types.JID
	name, topic, topicID  string
	owner                 types.JID
	created               time.Time
	announce, locked, lid bool
	members               []member
	// code is the current invite code; empty until the first request for the link.
	code string
	// addMode is who can add participants: the mode a create asked for, none for a group AddGroup put there until one
	// is set; only admins add unless it is all_member_add [inferred].
	addMode types.GroupMemberAddMode
	// timer is the disappearing messages timer in seconds; zero is off.
	timer uint32
	// removed is who an admin removed and has not been added back: WhatsApp refuses them the group's info from its link
	// with 401 [observed: #36 live run 2026-11-04, ARCHITECTURE §14]. Whether it refuses them a join too is [unknown].
	removed []types.JID
}

// refusal is how WhatsApp refuses to add one user (RefuseGroupAdd).
type refusal struct {
	code   int
	invite bool
}

// groupState is WhatsApp's side of the group calls, shared by every session: the groups, the invite codes that were reset,
// the users it refuses to add, and a counter for new ids. w.mu guards it.
type groupState struct {
	groups   map[types.JID]*group
	revoked  map[string]bool
	refusals map[types.JID]refusal
	serial   int
}

// notice is one notification to deliver to a client once w.mu is released, with its stanza id.
type notice struct {
	client *Client
	id     string
	evt    any
}

// groupQuery is WhatsApp answering one group call of the client: the IQ error FailNextGroupQuery scripted, or nil.
func (c *Client) groupQuery() error {
	var code int32

	if c.whatsApp.take(c.sessionID, func(s *script) bool { code = s.failGroupQuery; return code != 0 }, func(s *script) { s.failGroupQuery = 0 }) {
		return &whatsmeow.IQError{Code: int(code), Text: "fake WhatsApp: FailNextGroupQuery"}
	}

	return nil
}

// self is the session's user, by phone number.
func (c *Client) self() types.JID {
	if c.Store == nil || c.Store.ID == nil {
		return types.EmptyJID
	}

	return c.Store.ID.ToNonAD()
}

// identity is a user as a member: by phone number when the directory knows the LID's. w.mu is held.
func (w *WhatsApp) identity(jid types.JID) types.JID {
	jid = jid.ToNonAD()
	if u, ok := w.users[jid]; ok && jid.Server == types.HiddenUserServer {
		return u.pn
	}

	return jid
}

// participant is a member as whatsmeow parses WhatsApp's answer (group.go:713-740): by LID in a LID-addressed group
// when the directory has one, with the phone number beside it, else by phone number, with the LID beside it when known.
// w.mu is held.
func (w *WhatsApp) participant(g *group, m member) types.GroupParticipant {
	p := types.GroupParticipant{IsAdmin: m.admin || m.super, IsSuperAdmin: m.super}

	var lid types.JID
	if u, ok := w.users[m.id]; ok {
		lid = u.lid
	}

	switch {
	case m.id.Server == types.HiddenUserServer:
		p.JID, p.LID = m.id, m.id
	case g.lid && !lid.IsEmpty():
		p.JID, p.LID, p.PhoneNumber = lid, lid, m.id
	default:
		p.JID, p.PhoneNumber, p.LID = m.id, m.id, lid
	}

	return p
}

// info is the group as whatsmeow parses WhatsApp's answer (group.go:742-811). w.mu is held.
func (w *WhatsApp) info(g *group) *types.GroupInfo {
	info := &types.GroupInfo{
		JID: g.jid, GroupName: types.GroupName{Name: g.name}, GroupTopic: types.GroupTopic{Topic: g.topic, TopicID: g.topicID},
		GroupLocked: types.GroupLocked{IsLocked: g.locked}, GroupAnnounce: types.GroupAnnounce{IsAnnounce: g.announce},
		GroupCreated: g.created, AddressingMode: types.AddressingModePN, MemberAddMode: g.addMode,
		GroupEphemeral: types.GroupEphemeral{IsEphemeral: g.timer != 0, DisappearingTimer: g.timer},
	}

	if g.lid {
		info.AddressingMode = types.AddressingModeLID
	}

	if !g.owner.IsEmpty() {
		owner := w.participant(g, member{id: g.owner})
		info.OwnerJID = owner.JID
		if owner.JID.Server == types.HiddenUserServer {
			info.OwnerPN = owner.PhoneNumber
		}
	}

	for _, m := range g.members {
		info.Participants = append(info.Participants, w.participant(g, m))
	}

	info.ParticipantCount = len(info.Participants)

	return info
}

// find is the group of a call by self: 404 for an unknown one, 403 for one self is not in (group.go:674-678), and the
// member index. w.mu is held.
func (w *WhatsApp) find(jid, self types.JID) (*group, int, error) {
	g, ok := w.groups[jid]
	if !ok {
		return nil, -1, fmt.Errorf("%w: %w", whatsmeow.ErrGroupNotFound, whatsmeow.ErrIQNotFound)
	}

	i := slices.IndexFunc(g.members, func(m member) bool { return m.id == self })
	if i < 0 {
		return nil, -1, fmt.Errorf("%w: %w", whatsmeow.ErrNotInGroup, whatsmeow.ErrIQForbidden)
	}

	return g, i, nil
}

// admin fails a member's call to change the participants with 403 [observed: #36 live run 2026-11-04, a member
// promoting itself], and is the check for every other call only an admin may make.
func admin(g *group, i int) error {
	if !g.members[i].admin && !g.members[i].super {
		return whatsmeow.ErrIQForbidden
	}

	return nil
}

// change is a change notification of g by self, as whatsmeow parses it (group.go:886-1014). w.mu is held.
func (w *WhatsApp) change(g *group, self types.JID) *events.GroupInfo {
	sender := w.participant(g, member{id: self})
	evt := &events.GroupInfo{JID: g.jid, Sender: &sender.JID, Timestamp: now()}

	if sender.JID.Server == types.HiddenUserServer && !sender.PhoneNumber.IsEmpty() {
		evt.SenderPN = &sender.PhoneNumber
	}

	return evt
}

// joined is the create notification of g that a member added by self, or joining, gets (group.go:860-884). w.mu is
// held.
func (w *WhatsApp) joined(g *group, self types.JID, reason string) *events.JoinedGroup {
	sender := w.participant(g, member{id: self})
	evt := &events.JoinedGroup{Reason: reason, Sender: &sender.JID, GroupInfo: *w.info(g)}

	if sender.JID.Server == types.HiddenUserServer && !sender.PhoneNumber.IsEmpty() {
		evt.SenderPN = &sender.PhoneNumber
	}

	return evt
}

// noticesTo is evt for the clients of the members in to, in session order. w.mu is held.
func (w *WhatsApp) noticesTo(to []types.JID, evt any) []notice {
	var notices []notice

	ids := make([]string, 0, len(w.clients))
	for id := range w.clients {
		ids = append(ids, id)
	}

	slices.Sort(ids)

	for _, id := range ids {
		if c := w.clients[id]; slices.Contains(to, c.self()) && !c.self().IsEmpty() {
			w.serial++
			notices = append(notices, notice{client: c, id: fmt.Sprintf("GN%d", w.serial), evt: evt})
		}
	}

	return notices
}

// ids is the members' ids. w.mu is held.
func ids(g *group) []types.JID {
	all := make([]types.JID, len(g.members))
	for i, m := range g.members {
		all[i] = m.id
	}

	return all
}

// deliver dispatches each notification on its client's node queue, in order, once w.mu is released.
func (w *WhatsApp) deliver(notices []notice) {
	for _, n := range notices {
		w.notify(n.client, n.id, n.evt)
	}
}

// notify dispatches a w:gp2 notification's event to c on its node queue and records whatsmeow's ack, which goes out
// only when every handler succeeded (notification.go:493-506, maybeDeferredAck receipt.go:115-128), with the stanza's
// class, id and type (sendAck, receipt.go:151-181).
func (w *WhatsApp) notify(c *Client, id string, evt any) {
	if c.dispatch(evt) {
		return
	}

	w.append(&fakev1.Sent{SessionId: c.sessionID, Kind: &fakev1.Sent_Ack{Ack: &fakev1.Ack{Class: "notification", Id: id, Type: "w:gp2"}}})
}

// now is WhatsApp's time on the group calls: fixed, so a test knows every time it answers and notifies.
func now() time.Time { return time.Date(2026, 11, 1, 12, 0, 0, 0, time.UTC) }

// GetJoinedGroups answers the groups the session is in, by JID.
func (c *Client) GetJoinedGroups(context.Context) ([]*types.GroupInfo, error) {
	if err := c.groupQuery(); err != nil {
		return nil, err
	}

	w := c.whatsApp
	w.mu.Lock()
	defer w.mu.Unlock()

	var infos []*types.GroupInfo

	for _, g := range w.groups {
		if slices.ContainsFunc(g.members, func(m member) bool { return m.id == c.self() }) {
			infos = append(infos, w.info(g))
		}
	}

	slices.SortFunc(infos, func(a, b *types.GroupInfo) int { return cmp.Compare(a.JID.String(), b.JID.String()) })

	return infos, nil
}

// GetGroupInfo answers one group the session is in.
func (c *Client) GetGroupInfo(_ context.Context, jid types.JID) (*types.GroupInfo, error) {
	if err := c.groupQuery(); err != nil {
		return nil, err
	}

	w := c.whatsApp
	w.mu.Lock()
	defer w.mu.Unlock()

	g, _, err := w.find(jid, c.self())
	if err != nil {
		return nil, err
	}

	return w.info(g), nil
}

// CreateGroup creates a group with the session as its super admin and adds each participant WhatsApp does not refuse;
// the answer lists a refused one with its error [inferred]. The group has the member add mode the create asked for,
// all_member_add unless told otherwise, as whatsmeow asks (group.go:59-63); what WhatsApp answers is [unknown]. Each added session gets a create notification; the
// creator gets none [inferred].
func (c *Client) CreateGroup(_ context.Context, req whatsmeow.ReqCreateGroup) (*types.GroupInfo, error) {
	if err := c.groupQuery(); err != nil {
		return nil, err
	}

	w := c.whatsApp
	w.mu.Lock()

	w.serial++
	self := c.self()
	g := &group{
		jid: types.NewJID(fmt.Sprintf("1203639%011d", w.serial), types.GroupServer), name: req.Name, owner: self, created: now(),
		members: []member{{id: self, super: true}}, addMode: cmp.Or(req.MemberAddMode, types.GroupMemberAddModeAllMember),
	}
	w.groups[g.jid] = g

	var refused []types.GroupParticipant

	for _, p := range req.Participants {
		id := w.identity(p)
		if r, ok := w.refusals[id]; ok {
			refused = append(refused, w.refused(g, id, r))

			continue
		}

		g.members = append(g.members, member{id: id})
	}

	info := w.info(g)
	info.Participants = append(info.Participants, refused...)
	notices := w.noticesTo(ids(g)[1:], w.joined(g, self, ""))
	w.mu.Unlock()

	w.deliver(notices)

	return info, nil
}

// refused is a participant WhatsApp would not add, with its code and, when it offers one, an invite (group.go:729-739).
// w.mu is held.
func (w *WhatsApp) refused(g *group, id types.JID, r refusal) types.GroupParticipant {
	p := w.participant(g, member{id: id})
	p.Error = r.code

	if r.invite {
		p.AddRequest = &types.GroupParticipantAddRequest{Code: "INVITE-" + id.User, Expiration: now().Add(72 * time.Hour)}
	}

	return p
}

// UpdateGroupParticipants is an admin's change to participants, or any member's add when the group's member add mode
// is all_member_add, answered for each one [inferred: 409 for adding a member, 404 for a non-member otherwise]. Every member, and each one removed, is notified of the changes made; a
// session added gets a create notification instead.
func (c *Client) UpdateGroupParticipants(_ context.Context, jid types.JID, changes []types.JID, action whatsmeow.ParticipantChange) ([]types.GroupParticipant, error) {
	if err := c.groupQuery(); err != nil {
		return nil, err
	}

	w := c.whatsApp
	w.mu.Lock()

	self := c.self()

	g, i, err := w.find(jid, self)
	if err == nil && (action != whatsmeow.ParticipantChangeAdd || g.addMode != types.GroupMemberAddModeAllMember) {
		err = admin(g, i)
	}

	if err != nil {
		w.mu.Unlock()

		return nil, err
	}

	results := make([]types.GroupParticipant, 0, len(changes))
	before := ids(g)

	var done, added []types.JID

	for _, p := range changes {
		id := w.identity(p)
		at := slices.IndexFunc(g.members, func(m member) bool { return m.id == id })
		result := w.participant(g, member{id: id})
		refuse, refused := w.refusals[id]

		switch {
		case action == whatsmeow.ParticipantChangeAdd && at >= 0:
			result.Error = 409
		case action == whatsmeow.ParticipantChangeAdd && refused:
			result = w.refused(g, id, refuse)
		case action == whatsmeow.ParticipantChangeAdd:
			g.members = append(g.members, member{id: id})
			g.removed = slices.DeleteFunc(g.removed, func(r types.JID) bool { return r == id })
			added = append(added, id)
		case at < 0:
			result.Error = 404
		case action == whatsmeow.ParticipantChangeRemove:
			g.members = slices.Delete(g.members, at, at+1)
			g.removed = append(g.removed, id)
		default:
			g.members[at].admin = action == whatsmeow.ParticipantChangePromote
		}

		if result.Error == 0 {
			done = append(done, result.JID)
		}

		results = append(results, result)
	}

	var notices []notice

	if len(done) > 0 {
		evt := w.change(g, self)

		switch action {
		case whatsmeow.ParticipantChangeAdd:
			evt.Join = done
		case whatsmeow.ParticipantChangeRemove:
			evt.Leave = done
		case whatsmeow.ParticipantChangePromote:
			evt.Promote = done
		case whatsmeow.ParticipantChangeDemote:
			evt.Demote = done
		}

		notices = w.noticesTo(slices.DeleteFunc(before, func(id types.JID) bool { return slices.Contains(added, id) }), evt)
		notices = append(notices, w.noticesTo(added, w.joined(g, self, ""))...)
	}

	w.mu.Unlock()

	w.deliver(notices)

	return results, nil
}

// changeInfo applies a member's change to g's info, after its permission check, and notifies the members.
func (c *Client) changeInfo(jid types.JID, adminOnly bool, apply func(*group, *events.GroupInfo)) error {
	if err := c.groupQuery(); err != nil {
		return err
	}

	w := c.whatsApp
	w.mu.Lock()

	self := c.self()

	// A member's change of the group's info is 401, not the participants' 403 [observed: #36 live run 2026-11-04, the
	// name, the member add mode and the timer of a locked group; the topic, announce and locked [inferred] the same].
	g, i, err := w.find(jid, self)
	if err == nil && (adminOnly || g.locked) && admin(g, i) != nil {
		err = whatsmeow.ErrIQNotAuthorized
	}

	if err != nil {
		w.mu.Unlock()

		return err
	}

	evt := w.change(g, self)
	apply(g, evt)
	notices := w.noticesTo(ids(g), evt)
	w.mu.Unlock()

	w.deliver(notices)

	return nil
}

// SetGroupName renames the group: any member of an unlocked group may, only an admin of a locked one.
func (c *Client) SetGroupName(_ context.Context, jid types.JID, name string) error {
	return c.changeInfo(jid, false, func(g *group, evt *events.GroupInfo) {
		g.name = name
		evt.Name = &types.GroupName{Name: name, NameSetAt: evt.Timestamp, NameSetBy: *evt.Sender}
	})
}

// SetGroupTopic sets or, empty, removes the topic, as SetGroupName may. previousID is taken as given: the second read
// whatsmeow makes for a group with no topic yet (group.go:370-376) is not modelled; the slice's tests cover its
// failure.
func (c *Client) SetGroupTopic(_ context.Context, jid types.JID, _, newID, topic string) error {
	return c.changeInfo(jid, false, func(g *group, evt *events.GroupInfo) {
		c.whatsApp.serial++
		g.topic, g.topicID = topic, cmp.Or(newID, fmt.Sprintf("TOPIC%d", c.whatsApp.serial))
		evt.Topic = &types.GroupTopic{Topic: topic, TopicID: g.topicID, TopicSetAt: evt.Timestamp, TopicSetBy: *evt.Sender, TopicDeleted: topic == ""}
	})
}

// SetGroupAnnounce sets who can send messages; only an admin may.
func (c *Client) SetGroupAnnounce(_ context.Context, jid types.JID, announce bool) error {
	return c.changeInfo(jid, true, func(g *group, evt *events.GroupInfo) {
		g.announce = announce
		evt.Announce = &types.GroupAnnounce{IsAnnounce: announce}
	})
}

// SetGroupLocked sets who can change the group's info; only an admin may.
func (c *Client) SetGroupLocked(_ context.Context, jid types.JID, locked bool) error {
	return c.changeInfo(jid, true, func(g *group, evt *events.GroupInfo) {
		g.locked = locked
		evt.Locked = &types.GroupLocked{IsLocked: locked}
	})
}

// SetGroupMemberAddMode sets who can add participants; only an admin may. The members' notification carries a
// member_add_mode child, which whatsmeow leaves in UnknownChanges [inferred: named and filled as the IQ,
// group.go:1084-1097].
func (c *Client) SetGroupMemberAddMode(_ context.Context, jid types.JID, mode types.GroupMemberAddMode) error {
	if mode != types.GroupMemberAddModeAdmin && mode != types.GroupMemberAddModeAllMember {
		return errors.New("invalid mode, must be 'admin_add' or 'all_member_add'") //nolint:err113 // whatsmeow's own error text (group.go:1086-1088)
	}

	return c.changeInfo(jid, true, func(g *group, evt *events.GroupInfo) {
		g.addMode = mode
		evt.UnknownChanges = []*waBinary.Node{{Tag: "member_add_mode", Content: []byte(mode)}}
	})
}

// SetDisappearingTimer sets a group's disappearing messages timer, as SetGroupName may; a timer the phones do not offer
// is 400, as whatsmeow names it (send.go:687-690). A direct chat's timer is a message the sidecar sends itself, so the
// fake has none.
func (c *Client) SetDisappearingTimer(_ context.Context, chat types.JID, timer time.Duration, _ time.Time) error {
	if chat.Server != types.GroupServer {
		return errors.New("fake WhatsApp: a direct chat's timer is a message; the sidecar sends it") //nolint:err113 // a misuse of the fake
	}

	offered := []time.Duration{whatsmeow.DisappearingTimerOff, whatsmeow.DisappearingTimer24Hours, whatsmeow.DisappearingTimer7Days, whatsmeow.DisappearingTimer90Days}
	if !slices.Contains(offered, timer) {
		if err := c.groupQuery(); err != nil {
			return err
		}

		return fmt.Errorf("%w: %w", whatsmeow.ErrInvalidDisappearingTimer, whatsmeow.ErrIQBadRequest)
	}

	return c.changeInfo(chat, false, func(g *group, evt *events.GroupInfo) {
		g.timer = uint32(timer.Seconds())
		evt.Ephemeral = &types.GroupEphemeral{IsEphemeral: g.timer != 0, DisappearingTimer: g.timer}
	})
}

// GetGroupInviteLink answers the group's link, as an admin may [inferred: 401 otherwise, whatsmeow's
// ErrGroupInviteLinkUnauthorized, group.go:432]; reset revokes the current code first.
func (c *Client) GetGroupInviteLink(_ context.Context, jid types.JID, reset bool) (string, error) {
	if err := c.groupQuery(); err != nil {
		return "", err
	}

	w := c.whatsApp
	w.mu.Lock()
	defer w.mu.Unlock()

	g, i, err := w.find(jid, c.self())
	if err != nil {
		return "", err
	}

	if admin(g, i) != nil {
		return "", fmt.Errorf("%w: %w", whatsmeow.ErrGroupInviteLinkUnauthorized, whatsmeow.ErrIQNotAuthorized)
	}

	if reset && g.code != "" {
		w.revoked[g.code] = true
		g.code = ""
	}

	if g.code == "" {
		w.serial++
		g.code = fmt.Sprintf("FAKECODE%d", w.serial)
	}

	return whatsmeow.InviteLinkPrefix + g.code, nil
}

// JoinGroupWithLink joins the group whose link has the code: a reset code is 410 and an unknown one 406, as whatsmeow
// names them (group.go:516-519). The session gets a create notification with reason invite, every other member a
// change; a member joins nothing again [inferred].
func (c *Client) JoinGroupWithLink(_ context.Context, code string) (types.JID, error) {
	if err := c.groupQuery(); err != nil {
		return types.EmptyJID, err
	}

	w := c.whatsApp
	w.mu.Lock()

	g, err := w.byCode(code)
	if err != nil {
		w.mu.Unlock()

		return types.EmptyJID, err
	}

	self := c.self()

	var notices []notice

	if !slices.ContainsFunc(g.members, func(m member) bool { return m.id == self }) {
		others := ids(g)
		g.members = append(g.members, member{id: self})
		g.removed = slices.DeleteFunc(g.removed, func(r types.JID) bool { return r == self })

		evt := w.change(g, self)
		evt.Join, evt.JoinReason = []types.JID{*evt.Sender}, "invite"
		notices = append(w.noticesTo(others, evt), w.noticesTo([]types.JID{self}, w.joined(g, self, "invite"))...)
	}

	w.mu.Unlock()

	w.deliver(notices)

	return g.jid, nil
}

// GetGroupInfoFromLink answers the group of a link's code, with every member [inferred], and joins nothing; its codes
// are the join's (group.go:497-502), and one an admin removed is refused with 401 (group.removed).
func (c *Client) GetGroupInfoFromLink(_ context.Context, code string) (*types.GroupInfo, error) {
	if err := c.groupQuery(); err != nil {
		return nil, err
	}

	w := c.whatsApp
	w.mu.Lock()
	defer w.mu.Unlock()

	g, err := w.byCode(code)
	if err != nil {
		return nil, err
	}

	if slices.Contains(g.removed, c.self()) {
		return nil, whatsmeow.ErrIQNotAuthorized
	}

	return w.info(g), nil
}

// byCode is the group whose invite link has the code, taken from a link as whatsmeow strips it (group.go:494, 516): a
// reset code is 410 and an unknown one 406. w.mu is held.
func (w *WhatsApp) byCode(code string) (*group, error) {
	code = strings.TrimPrefix(code, whatsmeow.InviteLinkPrefix)

	if w.revoked[code] {
		return nil, fmt.Errorf("%w: %w", whatsmeow.ErrInviteLinkRevoked, whatsmeow.ErrIQGone)
	}

	for _, g := range w.groups {
		if g.code != "" && g.code == code {
			return g, nil
		}
	}

	return nil, fmt.Errorf("%w: %w", whatsmeow.ErrInviteLinkInvalid, whatsmeow.ErrIQNotAcceptable)
}

// LeaveGroup leaves the group; every member, the session included, is notified [inferred].
func (c *Client) LeaveGroup(_ context.Context, jid types.JID) error {
	if err := c.groupQuery(); err != nil {
		return err
	}

	w := c.whatsApp
	w.mu.Lock()

	self := c.self()

	g, i, err := w.find(jid, self)
	if err != nil {
		w.mu.Unlock()

		return err
	}

	evt := w.change(g, self)
	evt.Leave = []types.JID{*evt.Sender}
	notices := w.noticesTo(ids(g), evt)
	g.members = slices.Delete(g.members, i, i+1)
	w.mu.Unlock()

	w.deliver(notices)

	return nil
}
