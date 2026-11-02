// Package groups is the Groups slice: listing, reading, creating and managing the session's groups, their
// participants, settings and invite links, and the group notifications (group.proto, ADR 0030).
//
// JIDs are other people's personal data, a group's included: an older group's JID carries its creator's phone number.
// Nothing here logs one, and no error this package returns carries one, whatsmeow's text included (classify,
// ARCHITECTURE §7.8).
package groups

import (
	"cmp"
	"context"
	"errors"
	"fmt"
	"slices"

	"go.mau.fi/whatsmeow"
	waBinary "go.mau.fi/whatsmeow/binary"
	"go.mau.fi/whatsmeow/types"
	"google.golang.org/protobuf/types/known/timestamppb"

	whatsmeowv1 "github.com/tcortega/Whatsmeow.NET/sidecar/gen/whatsmeow/v1"
	"github.com/tcortega/Whatsmeow.NET/sidecar/internal/server"
	"github.com/tcortega/Whatsmeow.NET/sidecar/internal/session"
)

// rateOverLimit is the code of whatsmeow's ErrIQRateOverLimit (errors.go:197). Its Is also compares the text
// (errors.go:229-238), so the code alone is matched.
const rateOverLimit = 429

// Client is what the slice needs of a whatsmeow client. *whatsmeow.Client and the fake WhatsApp's client both
// satisfy it (ADR 0019).
type Client interface {
	GetJoinedGroups(ctx context.Context) ([]*types.GroupInfo, error)
	GetGroupInfo(ctx context.Context, jid types.JID) (*types.GroupInfo, error)
	CreateGroup(ctx context.Context, req whatsmeow.ReqCreateGroup) (*types.GroupInfo, error)
	UpdateGroupParticipants(ctx context.Context, jid types.JID, participantChanges []types.JID, action whatsmeow.ParticipantChange) ([]types.GroupParticipant, error)
	SetGroupName(ctx context.Context, jid types.JID, name string) error
	SetGroupTopic(ctx context.Context, jid types.JID, previousID, newID, topic string) error
	SetGroupAnnounce(ctx context.Context, jid types.JID, announce bool) error
	SetGroupLocked(ctx context.Context, jid types.JID, locked bool) error
	GetGroupInviteLink(ctx context.Context, jid types.JID, reset bool) (string, error)
	JoinGroupWithLink(ctx context.Context, code string) (types.JID, error)
	LeaveGroup(ctx context.Context, jid types.JID) error
}

var _ Client = (*whatsmeow.Client)(nil)

// Sessions is what the slice needs of the Sessions slice: a session's client while it is online.
type Sessions interface {
	Online(id string) (session.Client, <-chan struct{}, error)
}

// Service implements whatsmeowv1.GroupServiceServer.
type Service struct {
	whatsmeowv1.UnimplementedGroupServiceServer

	sessions Sessions
}

// New returns the slice over sessions.
func New(sessions Sessions) *Service { return &Service{sessions: sessions} }

// client checks the session id and returns the session's client for a call to WhatsApp; a session that is not online
// fails with its reason.
func (svc *Service) client(id string) (Client, error) {
	if err := server.CheckID("session_id", id); err != nil {
		return nil, fmt.Errorf("session id: %w", err)
	}

	c, _, err := svc.sessions.Online(id)
	if err != nil {
		return nil, fmt.Errorf("groups: %w", err)
	}

	groups, ok := c.(Client)
	if !ok {
		return nil, fmt.Errorf("session %q: its client %T has no group calls", id, c)
	}

	return groups, nil
}

// checkSession fails a malformed session id before any other argument is looked at.
func checkSession(id string) error {
	if err := server.CheckID("session_id", id); err != nil {
		return fmt.Errorf("session id: %w", err)
	}

	return nil
}

// ListGroups lists the session's groups, ordered by JID (group.proto).
func (svc *Service) ListGroups(ctx context.Context, req *whatsmeowv1.ListGroupsRequest) (*whatsmeowv1.ListGroupsResponse, error) {
	client, err := svc.client(req.GetSessionId())
	if err != nil {
		return nil, err
	}

	infos, err := client.GetJoinedGroups(ctx)
	if err != nil {
		return nil, classify("list the groups", err, false)
	}

	resp := &whatsmeowv1.ListGroupsResponse{Groups: make([]*whatsmeowv1.Group, 0, len(infos))}
	for _, info := range infos {
		resp.Groups = append(resp.Groups, toGroup(info))
	}

	slices.SortFunc(resp.Groups, func(a, b *whatsmeowv1.Group) int { return cmp.Compare(a.GetJid(), b.GetJid()) })

	return resp, nil
}

// GetGroup reads one group (group.proto).
func (svc *Service) GetGroup(ctx context.Context, req *whatsmeowv1.GetGroupRequest) (*whatsmeowv1.GetGroupResponse, error) {
	client, jid, err := svc.clientFor(req.GetSessionId(), req.GetGroup())
	if err != nil {
		return nil, err
	}

	info, err := client.GetGroupInfo(ctx, jid)
	if err != nil {
		return nil, classify("get the group", err, false)
	}

	return &whatsmeowv1.GetGroupResponse{Group: toGroup(info)}, nil
}

// CreateGroup creates a group (group.proto).
func (svc *Service) CreateGroup(ctx context.Context, req *whatsmeowv1.CreateGroupRequest) (*whatsmeowv1.CreateGroupResponse, error) {
	if err := checkSession(req.GetSessionId()); err != nil {
		return nil, err
	}

	if req.GetName() == "" {
		return nil, fmt.Errorf("create a group: %w", server.InvalidArgument("name", "empty"))
	}

	participants, err := users(req.GetParticipants())
	if err != nil {
		return nil, err
	}

	client, err := svc.client(req.GetSessionId())
	if err != nil {
		return nil, err
	}

	info, err := client.CreateGroup(ctx, whatsmeow.ReqCreateGroup{Name: req.GetName(), Participants: participants})
	if err != nil {
		return nil, classify("create a group", err, true)
	}

	// The answer lists the session too; the results are for the participants asked for, by either form.
	requested := func(p types.GroupParticipant) bool {
		return slices.ContainsFunc(participants, func(j types.JID) bool {
			return j == p.JID.ToNonAD() || j == p.PhoneNumber.ToNonAD() || j == p.LID.ToNonAD()
		})
	}

	resp := &whatsmeowv1.CreateGroupResponse{Group: toGroup(info), Participants: []*whatsmeowv1.ParticipantResult{}}
	for _, p := range info.Participants {
		if requested(p) {
			resp.Participants = append(resp.Participants, toResult(p))
		}
	}

	return resp, nil
}

// actions are the contract's participant actions as whatsmeow's.
var actions = map[whatsmeowv1.ParticipantAction]whatsmeow.ParticipantChange{ //nolint:gochecknoglobals // a read-only table
	whatsmeowv1.ParticipantAction_PARTICIPANT_ACTION_ADD:     whatsmeow.ParticipantChangeAdd,
	whatsmeowv1.ParticipantAction_PARTICIPANT_ACTION_REMOVE:  whatsmeow.ParticipantChangeRemove,
	whatsmeowv1.ParticipantAction_PARTICIPANT_ACTION_PROMOTE: whatsmeow.ParticipantChangePromote,
	whatsmeowv1.ParticipantAction_PARTICIPANT_ACTION_DEMOTE:  whatsmeow.ParticipantChangeDemote,
}

// UpdateParticipants adds, removes, promotes or demotes participants, with a result for each (group.proto).
func (svc *Service) UpdateParticipants(ctx context.Context, req *whatsmeowv1.UpdateParticipantsRequest) (*whatsmeowv1.UpdateParticipantsResponse, error) {
	if err := checkSession(req.GetSessionId()); err != nil {
		return nil, err
	}

	jid, err := groupJID(req.GetGroup())
	if err != nil {
		return nil, err
	}

	participants, err := users(req.GetParticipants())
	if err != nil {
		return nil, err
	}

	action, ok := actions[req.GetAction()]
	if !ok {
		return nil, fmt.Errorf("update participants: %w", server.InvalidArgument("action", "add, remove, promote or demote"))
	}

	resp := &whatsmeowv1.UpdateParticipantsResponse{Participants: []*whatsmeowv1.ParticipantResult{}}
	if len(participants) == 0 {
		return resp, nil
	}

	client, err := svc.client(req.GetSessionId())
	if err != nil {
		return nil, err
	}

	results, err := client.UpdateGroupParticipants(ctx, jid, participants, action)
	if err != nil {
		return nil, classify("update participants", err, true)
	}

	for _, p := range results {
		resp.Participants = append(resp.Participants, toResult(p))
	}

	return resp, nil
}

// SetGroupName renames a group (group.proto).
func (svc *Service) SetGroupName(ctx context.Context, req *whatsmeowv1.SetGroupNameRequest) (*whatsmeowv1.SetGroupNameResponse, error) {
	if err := checkSession(req.GetSessionId()); err != nil {
		return nil, err
	}

	jid, err := groupJID(req.GetGroup())
	if err != nil {
		return nil, err
	}

	if req.GetName() == "" {
		return nil, fmt.Errorf("rename a group: %w", server.InvalidArgument("name", "empty"))
	}

	client, err := svc.client(req.GetSessionId())
	if err != nil {
		return nil, err
	}

	if err := client.SetGroupName(ctx, jid, req.GetName()); err != nil {
		return nil, classify("rename a group", err, true)
	}

	return &whatsmeowv1.SetGroupNameResponse{}, nil
}

// SetGroupTopic sets or removes a group's topic (group.proto).
func (svc *Service) SetGroupTopic(ctx context.Context, req *whatsmeowv1.SetGroupTopicRequest) (*whatsmeowv1.SetGroupTopicResponse, error) {
	client, jid, err := svc.clientFor(req.GetSessionId(), req.GetGroup())
	if err != nil {
		return nil, err
	}

	// The topic it replaces: whatsmeow reads it itself when none is given, but wraps that read's error with %v, which
	// keeps no type to classify (group.go:370-376). A group without a topic has no id to pass, so whatsmeow still reads
	// once more, and that read's failure is INTERNAL (group.proto, SetGroupTopic).
	info, err := client.GetGroupInfo(ctx, jid)
	if err != nil {
		return nil, classify("read the group for its topic", err, false)
	}

	if err := client.SetGroupTopic(ctx, jid, info.TopicID, "", req.GetTopic()); err != nil {
		return nil, classify("set a group's topic", err, true)
	}

	return &whatsmeowv1.SetGroupTopicResponse{}, nil
}

// SetGroupAnnounce sets who can send messages (group.proto).
func (svc *Service) SetGroupAnnounce(ctx context.Context, req *whatsmeowv1.SetGroupAnnounceRequest) (*whatsmeowv1.SetGroupAnnounceResponse, error) {
	client, jid, err := svc.clientFor(req.GetSessionId(), req.GetGroup())
	if err != nil {
		return nil, err
	}

	if err := client.SetGroupAnnounce(ctx, jid, req.GetAnnounce()); err != nil {
		return nil, classify("set a group's announce setting", err, true)
	}

	return &whatsmeowv1.SetGroupAnnounceResponse{}, nil
}

// SetGroupLocked sets who can change the group's info (group.proto).
func (svc *Service) SetGroupLocked(ctx context.Context, req *whatsmeowv1.SetGroupLockedRequest) (*whatsmeowv1.SetGroupLockedResponse, error) {
	client, jid, err := svc.clientFor(req.GetSessionId(), req.GetGroup())
	if err != nil {
		return nil, err
	}

	if err := client.SetGroupLocked(ctx, jid, req.GetLocked()); err != nil {
		return nil, classify("set a group's locked setting", err, true)
	}

	return &whatsmeowv1.SetGroupLockedResponse{}, nil
}

// GetInviteLink reads a group's invite link (group.proto).
func (svc *Service) GetInviteLink(ctx context.Context, req *whatsmeowv1.GetInviteLinkRequest) (*whatsmeowv1.GetInviteLinkResponse, error) {
	client, jid, err := svc.clientFor(req.GetSessionId(), req.GetGroup())
	if err != nil {
		return nil, err
	}

	link, err := client.GetGroupInviteLink(ctx, jid, false)
	if err != nil {
		return nil, classify("get a group's invite link", err, false)
	}

	return &whatsmeowv1.GetInviteLinkResponse{Link: link}, nil
}

// ResetInviteLink replaces a group's invite link (group.proto).
func (svc *Service) ResetInviteLink(ctx context.Context, req *whatsmeowv1.ResetInviteLinkRequest) (*whatsmeowv1.ResetInviteLinkResponse, error) {
	client, jid, err := svc.clientFor(req.GetSessionId(), req.GetGroup())
	if err != nil {
		return nil, err
	}

	link, err := client.GetGroupInviteLink(ctx, jid, true)
	if err != nil {
		return nil, classify("reset a group's invite link", err, true)
	}

	return &whatsmeowv1.ResetInviteLinkResponse{Link: link}, nil
}

// JoinGroupWithLink joins the group of an invite link (group.proto).
func (svc *Service) JoinGroupWithLink(ctx context.Context, req *whatsmeowv1.JoinGroupWithLinkRequest) (*whatsmeowv1.JoinGroupWithLinkResponse, error) {
	if err := checkSession(req.GetSessionId()); err != nil {
		return nil, err
	}

	if req.GetLink() == "" {
		return nil, fmt.Errorf("join a group: %w", server.InvalidArgument("link", "empty"))
	}

	client, err := svc.client(req.GetSessionId())
	if err != nil {
		return nil, err
	}

	// whatsmeow takes the link or its code (stripURLPrefix, group.go:512-520). For a group that approves new members it
	// answers the same JID while the session only asked to join (group.go:526-529).
	jid, err := client.JoinGroupWithLink(ctx, req.GetLink())
	if err != nil {
		return nil, classify("join a group", err, true)
	}

	// An answer whose node has no jid gives the empty JID (binary/attrs.go:66-69, group.go:526-533): the session may
	// have joined.
	if jid.User == "" || jid.Server != types.GroupServer {
		return nil, fmt.Errorf("join a group: %w: WhatsApp's answer names no group", server.ErrOutcomeUnknown)
	}

	return &whatsmeowv1.JoinGroupWithLinkResponse{Group: text(jid)}, nil
}

// LeaveGroup leaves a group (group.proto).
func (svc *Service) LeaveGroup(ctx context.Context, req *whatsmeowv1.LeaveGroupRequest) (*whatsmeowv1.LeaveGroupResponse, error) {
	client, jid, err := svc.clientFor(req.GetSessionId(), req.GetGroup())
	if err != nil {
		return nil, err
	}

	if err := client.LeaveGroup(ctx, jid); err != nil {
		return nil, classify("leave a group", err, true)
	}

	return &whatsmeowv1.LeaveGroupResponse{}, nil
}

// clientFor checks the session id and the group, then returns the session's client.
func (svc *Service) clientFor(sessionID, group string) (Client, types.JID, error) {
	if err := checkSession(sessionID); err != nil {
		return nil, types.EmptyJID, err
	}

	jid, err := groupJID(group)
	if err != nil {
		return nil, types.EmptyJID, err
	}

	client, err := svc.client(sessionID)

	return client, jid, err
}

// groupJID parses the request field "group": a group has no agent or device.
func groupJID(value string) (types.JID, error) {
	jid, err := types.ParseJID(value)
	if err != nil || jid.User == "" || jid.Server != types.GroupServer || jid.RawAgent != 0 || jid.Device != 0 {
		return types.EmptyJID, fmt.Errorf("groups: %w", server.InvalidArgument("group", "not a group JID"))
	}

	return jid, nil
}

// users parses the request field "participants": users by phone number or LID, without their devices.
func users(values []string) ([]types.JID, error) {
	jids := make([]types.JID, len(values))

	for i, value := range values {
		jid, err := types.ParseJID(value)
		if err != nil || jid.User == "" || (jid.Server != types.DefaultUserServer && jid.Server != types.HiddenUserServer) {
			return nil, fmt.Errorf("groups: %w", server.InvalidArgument("participants", "entry %d is not a user JID (phone number or LID)", i))
		}

		jids[i] = jid.ToNonAD()
	}

	return jids, nil
}

// toGroup maps a group as whatsmeow parses it (group.go:742-811). A participant WhatsApp answered with an error is
// not in the group.
func toGroup(info *types.GroupInfo) *whatsmeowv1.Group {
	group := &whatsmeowv1.Group{
		Jid: text(info.JID), Name: info.Name, Topic: info.Topic, Owner: text(info.OwnerJID), Announce: info.IsAnnounce, Locked: info.IsLocked,
		Participants: make([]*whatsmeowv1.GroupParticipant, 0, len(info.Participants)),
	}

	// creator_pn is sent beside a LID creator (group.go:748).
	if info.OwnerJID.Server == types.HiddenUserServer {
		group.OwnerAlt = text(info.OwnerPN)
	}

	if !info.GroupCreated.IsZero() {
		group.CreateTime = timestamppb.New(info.GroupCreated)
	}

	for _, p := range info.Participants {
		if p.Error != 0 {
			continue
		}

		role := whatsmeowv1.GroupRole_GROUP_ROLE_MEMBER

		switch {
		case p.IsSuperAdmin:
			role = whatsmeowv1.GroupRole_GROUP_ROLE_SUPER_ADMIN
		case p.IsAdmin:
			role = whatsmeowv1.GroupRole_GROUP_ROLE_ADMIN
		}

		group.Participants = append(group.Participants, &whatsmeowv1.GroupParticipant{Jid: text(p.JID), JidAlt: alt(p), Role: role})
	}

	return group
}

// toResult maps what WhatsApp answered for one participant (group.go:713-740).
func toResult(p types.GroupParticipant) *whatsmeowv1.ParticipantResult {
	result := &whatsmeowv1.ParticipantResult{Jid: text(p.JID), JidAlt: alt(p), ErrorCode: int32(p.Error)} //nolint:gosec // WhatsApp's codes are HTTP-like, far below 2^31

	if p.Error != 0 && p.AddRequest != nil {
		result.Invite = &whatsmeowv1.AddInvite{Code: p.AddRequest.Code, ExpireTime: timestamppb.New(p.AddRequest.Expiration)}
	}

	return result
}

// alt is a participant's other address form: whatsmeow sets both beside the JID, one of them equal to it
// (group.go:722-728).
func alt(p types.GroupParticipant) string {
	switch p.JID.Server {
	case types.HiddenUserServer:
		return text(p.PhoneNumber)
	case types.DefaultUserServer:
		return text(p.LID)
	default:
		return ""
	}
}

// text is a JID's string form, empty for the empty JID.
func text(jid types.JID) string {
	if jid.IsEmpty() {
		return ""
	}

	return jid.String()
}

// classify gives what a group call returned its reason (request.go:152-182, errors.go). A write that went out and got
// no answer, or one whatsmeow could not read, may have taken effect: OUTCOME_UNKNOWN; a read that got none did nothing: WHATSAPP_TIMED_OUT, or
// NOT_CONNECTED when the socket dropped. A context error stays one.
//
// The error keeps whatsmeow's sentinel or a bare copy of its typed error, never the text whatsmeow wraps around it, and
// for any other error only its Go type: whatsmeow names participants with %v, which keeps no type to strip
// (group.go:71, 84, 221, 229), and an IQ error's node names the group (errors.go:216-226). The status message reaches
// the application, and an INTERNAL one the error log (server.toStatus).
func classify(op string, err error, write bool) error {
	var (
		iqErr        *whatsmeow.IQError
		disconnected *whatsmeow.DisconnectedError
		missing      *whatsmeow.ElementMissingError
		unparsed     waBinary.ErrorList
	)

	switch {
	case errors.Is(err, context.Canceled):
		return fmt.Errorf("%s: %w", op, context.Canceled)
	case errors.Is(err, context.DeadlineExceeded):
		return fmt.Errorf("%s: %w", op, context.DeadlineExceeded)
	case errors.Is(err, whatsmeow.ErrInviteLinkInvalid):
		return fmt.Errorf("%s: %w: %w", op, server.ErrInviteLinkInvalid, whatsmeow.ErrInviteLinkInvalid)
	case errors.Is(err, whatsmeow.ErrInviteLinkRevoked):
		return fmt.Errorf("%s: %w: %w", op, server.ErrInviteLinkInvalid, whatsmeow.ErrInviteLinkRevoked)
	case errors.As(err, &iqErr):
		// The code and WhatsApp's error keyword, without the nodes.
		bare := &whatsmeow.IQError{Code: iqErr.Code, Text: iqErr.Text}
		if iqErr.Code == rateOverLimit {
			return fmt.Errorf("%s: %w: %w", op, server.ErrRateLimited, bare)
		}

		return fmt.Errorf("%s: %w: %w", op, server.WithMetadata(server.ErrWhatsAppError, "code", fmt.Sprint(iqErr.Code)), bare)
	case errors.Is(err, whatsmeow.ErrNotConnected):
		// Never sent (request.go:133-141).
		return fmt.Errorf("%s: %w: %w", op, server.ErrNotConnected, whatsmeow.ErrNotConnected)
	case errors.Is(err, whatsmeow.ErrIQTimedOut) && write:
		return fmt.Errorf("%s: %w: %w", op, server.ErrOutcomeUnknown, whatsmeow.ErrIQTimedOut)
	case errors.Is(err, whatsmeow.ErrIQTimedOut):
		return fmt.Errorf("%s: %w: %w", op, server.ErrWhatsAppTimedOut, whatsmeow.ErrIQTimedOut)
	case errors.As(err, &disconnected):
		bare := &whatsmeow.DisconnectedError{Action: disconnected.Action}
		if write {
			return fmt.Errorf("%s: %w: %w", op, server.ErrOutcomeUnknown, bare)
		}

		return fmt.Errorf("%s: %w: %w", op, server.ErrNotConnected, bare)
	case write && (errors.As(err, &missing) || errors.As(err, &unparsed)):
		// WhatsApp answered the write without what whatsmeow reads from the answer (group.go:151-155, 245-248,
		// 530-533, 809): it may have taken effect.
		return fmt.Errorf("%s: %w: whatsmeow could not read WhatsApp's answer (a %T)", op, server.ErrOutcomeUnknown, err)
	default:
		return fmt.Errorf("%s: whatsmeow failed with a %T", op, err) //nolint:err113 // only the type: its text may name a JID
	}
}
