package groups

import (
	"bytes"
	"context"
	"errors"
	"fmt"
	"net"
	"strings"
	"sync"
	"testing"
	"time"

	"github.com/rs/zerolog"
	"go.mau.fi/whatsmeow"
	waBinary "go.mau.fi/whatsmeow/binary"
	"go.mau.fi/whatsmeow/types"
	"go.mau.fi/whatsmeow/types/events"
	"google.golang.org/genproto/googleapis/rpc/errdetails"
	"google.golang.org/grpc"
	"google.golang.org/grpc/codes"
	"google.golang.org/grpc/credentials/insecure"
	"google.golang.org/grpc/metadata"
	"google.golang.org/grpc/status"
	"google.golang.org/grpc/test/bufconn"
	"google.golang.org/protobuf/proto"
	"google.golang.org/protobuf/types/known/durationpb"
	"google.golang.org/protobuf/types/known/timestamppb"

	whatsmeowv1 "github.com/tcortega/Whatsmeow.NET/sidecar/gen/whatsmeow/v1"
	sidecarlog "github.com/tcortega/Whatsmeow.NET/sidecar/internal/log"
	"github.com/tcortega/Whatsmeow.NET/sidecar/internal/server"
	"github.com/tcortega/Whatsmeow.NET/sidecar/internal/session"
)

const token = "00112233445566778899aabbccddeeff00112233445566778899aabbccddeeff"

// group is a group in the tests: its JID carries digits no other JID has, so a test can tell it from a user's.
const group = "120363777000000001@g.us"

// stub stands in for whatsmeow behind the slice's interface; the session methods it never needs stay nil.
type stub struct {
	session.Client

	mu sync.Mutex
	// err is what every call returns; infoErr, when set, what GetGroupInfo returns instead; topicReadErr, when set,
	// what SetGroupTopic's own read of the group returns.
	err, infoErr, topicReadErr error
	calls                      []string
	groups                     []*types.GroupInfo
	info                       *types.GroupInfo
	created                    *types.GroupInfo
	createReq                  whatsmeow.ReqCreateGroup
	results                    []types.GroupParticipant
	link                       string
	joined                     types.JID
	// pictureID is what SetGroupPhoto answers a set; photo is the last photo it got.
	pictureID string
	photo     []byte
}

func (s *stub) call(format string, args ...any) {
	s.calls = append(s.calls, fmt.Sprintf(format, args...))
}

func (s *stub) GetJoinedGroups(context.Context) ([]*types.GroupInfo, error) {
	s.mu.Lock()
	defer s.mu.Unlock()

	s.call("list")

	return s.groups, s.err
}

func (s *stub) GetGroupInfo(_ context.Context, jid types.JID) (*types.GroupInfo, error) {
	s.mu.Lock()
	defer s.mu.Unlock()

	s.call("info %s", jid)

	if s.infoErr != nil {
		return nil, s.infoErr
	}

	return s.info, s.err
}

func (s *stub) CreateGroup(_ context.Context, req whatsmeow.ReqCreateGroup) (*types.GroupInfo, error) {
	s.mu.Lock()
	defer s.mu.Unlock()

	s.call("create %s %v", req.Name, req.Participants)
	s.createReq = req

	return s.created, s.err
}

func (s *stub) UpdateGroupParticipants(_ context.Context, jid types.JID, participants []types.JID, action whatsmeow.ParticipantChange) ([]types.GroupParticipant, error) {
	s.mu.Lock()
	defer s.mu.Unlock()

	s.call("%s %s %v", action, jid, participants)

	return s.results, s.err
}

func (s *stub) SetGroupName(_ context.Context, jid types.JID, name string) error {
	s.mu.Lock()
	defer s.mu.Unlock()

	s.call("name %s %s", jid, name)

	return s.err
}

func (s *stub) SetGroupTopic(_ context.Context, jid types.JID, previousID, newID, topic string) error {
	s.mu.Lock()
	defer s.mu.Unlock()

	// As whatsmeow does, without a previous id it reads the group once more and wraps that read's error with %v
	// (group.go:370-376).
	if previousID == "" {
		s.call("topic's own read %s", jid)

		if s.topicReadErr != nil {
			return fmt.Errorf("failed to get old group info to update topic: %v", s.topicReadErr) //nolint:errorlint // as whatsmeow wraps it
		}
	}

	s.call("topic %s prev=%s new=%s %q", jid, previousID, newID, topic)

	return s.err
}

func (s *stub) SetGroupAnnounce(_ context.Context, jid types.JID, announce bool) error {
	s.mu.Lock()
	defer s.mu.Unlock()

	s.call("announce %s %v", jid, announce)

	return s.err
}

func (s *stub) SetGroupLocked(_ context.Context, jid types.JID, locked bool) error {
	s.mu.Lock()
	defer s.mu.Unlock()

	s.call("locked %s %v", jid, locked)

	return s.err
}

func (s *stub) GetGroupInviteLink(_ context.Context, jid types.JID, reset bool) (string, error) {
	s.mu.Lock()
	defer s.mu.Unlock()

	s.call("invite %s reset=%v", jid, reset)

	return s.link, s.err
}

func (s *stub) JoinGroupWithLink(_ context.Context, code string) (types.JID, error) {
	s.mu.Lock()
	defer s.mu.Unlock()

	s.call("join %s", code)

	return s.joined, s.err
}

func (s *stub) SetGroupMemberAddMode(_ context.Context, jid types.JID, mode types.GroupMemberAddMode) error {
	s.mu.Lock()
	defer s.mu.Unlock()

	s.call("member add mode %s %s", jid, mode)

	return s.err
}

func (s *stub) SetDisappearingTimer(_ context.Context, chat types.JID, timer time.Duration, settingTS time.Time) error {
	s.mu.Lock()
	defer s.mu.Unlock()

	s.call("timer %s %v at %v", chat, timer, settingTS.IsZero())

	return s.err
}

func (s *stub) GetGroupInfoFromLink(_ context.Context, code string) (*types.GroupInfo, error) {
	s.mu.Lock()
	defer s.mu.Unlock()

	s.call("link info %s", code)

	return s.info, s.err
}

// SetGroupPhoto answers a removal "remove", as whatsmeow does (group.go:345-347).
func (s *stub) SetGroupPhoto(_ context.Context, jid types.JID, avatar []byte) (string, error) {
	s.mu.Lock()
	defer s.mu.Unlock()

	s.call("photo %s of %d bytes, removal %v", jid, len(avatar), avatar == nil)
	s.photo = avatar

	switch {
	case s.err != nil:
		return "", s.err
	case avatar == nil:
		return "remove", nil
	default:
		return s.pictureID, nil
	}
}

func (s *stub) LeaveGroup(_ context.Context, jid types.JID) error {
	s.mu.Lock()
	defer s.mu.Unlock()

	s.call("leave %s", jid)

	return s.err
}

// sessions stands in for the Sessions slice: one session, "s1", online with the stub unless offline is set.
type sessions struct {
	client  session.Client
	offline error
}

func (s *sessions) Online(id string) (session.Client, <-chan struct{}, error) {
	if id != "s1" {
		return nil, nil, fmt.Errorf("session %q: %w", id, server.ErrSessionNotFound)
	}

	if s.offline != nil {
		return nil, nil, s.offline
	}

	return s.client, make(chan struct{}), nil
}

type harness struct {
	stub     *stub
	sessions *sessions
	client   whatsmeowv1.GroupServiceClient
	conn     *grpc.ClientConn
	ctx      context.Context //nolint:containedctx // the test's call context
}

func start(t *testing.T) *harness {
	t.Helper()

	return startLogging(t, zerolog.Nop())
}

// startLogging is start with the server logging to log.
func startLogging(t *testing.T, log zerolog.Logger) *harness {
	t.Helper()

	h := &harness{stub: &stub{}}
	h.sessions = &sessions{client: h.stub}

	srv := server.New(token, log, sidecarlog.NewRedactor())
	whatsmeowv1.RegisterGroupServiceServer(srv, New(h.sessions))

	lis := bufconn.Listen(1 << 20)
	served := make(chan error, 1)

	go func() { served <- srv.Serve(lis) }()

	conn, err := grpc.NewClient("passthrough:///bufnet",
		grpc.WithContextDialer(func(ctx context.Context, _ string) (net.Conn, error) { return lis.DialContext(ctx) }),
		grpc.WithTransportCredentials(insecure.NewCredentials()))
	if err != nil {
		t.Fatalf("dial: %v", err)
	}

	t.Cleanup(func() {
		_ = conn.Close()
		srv.Shutdown()
		srv.Stop()

		if err := <-served; err != nil {
			t.Errorf("serve: %v", err)
		}
	})

	ctx, cancel := context.WithTimeout(context.Background(), 30*time.Second)
	t.Cleanup(cancel)

	h.ctx = metadata.AppendToOutgoingContext(ctx, "authorization", "Bearer "+token)
	h.client = whatsmeowv1.NewGroupServiceClient(conn)
	h.conn = conn

	return h
}

func jid(s string) types.JID {
	j, err := types.ParseJID(s)
	if err != nil {
		panic(err)
	}

	return j
}

func ptr(j types.JID) *types.JID { return &j }

// wantReason fails unless err is a status of code with one ErrorInfo of reason and metadata.
func wantReason(t *testing.T, err error, code codes.Code, reason string, metadata map[string]string) {
	t.Helper()

	st, _ := status.FromError(err)
	if st.Code() != code || len(st.Details()) != 1 {
		t.Fatalf("error = %v, want %v with one ErrorInfo", err, code)
	}

	info, _ := st.Details()[0].(*errdetails.ErrorInfo)
	if info.GetReason() != reason || fmt.Sprint(info.GetMetadata()) != fmt.Sprint(metadata) {
		t.Fatalf("ErrorInfo = %v, want %s with %v", info, reason, metadata)
	}
}

func wantProto(t *testing.T, got, want proto.Message) {
	t.Helper()

	if !proto.Equal(got, want) {
		t.Fatalf("got %v, want %v", got, want)
	}
}

func wantCalls(t *testing.T, s *stub, want ...string) {
	t.Helper()

	if fmt.Sprint(s.calls) != fmt.Sprint(want) {
		t.Fatalf("asked WhatsApp %q, want %q", s.calls, want)
	}
}

// createdAt is when the test groups were created.
func createdAt() time.Time { return time.Date(2026, 10, 1, 12, 0, 0, 0, time.UTC) }

// lidGroup is a LID-addressed group as whatsmeow parses it (group.go:713-811): each participant's JID is the LID, with
// the phone number beside it when WhatsApp sent it.
func lidGroup() *types.GroupInfo {
	return &types.GroupInfo{
		JID: jid(group), OwnerJID: jid("777000001@lid"), OwnerPN: jid("5511777000001@s.whatsapp.net"),
		GroupName:    types.GroupName{Name: "Team"},
		GroupTopic:   types.GroupTopic{Topic: "Plans", TopicID: "T1"},
		GroupLocked:  types.GroupLocked{IsLocked: true},
		GroupCreated: createdAt(), AddressingMode: types.AddressingModeLID,
		Participants: []types.GroupParticipant{
			{JID: jid("777000001@lid"), LID: jid("777000001@lid"), PhoneNumber: jid("5511777000001@s.whatsapp.net"), IsAdmin: true, IsSuperAdmin: true},
			{JID: jid("777000002@lid"), LID: jid("777000002@lid"), PhoneNumber: jid("5511777000002@s.whatsapp.net"), IsAdmin: true},
			{JID: jid("777000003@lid"), LID: jid("777000003@lid")},
		},
	}
}

func lidGroupProto() *whatsmeowv1.Group {
	return &whatsmeowv1.Group{
		Jid: group, Name: "Team", Topic: "Plans", Owner: "777000001@lid", OwnerAlt: "5511777000001@s.whatsapp.net",
		CreateTime: timestamppb.New(createdAt()), Locked: true,
		Participants: []*whatsmeowv1.GroupParticipant{
			{Jid: "777000001@lid", JidAlt: "5511777000001@s.whatsapp.net", Role: whatsmeowv1.GroupRole_GROUP_ROLE_SUPER_ADMIN},
			{Jid: "777000002@lid", JidAlt: "5511777000002@s.whatsapp.net", Role: whatsmeowv1.GroupRole_GROUP_ROLE_ADMIN},
			{Jid: "777000003@lid", Role: whatsmeowv1.GroupRole_GROUP_ROLE_MEMBER},
		},
	}
}

func TestListGroupsGivesEachGroupWithItsParticipantsOrderedByJID(t *testing.T) {
	t.Parallel()

	h := start(t)
	// A phone-number-addressed group: each JID is the phone number, with the LID beside it when sent (group.go:725-728).
	pnGroup := &types.GroupInfo{
		JID: jid("120363777000000000@g.us"), OwnerJID: jid("5511777000001@s.whatsapp.net"),
		GroupName:     types.GroupName{Name: "Family"},
		GroupAnnounce: types.GroupAnnounce{IsAnnounce: true},
		Participants: []types.GroupParticipant{
			{JID: jid("5511777000001@s.whatsapp.net"), PhoneNumber: jid("5511777000001@s.whatsapp.net"), LID: jid("777000001@lid"), IsAdmin: true, IsSuperAdmin: true},
			{JID: jid("5511777000004@s.whatsapp.net"), PhoneNumber: jid("5511777000004@s.whatsapp.net")},
		},
	}
	h.stub.groups = []*types.GroupInfo{lidGroup(), pnGroup}

	resp, err := h.client.ListGroups(h.ctx, &whatsmeowv1.ListGroupsRequest{SessionId: "s1"})
	if err != nil {
		t.Fatalf("ListGroups: %v", err)
	}

	wantProto(t, resp, &whatsmeowv1.ListGroupsResponse{Groups: []*whatsmeowv1.Group{
		{
			Jid: "120363777000000000@g.us", Name: "Family", Owner: "5511777000001@s.whatsapp.net", Announce: true,
			Participants: []*whatsmeowv1.GroupParticipant{
				{Jid: "5511777000001@s.whatsapp.net", JidAlt: "777000001@lid", Role: whatsmeowv1.GroupRole_GROUP_ROLE_SUPER_ADMIN},
				{Jid: "5511777000004@s.whatsapp.net", Role: whatsmeowv1.GroupRole_GROUP_ROLE_MEMBER},
			},
		},
		lidGroupProto(),
	}})
}

func TestGetGroupGivesTheGroupWithOwnerCreationAndSettings(t *testing.T) {
	t.Parallel()

	h := start(t)
	h.stub.info = lidGroup()

	resp, err := h.client.GetGroup(h.ctx, &whatsmeowv1.GetGroupRequest{SessionId: "s1", Group: group})
	if err != nil {
		t.Fatalf("GetGroup: %v", err)
	}

	wantProto(t, resp, &whatsmeowv1.GetGroupResponse{Group: lidGroupProto()})
	wantCalls(t, h.stub, "info "+group)
}

func TestCreateGroupGivesTheGroupWithTheAddedAndAResultForEachParticipant(t *testing.T) {
	t.Parallel()

	h := start(t)
	// WhatsApp lists the participants it could not add with their error (group.go:729-739, types/group.go:120-123).
	h.stub.created = &types.GroupInfo{
		JID: jid(group), OwnerJID: jid("5511777000001@s.whatsapp.net"), GroupName: types.GroupName{Name: "Team"}, GroupCreated: createdAt(),
		Participants: []types.GroupParticipant{
			{JID: jid("5511777000001@s.whatsapp.net"), PhoneNumber: jid("5511777000001@s.whatsapp.net"), IsAdmin: true, IsSuperAdmin: true},
			{JID: jid("5511777000002@s.whatsapp.net"), PhoneNumber: jid("5511777000002@s.whatsapp.net"), LID: jid("777000002@lid")},
			{
				JID: jid("5511777000003@s.whatsapp.net"), PhoneNumber: jid("5511777000003@s.whatsapp.net"), Error: 403,
				AddRequest: &types.GroupParticipantAddRequest{Code: "INV3", Expiration: createdAt().Add(72 * time.Hour)},
			},
		},
	}

	resp, err := h.client.CreateGroup(h.ctx, &whatsmeowv1.CreateGroupRequest{
		SessionId: "s1", Name: "Team", Participants: []string{"777000002:4@lid", "5511777000003@s.whatsapp.net"},
	})
	if err != nil {
		t.Fatalf("CreateGroup: %v", err)
	}

	wantProto(t, resp, &whatsmeowv1.CreateGroupResponse{
		Group: &whatsmeowv1.Group{
			Jid: group, Name: "Team", Owner: "5511777000001@s.whatsapp.net", CreateTime: timestamppb.New(createdAt()),
			Participants: []*whatsmeowv1.GroupParticipant{
				{Jid: "5511777000001@s.whatsapp.net", Role: whatsmeowv1.GroupRole_GROUP_ROLE_SUPER_ADMIN},
				{Jid: "5511777000002@s.whatsapp.net", JidAlt: "777000002@lid", Role: whatsmeowv1.GroupRole_GROUP_ROLE_MEMBER},
			},
		},
		// The requested participants only, matched by either form: the creator is not one.
		Participants: []*whatsmeowv1.ParticipantResult{
			{Jid: "5511777000002@s.whatsapp.net", JidAlt: "777000002@lid"},
			{
				Jid: "5511777000003@s.whatsapp.net", ErrorCode: 403,
				Invite: &whatsmeowv1.AddInvite{Code: "INV3", ExpireTime: timestamppb.New(createdAt().Add(72 * time.Hour))},
			},
		},
	})
	// Without their devices.
	wantCalls(t, h.stub, "create Team [777000002@lid 5511777000003@s.whatsapp.net]")
}

func TestUpdateParticipantsAnswersForEachParticipantAndFailsForNone(t *testing.T) {
	t.Parallel()

	for _, tc := range []struct {
		action whatsmeowv1.ParticipantAction
		want   string
	}{
		{whatsmeowv1.ParticipantAction_PARTICIPANT_ACTION_ADD, "add"},
		{whatsmeowv1.ParticipantAction_PARTICIPANT_ACTION_REMOVE, "remove"},
		{whatsmeowv1.ParticipantAction_PARTICIPANT_ACTION_PROMOTE, "promote"},
		{whatsmeowv1.ParticipantAction_PARTICIPANT_ACTION_DEMOTE, "demote"},
	} {
		t.Run(tc.want, func(t *testing.T) {
			t.Parallel()

			h := start(t)
			h.stub.results = []types.GroupParticipant{
				{JID: jid("777000002@lid"), LID: jid("777000002@lid"), PhoneNumber: jid("5511777000002@s.whatsapp.net")},
				{JID: jid("777000003@lid"), LID: jid("777000003@lid"), Error: 409},
			}

			resp, err := h.client.UpdateParticipants(h.ctx, &whatsmeowv1.UpdateParticipantsRequest{
				SessionId: "s1", Group: group, Participants: []string{"777000002:1@lid", "777000003@lid"}, Action: tc.action,
			})
			if err != nil {
				t.Fatalf("UpdateParticipants: %v", err)
			}

			wantProto(t, resp, &whatsmeowv1.UpdateParticipantsResponse{Participants: []*whatsmeowv1.ParticipantResult{
				{Jid: "777000002@lid", JidAlt: "5511777000002@s.whatsapp.net"},
				{Jid: "777000003@lid", ErrorCode: 409},
			}})
			wantCalls(t, h.stub, tc.want+" "+group+" [777000002@lid 777000003@lid]")
		})
	}
}

func TestAnEmptyParticipantListReturnsAnEmptyListWithoutAskingWhatsApp(t *testing.T) {
	t.Parallel()

	h := start(t)
	// Even offline: nothing is asked.
	h.sessions.offline = server.ErrNotConnected

	resp, err := h.client.UpdateParticipants(h.ctx, &whatsmeowv1.UpdateParticipantsRequest{
		SessionId: "s1", Group: group, Action: whatsmeowv1.ParticipantAction_PARTICIPANT_ACTION_ADD,
	})
	if err != nil || len(resp.GetParticipants()) != 0 {
		t.Fatalf("UpdateParticipants = %v, %v; want empty", resp, err)
	}

	wantCalls(t, h.stub)
}

func TestTheSettingsAndTheInviteLinkReachWhatsApp(t *testing.T) {
	t.Parallel()

	h := start(t)
	h.stub.info = lidGroup()
	h.stub.link = "https://chat.whatsapp.com/CODE1"
	h.stub.joined = jid(group)

	for _, call := range []func() error{
		func() error {
			return errOf(h.client.SetGroupName(h.ctx, &whatsmeowv1.SetGroupNameRequest{SessionId: "s1", Group: group, Name: "Crew"}))
		},
		func() error {
			return errOf(h.client.SetGroupTopic(h.ctx, &whatsmeowv1.SetGroupTopicRequest{SessionId: "s1", Group: group, Topic: "New plans"}))
		},
		func() error {
			return errOf(h.client.SetGroupTopic(h.ctx, &whatsmeowv1.SetGroupTopicRequest{SessionId: "s1", Group: group}))
		},
		func() error {
			return errOf(h.client.SetGroupAnnounce(h.ctx, &whatsmeowv1.SetGroupAnnounceRequest{SessionId: "s1", Group: group, Announce: true}))
		},
		func() error {
			return errOf(h.client.SetGroupLocked(h.ctx, &whatsmeowv1.SetGroupLockedRequest{SessionId: "s1", Group: group}))
		},
		func() error {
			resp, err := h.client.GetInviteLink(h.ctx, &whatsmeowv1.GetInviteLinkRequest{SessionId: "s1", Group: group})
			if err == nil && resp.GetLink() != h.stub.link {
				return fmt.Errorf("link %q", resp.GetLink())
			}

			return errOf(resp, err)
		},
		func() error {
			resp, err := h.client.ResetInviteLink(h.ctx, &whatsmeowv1.ResetInviteLinkRequest{SessionId: "s1", Group: group})
			if err == nil && resp.GetLink() != h.stub.link {
				return fmt.Errorf("link %q", resp.GetLink())
			}

			return errOf(resp, err)
		},
		func() error {
			resp, err := h.client.JoinGroupWithLink(h.ctx, &whatsmeowv1.JoinGroupWithLinkRequest{SessionId: "s1", Link: "https://chat.whatsapp.com/CODE1"})
			if err == nil && resp.GetGroup() != group {
				return fmt.Errorf("joined %q", resp.GetGroup())
			}

			return errOf(resp, err)
		},
		func() error {
			return errOf(h.client.LeaveGroup(h.ctx, &whatsmeowv1.LeaveGroupRequest{SessionId: "s1", Group: group}))
		},
	} {
		if err := call(); err != nil {
			t.Fatalf("call %d: %v", len(h.stub.calls), err)
		}
	}

	// The topic replaces the current one: its id comes from the group, read first, so a failed read is classified
	// (SetGroupTopic wraps its own read with %v, group.go:370-376).
	wantCalls(t, h.stub,
		"name "+group+" Crew",
		"info "+group, "topic "+group+` prev=T1 new= "New plans"`,
		"info "+group, "topic "+group+` prev=T1 new= ""`,
		"announce "+group+" true",
		"locked "+group+" false",
		"invite "+group+" reset=false",
		"invite "+group+" reset=true",
		"join https://chat.whatsapp.com/CODE1",
		"leave "+group,
	)
}

func TestAMalformedArgumentIsInvalidArgumentAndAsksNothing(t *testing.T) {
	t.Parallel()

	h := start(t)
	add := whatsmeowv1.ParticipantAction_PARTICIPANT_ACTION_ADD

	for _, tc := range []struct {
		field string
		call  func() error
	}{
		{"group", func() error {
			return errOf(h.client.GetGroup(h.ctx, &whatsmeowv1.GetGroupRequest{SessionId: "s1", Group: "5511777000001@s.whatsapp.net"}))
		}},
		{"group", func() error {
			return errOf(h.client.GetGroup(h.ctx, &whatsmeowv1.GetGroupRequest{SessionId: "s1", Group: "@g.us"}))
		}},
		{"group", func() error {
			return errOf(h.client.GetGroup(h.ctx, &whatsmeowv1.GetGroupRequest{SessionId: "s1", Group: "120363777000000001:3@g.us"}))
		}},
		{"group", func() error {
			return errOf(h.client.LeaveGroup(h.ctx, &whatsmeowv1.LeaveGroupRequest{SessionId: "s1", Group: "120363777000000001.1@g.us"}))
		}},
		{"group", func() error {
			return errOf(h.client.UpdateParticipants(h.ctx, &whatsmeowv1.UpdateParticipantsRequest{SessionId: "s1", Group: "a@b@c", Participants: []string{"777000002@lid"}, Action: add}))
		}},
		{"participants", func() error {
			return errOf(h.client.UpdateParticipants(h.ctx, &whatsmeowv1.UpdateParticipantsRequest{SessionId: "s1", Group: group, Participants: []string{group}, Action: add}))
		}},
		{"action", func() error {
			return errOf(h.client.UpdateParticipants(h.ctx, &whatsmeowv1.UpdateParticipantsRequest{SessionId: "s1", Group: group, Participants: []string{"777000002@lid"}}))
		}},
		{"name", func() error {
			return errOf(h.client.CreateGroup(h.ctx, &whatsmeowv1.CreateGroupRequest{SessionId: "s1"}))
		}},
		{"participants", func() error {
			return errOf(h.client.CreateGroup(h.ctx, &whatsmeowv1.CreateGroupRequest{SessionId: "s1", Name: "Team", Participants: []string{"status@broadcast"}}))
		}},
		{"group", func() error {
			return errOf(h.client.SetGroupName(h.ctx, &whatsmeowv1.SetGroupNameRequest{SessionId: "s1", Group: "", Name: "Crew"}))
		}},
		{"name", func() error {
			return errOf(h.client.SetGroupName(h.ctx, &whatsmeowv1.SetGroupNameRequest{SessionId: "s1", Group: group}))
		}},
		{"group", func() error {
			return errOf(h.client.SetGroupTopic(h.ctx, &whatsmeowv1.SetGroupTopicRequest{SessionId: "s1", Group: "111@lid"}))
		}},
		{"group", func() error {
			return errOf(h.client.SetGroupAnnounce(h.ctx, &whatsmeowv1.SetGroupAnnounceRequest{SessionId: "s1", Group: "111@lid"}))
		}},
		{"group", func() error {
			return errOf(h.client.SetGroupLocked(h.ctx, &whatsmeowv1.SetGroupLockedRequest{SessionId: "s1", Group: "111@lid"}))
		}},
		{"group", func() error {
			return errOf(h.client.GetInviteLink(h.ctx, &whatsmeowv1.GetInviteLinkRequest{SessionId: "s1", Group: "111@lid"}))
		}},
		{"group", func() error {
			return errOf(h.client.ResetInviteLink(h.ctx, &whatsmeowv1.ResetInviteLinkRequest{SessionId: "s1", Group: "111@lid"}))
		}},
		{"link", func() error {
			return errOf(h.client.JoinGroupWithLink(h.ctx, &whatsmeowv1.JoinGroupWithLinkRequest{SessionId: "s1"}))
		}},
		{"group", func() error {
			return errOf(h.client.LeaveGroup(h.ctx, &whatsmeowv1.LeaveGroupRequest{SessionId: "s1", Group: "111@lid"}))
		}},
		{"session_id", func() error {
			return errOf(h.client.ListGroups(h.ctx, &whatsmeowv1.ListGroupsRequest{}))
		}},
	} {
		wantReason(t, tc.call(), codes.InvalidArgument, "INVALID_ARGUMENT", map[string]string{"field": tc.field})
	}

	wantCalls(t, h.stub)
}

func TestWhatsAppsErrorsBecomeTheirReasons(t *testing.T) {
	t.Parallel()

	// whatsmeow names the participant in some of its errors with %v, which keeps no type (group.go:71, 84, 221, 229);
	// an IQ error without a code prints its node (errors.go:217-227).
	node := &waBinary.Node{Tag: "iq", Attrs: waBinary.Attrs{"from": jid(group), "participant": jid("5511777000009@s.whatsapp.net")}}
	disconnected := &whatsmeow.DisconnectedError{Action: "info query", Node: node}

	for _, tc := range []struct {
		name     string
		err      error
		write    bool
		code     codes.Code
		reason   string
		metadata map[string]string
	}{
		{"rate limited", whatsmeow.ErrIQRateOverLimit, false, codes.ResourceExhausted, "RATE_LIMITED", nil},
		{"not an admin", &whatsmeow.IQError{Code: 403, Text: "forbidden", RawNode: node}, true, codes.FailedPrecondition, "WHATSAPP_ERROR", map[string]string{"code": "403"}},
		{"not in the group, as whatsmeow wraps it", fmt.Errorf("%w: %w", whatsmeow.ErrNotInGroup, whatsmeow.ErrIQForbidden), false, codes.FailedPrecondition, "WHATSAPP_ERROR", map[string]string{"code": "403"}},
		{"an IQ error without a code", &whatsmeow.IQError{RawNode: node}, false, codes.FailedPrecondition, "WHATSAPP_ERROR", map[string]string{"code": "0"}},
		// The bare value comes before the IQ was written and after (client.go:976-985, request.go:198-208).
		{"a write not connected", whatsmeow.ErrNotConnected, true, codes.Unknown, "OUTCOME_UNKNOWN", nil},
		{"a read not connected", whatsmeow.ErrNotConnected, false, codes.Unavailable, "NOT_CONNECTED", nil},
		{"a write not connected, wrapped", fmt.Errorf("wrapped: %w", whatsmeow.ErrNotConnected), true, codes.Unavailable, "NOT_CONNECTED", nil},
		{"a read disconnected while waiting", disconnected, false, codes.Unavailable, "NOT_CONNECTED", nil},
		{"a write disconnected while waiting", disconnected, true, codes.Unknown, "OUTCOME_UNKNOWN", nil},
		{"a read that timed out", whatsmeow.ErrIQTimedOut, false, codes.Unavailable, "WHATSAPP_TIMED_OUT", nil},
		{"a write that timed out", whatsmeow.ErrIQTimedOut, true, codes.Unknown, "OUTCOME_UNKNOWN", nil},
		{"a store failure named with %v", namedWithV(), true, codes.Internal, "INTERNAL", nil},
		{"a malformed answer", &whatsmeow.ElementMissingError{Tag: "group", In: "response to group info query"}, false, codes.Internal, "INTERNAL", nil},
		{"cancelled", fmt.Errorf("wrapped: %w", context.Canceled), true, codes.Canceled, "", nil},
	} {
		t.Run(tc.name, func(t *testing.T) {
			t.Parallel()

			var logged bytes.Buffer

			h := startLogging(t, zerolog.New(&logged))
			h.stub.err = tc.err

			var err error
			if tc.write {
				_, err = h.client.UpdateParticipants(h.ctx, &whatsmeowv1.UpdateParticipantsRequest{
					SessionId: "s1", Group: group, Participants: []string{"5511777000009@s.whatsapp.net"}, Action: whatsmeowv1.ParticipantAction_PARTICIPANT_ACTION_ADD,
				})
			} else {
				_, err = h.client.GetGroup(h.ctx, &whatsmeowv1.GetGroupRequest{SessionId: "s1", Group: group})
			}

			if tc.reason == "" {
				if status.Code(err) != tc.code {
					t.Fatalf("error = %v, want %v", err, tc.code)
				}
			} else {
				wantReason(t, err, tc.code, tc.reason, tc.metadata)
			}

			// The message reaches the application, and an INTERNAL one the error log: none of whatsmeow's text around
			// the error, with a JID in it, goes along.
			if msg := status.Convert(err).Message(); strings.Contains(msg, "777000009") || strings.Contains(msg, "777000000001") {
				t.Fatalf("the message %q carries a JID", msg)
			}

			if strings.Contains(logged.String(), "777000009") || strings.Contains(logged.String(), "777000000001") {
				t.Fatalf("the log carries a JID:\n%s", logged.String())
			}
		})
	}
}

func TestAWriteWhoseAnswerLacksItsDataIsOutcomeUnknown(t *testing.T) {
	t.Parallel()

	// The write went out and WhatsApp answered, but not with what whatsmeow reads from the answer: it may have taken
	// effect.
	for _, tc := range []struct {
		name   string
		joined types.JID
		err    error
		call   func(h *harness) error
	}{
		{"a create answer without its group", types.EmptyJID, &whatsmeow.ElementMissingError{Tag: "group", In: "response to create group query"}, func(h *harness) error {
			return errOf(h.client.CreateGroup(h.ctx, &whatsmeowv1.CreateGroupRequest{SessionId: "s1", Name: "Team"}))
		}},
		{"a create answer whose group does not parse", types.EmptyJID, waBinary.ErrorList{errors.New("didn't find required attribute 'id'")}, func(h *harness) error {
			return errOf(h.client.CreateGroup(h.ctx, &whatsmeowv1.CreateGroupRequest{SessionId: "s1", Name: "Team"}))
		}},
		{"a participants answer without its action", types.EmptyJID, &whatsmeow.ElementMissingError{Tag: "add", In: "response to group participants update"}, func(h *harness) error {
			return errOf(h.client.UpdateParticipants(h.ctx, &whatsmeowv1.UpdateParticipantsRequest{
				SessionId: "s1", Group: group, Participants: []string{"777000002@lid"}, Action: whatsmeowv1.ParticipantAction_PARTICIPANT_ACTION_ADD,
			}))
		}},
		{"a join answer without its group", types.EmptyJID, &whatsmeow.ElementMissingError{Tag: "group", In: "response to group link join query"}, func(h *harness) error {
			return errOf(h.client.JoinGroupWithLink(h.ctx, &whatsmeowv1.JoinGroupWithLinkRequest{SessionId: "s1", Link: "CODE"}))
		}},
		// AttrGetter.JID gives the empty JID for a missing jid (binary/attrs.go:66-69, group.go:526-533).
		{"a join answer whose group has no jid", types.EmptyJID, nil, func(h *harness) error {
			return errOf(h.client.JoinGroupWithLink(h.ctx, &whatsmeowv1.JoinGroupWithLinkRequest{SessionId: "s1", Link: "CODE"}))
		}},
		{"a join answer whose jid is not a group", jid("5511777000001@s.whatsapp.net"), nil, func(h *harness) error {
			return errOf(h.client.JoinGroupWithLink(h.ctx, &whatsmeowv1.JoinGroupWithLinkRequest{SessionId: "s1", Link: "CODE"}))
		}},
	} {
		t.Run(tc.name, func(t *testing.T) {
			t.Parallel()

			h := start(t)
			h.stub.err, h.stub.joined = tc.err, tc.joined

			if tc.err != nil {
				// Wrapped once: the message names whatsmeow's type, not the wrapper's.
				h.stub.err = fmt.Errorf("group: %w", tc.err)
			}

			err := tc.call(h)
			wantReason(t, err, codes.Unknown, "OUTCOME_UNKNOWN", nil)

			if msg := status.Convert(err).Message(); tc.err != nil && !strings.Contains(msg, fmt.Sprintf("(a %T)", tc.err)) {
				t.Fatalf("the message %q does not name a %T", msg, tc.err)
			}
		})
	}
}

func TestAnInviteLinkWhatsAppDoesNotAcceptIsInviteLinkInvalid(t *testing.T) {
	t.Parallel()

	for _, tc := range []struct {
		name     string
		err      error
		code     codes.Code
		reason   string
		metadata map[string]string
		message  string
	}{
		// As whatsmeow wraps them (group.go:516-519).
		{"not acceptable, as whatsmeow names a 406", fmt.Errorf("%w: %w", whatsmeow.ErrInviteLinkInvalid, whatsmeow.ErrIQNotAcceptable), codes.NotFound, "INVITE_LINK_INVALID", nil, ""},
		{"reset", fmt.Errorf("%w: %w", whatsmeow.ErrInviteLinkRevoked, whatsmeow.ErrIQGone), codes.NotFound, "INVITE_LINK_INVALID", nil, ""},
		// An unknown or malformed code, which WhatsApp refuses with 400 and whatsmeow passes on as it came [observed: #36
		// and #120 live runs 2026-11-04]; the status message keeps the 400.
		{"unknown, as WhatsApp answers it", &whatsmeow.IQError{Code: 400, Text: "bad-request"}, codes.NotFound, "INVITE_LINK_INVALID", nil, "status 400: bad-request"},
		// Only a 400 is the link's: any other code stays WhatsApp's own.
		{"any other code WhatsApp answers", whatsmeow.ErrIQForbidden, codes.FailedPrecondition, "WHATSAPP_ERROR", map[string]string{"code": "403"}, ""},
	} {
		t.Run(tc.name, func(t *testing.T) {
			t.Parallel()

			h := start(t)
			h.stub.err = tc.err

			_, err := h.client.JoinGroupWithLink(h.ctx, &whatsmeowv1.JoinGroupWithLinkRequest{SessionId: "s1", Link: "https://chat.whatsapp.com/OLD"})
			wantReason(t, err, tc.code, tc.reason, tc.metadata)

			if msg := status.Convert(err).Message(); !strings.Contains(msg, tc.message) {
				t.Fatalf("status message = %q, want it to carry %q", msg, tc.message)
			}
		})
	}
}

func TestTheTopicsReadFirstIsARead(t *testing.T) {
	t.Parallel()

	h := start(t)
	h.stub.infoErr = whatsmeow.ErrIQTimedOut

	_, err := h.client.SetGroupTopic(h.ctx, &whatsmeowv1.SetGroupTopicRequest{SessionId: "s1", Group: group, Topic: "New plans"})
	wantReason(t, err, codes.Unavailable, "WHATSAPP_TIMED_OUT", nil)
	// Nothing was changed.
	wantCalls(t, h.stub, "info "+group)
}

func TestAFirstTopicCostsWhatsmeowASecondReadWhoseFailureIsInternal(t *testing.T) {
	t.Parallel()

	for _, tc := range []struct {
		name string
		err  error
	}{
		{"a timeout", whatsmeow.ErrIQTimedOut},
		{"a refusal", &whatsmeow.IQError{Code: 403, Text: "forbidden"}},
		{"a cancel", context.Canceled},
	} {
		t.Run(tc.name, func(t *testing.T) {
			t.Parallel()

			h := start(t)
			// A group with no topic has no topic id to pass.
			info := lidGroup()
			info.GroupTopic = types.GroupTopic{}
			h.stub.info = info
			h.stub.topicReadErr = tc.err

			_, err := h.client.SetGroupTopic(h.ctx, &whatsmeowv1.SetGroupTopicRequest{SessionId: "s1", Group: group, Topic: "First plans"})

			// whatsmeow's %v keeps no type to classify (group.go:370-376); nothing was changed.
			wantReason(t, err, codes.Internal, "INTERNAL", nil)
			wantCalls(t, h.stub, "info "+group, "topic's own read "+group)
		})
	}
}

func TestEveryCallNeedsTheSessionOnline(t *testing.T) {
	t.Parallel()

	h := start(t)
	h.sessions.offline = fmt.Errorf("session %q: %w", "s1", server.ErrNotConnected)

	_, err := h.client.ListGroups(h.ctx, &whatsmeowv1.ListGroupsRequest{SessionId: "s1"})
	wantReason(t, err, codes.Unavailable, "NOT_CONNECTED", nil)

	_, err = h.client.LeaveGroup(h.ctx, &whatsmeowv1.LeaveGroupRequest{SessionId: "s1", Group: group})
	wantReason(t, err, codes.Unavailable, "NOT_CONNECTED", nil)

	_, err = h.client.ListGroups(h.ctx, &whatsmeowv1.ListGroupsRequest{SessionId: "s2"})
	wantReason(t, err, codes.NotFound, "SESSION_NOT_FOUND", nil)

	wantCalls(t, h.stub)
}

// bare has the session methods only, like a client without group calls.
type bare struct{ session.Client }

func TestAClientWithoutGroupCallsIsInternal(t *testing.T) {
	t.Parallel()

	h := start(t)
	h.sessions.client = bare{}

	_, err := h.client.ListGroups(h.ctx, &whatsmeowv1.ListGroupsRequest{SessionId: "s1"})
	wantReason(t, err, codes.Internal, "INTERNAL", nil)
}

func TestEachGroupNotificationMapsToOneGroupEventAndOthersToNone(t *testing.T) {
	t.Parallel()

	at := time.Date(2026, 11, 2, 10, 0, 0, 0, time.UTC)
	changed := func(changes ...*whatsmeowv1.GroupChange) *whatsmeowv1.StreamEventsResponse {
		return &whatsmeowv1.StreamEventsResponse{Payload: &whatsmeowv1.StreamEventsResponse_Group{Group: &whatsmeowv1.GroupEvent{
			Group: group, Sender: "777000001@lid", SenderAlt: "5511777000001@s.whatsapp.net", Time: timestamppb.New(at),
			Kind: &whatsmeowv1.GroupEvent_Changed_{Changed: &whatsmeowv1.GroupEvent_Changed{Changes: changes}},
		}}}
	}
	participants := func(jids ...string) *whatsmeowv1.GroupChange_Participants {
		return &whatsmeowv1.GroupChange_Participants{Jids: jids}
	}
	for _, tc := range []struct {
		name string
		evt  any
		want *whatsmeowv1.StreamEventsResponse
	}{
		{
			"every change, in the contract's order",
			// whatsmeow sets each from one child node of the notification (group.go:886-1014).
			eventInfo(at, func(e *events.GroupInfo) {
				e.Demote = []types.JID{jid("777000005@lid")}
				e.Promote = []types.JID{jid("777000004@lid")}
				e.Leave = []types.JID{jid("777000003@lid")}
				e.Join = []types.JID{jid("777000002@lid"), jid("777000006@lid")}
				e.Locked = &types.GroupLocked{IsLocked: true}
				e.Announce = &types.GroupAnnounce{IsAnnounce: false, AnnounceVersionID: "V1"}
				e.Topic = &types.GroupTopic{Topic: "Plans", TopicID: "T2"}
				e.Name = &types.GroupName{Name: "Crew", NameSetAt: at}
			}),
			changed(
				&whatsmeowv1.GroupChange{Change: &whatsmeowv1.GroupChange_Renamed_{Renamed: &whatsmeowv1.GroupChange_Renamed{Name: "Crew"}}},
				&whatsmeowv1.GroupChange{Change: &whatsmeowv1.GroupChange_TopicChanged_{TopicChanged: &whatsmeowv1.GroupChange_TopicChanged{Topic: "Plans"}}},
				&whatsmeowv1.GroupChange{Change: &whatsmeowv1.GroupChange_AnnounceChanged_{AnnounceChanged: &whatsmeowv1.GroupChange_AnnounceChanged{}}},
				&whatsmeowv1.GroupChange{Change: &whatsmeowv1.GroupChange_LockedChanged_{LockedChanged: &whatsmeowv1.GroupChange_LockedChanged{Locked: true}}},
				&whatsmeowv1.GroupChange{Change: &whatsmeowv1.GroupChange_Joined{Joined: participants("777000002@lid", "777000006@lid")}},
				&whatsmeowv1.GroupChange{Change: &whatsmeowv1.GroupChange_Left{Left: participants("777000003@lid")}},
				&whatsmeowv1.GroupChange{Change: &whatsmeowv1.GroupChange_Promoted{Promoted: participants("777000004@lid")}},
				&whatsmeowv1.GroupChange{Change: &whatsmeowv1.GroupChange_Demoted{Demoted: participants("777000005@lid")}},
			),
		},
		{
			"a removed topic",
			eventInfo(at, func(e *events.GroupInfo) { e.Topic = &types.GroupTopic{TopicID: "T3", TopicDeleted: true} }),
			changed(&whatsmeowv1.GroupChange{Change: &whatsmeowv1.GroupChange_TopicChanged_{TopicChanged: &whatsmeowv1.GroupChange_TopicChanged{}}}),
		},
		{
			"a change WhatsApp gave no sender for",
			eventInfo(at, func(e *events.GroupInfo) {
				e.Sender, e.SenderPN = nil, nil
				e.Join = []types.JID{jid("5511777000002@s.whatsapp.net")}
			}),
			&whatsmeowv1.StreamEventsResponse{Payload: &whatsmeowv1.StreamEventsResponse_Group{Group: &whatsmeowv1.GroupEvent{
				Group: group, Time: timestamppb.New(at),
				Kind: &whatsmeowv1.GroupEvent_Changed_{Changed: &whatsmeowv1.GroupEvent_Changed{Changes: []*whatsmeowv1.GroupChange{
					{Change: &whatsmeowv1.GroupChange_Joined{Joined: participants("5511777000002@s.whatsapp.net")}},
				}}},
			}}},
		},
		{
			"the session joined",
			joinedGroup(lidGroup()),
			&whatsmeowv1.StreamEventsResponse{Payload: &whatsmeowv1.StreamEventsResponse_Group{Group: &whatsmeowv1.GroupEvent{
				Group: group, Sender: "777000001@lid", SenderAlt: "5511777000001@s.whatsapp.net",
				Kind: &whatsmeowv1.GroupEvent_Joined_{Joined: &whatsmeowv1.GroupEvent_Joined{Group: lidGroupProto()}},
			}}},
		},
		{
			"only changes the contract has no case for",
			eventInfo(at, func(e *events.GroupInfo) {
				e.Delete = &types.GroupDelete{Deleted: true}
				e.NewInviteLink = new(string)
				e.MembershipApprovalMode = &types.GroupMembershipApprovalMode{IsJoinApprovalRequired: true}
				e.Suspended = true
			}),
			nil,
		},
		// A member the client cannot read is left out, a list left empty dropped, and a change left with nothing not
		// mapped; whatsmeow keeps a created group's participant without a jid (group.go:718, 767, 804-806) (#122).
		{
			"members the client cannot read",
			eventInfo(at, func(e *events.GroupInfo) {
				e.Join = []types.JID{{User: "5511777000002"}, jid("777000002@lid")}
				e.Leave = []types.JID{{}}
				e.Promote = []types.JID{{User: "a.b.c", Server: types.HiddenUserServer}}
			}),
			changed(&whatsmeowv1.GroupChange{Change: &whatsmeowv1.GroupChange_Joined{Joined: participants("777000002@lid")}}),
		},
		{"only members the client cannot read", eventInfo(at, func(e *events.GroupInfo) { e.Demote = []types.JID{{}} }), nil},
		{
			"the session joined a group with a participant the client cannot read",
			joinedGroup(func() *types.GroupInfo {
				info := lidGroup()
				info.Participants = append([]types.GroupParticipant{{}}, info.Participants...)

				return info
			}()),
			&whatsmeowv1.StreamEventsResponse{Payload: &whatsmeowv1.StreamEventsResponse_Group{Group: &whatsmeowv1.GroupEvent{
				Group: group, Sender: "777000001@lid", SenderAlt: "5511777000001@s.whatsapp.net",
				Kind: &whatsmeowv1.GroupEvent_Joined_{Joined: &whatsmeowv1.GroupEvent_Joined{Group: lidGroupProto()}},
			}}},
		},
		{"a picture is the Contacts slice's", &events.Picture{JID: jid(group), PictureID: "P1"}, nil},
	} {
		key, got, ok := Event("s1", tc.evt)
		if ok != (tc.want != nil) || key != "" || !proto.Equal(got, tc.want) {
			t.Errorf("%s: Event = %q, %v, %v; want %v", tc.name, key, got, ok, tc.want)
		}
	}
}

func TestTheInfoLogCarriesNoJID(t *testing.T) {
	t.Parallel()

	var logged bytes.Buffer

	h := startLogging(t, zerolog.New(&logged).Level(zerolog.InfoLevel))
	h.stub.groups = []*types.GroupInfo{lidGroup()}
	h.stub.info = lidGroup()
	h.stub.created = lidGroup()
	h.stub.joined = jid(group)

	calls := func() {
		_, _ = h.client.ListGroups(h.ctx, &whatsmeowv1.ListGroupsRequest{SessionId: "s1"})
		_, _ = h.client.GetGroup(h.ctx, &whatsmeowv1.GetGroupRequest{SessionId: "s1", Group: group})
		_, _ = h.client.CreateGroup(h.ctx, &whatsmeowv1.CreateGroupRequest{SessionId: "s1", Name: "Team", Participants: []string{"777000002@lid"}})
		_, _ = h.client.UpdateParticipants(h.ctx, &whatsmeowv1.UpdateParticipantsRequest{
			SessionId: "s1", Group: group, Participants: []string{"777000002@lid"}, Action: whatsmeowv1.ParticipantAction_PARTICIPANT_ACTION_REMOVE,
		})
		_, _ = h.client.SetGroupName(h.ctx, &whatsmeowv1.SetGroupNameRequest{SessionId: "s1", Group: group, Name: "Crew"})
		_, _ = h.client.SetGroupTopic(h.ctx, &whatsmeowv1.SetGroupTopicRequest{SessionId: "s1", Group: group, Topic: "Plans"})
		_, _ = h.client.GetInviteLink(h.ctx, &whatsmeowv1.GetInviteLinkRequest{SessionId: "s1", Group: group})
		_, _ = h.client.JoinGroupWithLink(h.ctx, &whatsmeowv1.JoinGroupWithLinkRequest{SessionId: "s1", Link: "CODE"})
		_, _ = h.client.LeaveGroup(h.ctx, &whatsmeowv1.LeaveGroupRequest{SessionId: "s1", Group: group})
	}

	calls()

	h.stub.err = namedWithV()
	calls()

	if strings.Contains(logged.String(), "777000") || strings.Contains(logged.String(), "5511") {
		t.Fatalf("the info log carries a JID:\n%s", logged.String())
	}
}

// eventInfo is a change notification of the test group by 777000001@lid at at, as whatsmeow parses it, with set's
// changes (group.go:886-1014).
func eventInfo(at time.Time, set func(*events.GroupInfo)) *events.GroupInfo {
	e := &events.GroupInfo{JID: jid(group), Sender: ptr(jid("777000001@lid")), SenderPN: ptr(jid("5511777000001@s.whatsapp.net")), Timestamp: at}
	set(e)

	return e
}

// joinedGroup is a create notification of info, by 777000001@lid, as whatsmeow parses it (group.go:860-884).
func joinedGroup(info *types.GroupInfo) *events.JoinedGroup {
	return &events.JoinedGroup{Sender: ptr(jid("777000001@lid")), SenderPN: ptr(jid("5511777000001@s.whatsapp.net")), GroupInfo: *info}
}

// errOf is the error of a call's two results.
func errOf[T any](_ T, err error) error { return err }

// namedWithV is a store failure as whatsmeow wraps it, with %v and the participant's JID (group.go:84, 221).
func namedWithV() error {
	return fmt.Errorf("failed to get privacy token for participant %s: %v", "5511777000009@s.whatsapp.net", errors.New("database is locked")) //nolint:errorlint // as whatsmeow wraps it
}

func TestTheMemberAddModeTheTimerAndALinksInfoReachWhatsApp(t *testing.T) {
	t.Parallel()

	h := start(t)
	h.stub.info = lidGroup()
	h.stub.info.MemberAddMode = types.GroupMemberAddModeAdmin

	admins, all := whatsmeowv1.GroupMemberAddMode_GROUP_MEMBER_ADD_MODE_ADMINS, whatsmeowv1.GroupMemberAddMode_GROUP_MEMBER_ADD_MODE_ALL_MEMBERS
	timer := func(d time.Duration) error {
		return errOf(h.client.SetGroupDisappearingTimer(h.ctx, &whatsmeowv1.SetGroupDisappearingTimerRequest{SessionId: "s1", Group: group, Timer: durationpb.New(d)}))
	}

	for n, err := range []error{
		errOf(h.client.SetGroupMemberAddMode(h.ctx, &whatsmeowv1.SetGroupMemberAddModeRequest{SessionId: "s1", Group: group, Mode: admins})),
		errOf(h.client.SetGroupMemberAddMode(h.ctx, &whatsmeowv1.SetGroupMemberAddModeRequest{SessionId: "s1", Group: group, Mode: all})),
		timer(24 * time.Hour), timer(7 * 24 * time.Hour), timer(90 * 24 * time.Hour), timer(0),
	} {
		if err != nil {
			t.Fatalf("call %d: %v", n, err)
		}
	}

	// whatsmeow takes the link or its code (stripURLPrefix, group.go:490-497).
	for _, link := range []string{"https://chat.whatsapp.com/CODE1", "CODE1"} {
		resp, err := h.client.GetGroupInfoFromLink(h.ctx, &whatsmeowv1.GetGroupInfoFromLinkRequest{SessionId: "s1", Link: link})
		if err != nil {
			t.Fatalf("GetGroupInfoFromLink(%s): %v", link, err)
		}

		want := lidGroupProto()
		want.MemberAddMode = admins
		wantProto(t, resp, &whatsmeowv1.GetGroupInfoFromLinkResponse{Group: want})
	}

	// A group's timer is an IQ, for which whatsmeow takes no setting time (send.go:679-691).
	wantCalls(t, h.stub,
		"member add mode "+group+" admin_add",
		"member add mode "+group+" all_member_add",
		"timer "+group+" 24h0m0s at true", "timer "+group+" 168h0m0s at true", "timer "+group+" 2160h0m0s at true", "timer "+group+" 0s at true",
		"link info https://chat.whatsapp.com/CODE1", "link info CODE1",
	)
}

func TestAGroupsMemberAddModeIsTheContractsOrUnspecified(t *testing.T) {
	t.Parallel()

	for _, tc := range []struct {
		mode types.GroupMemberAddMode
		want whatsmeowv1.GroupMemberAddMode
	}{
		{types.GroupMemberAddModeAdmin, whatsmeowv1.GroupMemberAddMode_GROUP_MEMBER_ADD_MODE_ADMINS},
		{types.GroupMemberAddModeAllMember, whatsmeowv1.GroupMemberAddMode_GROUP_MEMBER_ADD_MODE_ALL_MEMBERS},
		// Not sent (group.go:785-787 sets it only from its child), or a mode the contract does not have.
		{"", whatsmeowv1.GroupMemberAddMode_GROUP_MEMBER_ADD_MODE_UNSPECIFIED},
		{"owner_add", whatsmeowv1.GroupMemberAddMode_GROUP_MEMBER_ADD_MODE_UNSPECIFIED},
	} {
		h := start(t)
		h.stub.info = lidGroup()
		h.stub.info.MemberAddMode = tc.mode

		resp, err := h.client.GetGroup(h.ctx, &whatsmeowv1.GetGroupRequest{SessionId: "s1", Group: group})
		if err != nil {
			t.Fatalf("GetGroup: %v", err)
		}

		want := lidGroupProto()
		want.MemberAddMode = tc.want
		wantProto(t, resp, &whatsmeowv1.GetGroupResponse{Group: want})
	}
}

func TestAMalformedModeTimerOrLinkIsInvalidArgumentAndAsksNothing(t *testing.T) {
	t.Parallel()

	h := start(t)
	admins := whatsmeowv1.GroupMemberAddMode_GROUP_MEMBER_ADD_MODE_ADMINS
	timer := func(d *durationpb.Duration) error {
		return errOf(h.client.SetGroupDisappearingTimer(h.ctx, &whatsmeowv1.SetGroupDisappearingTimerRequest{SessionId: "s1", Group: group, Timer: d}))
	}

	for _, tc := range []struct {
		field string
		err   error
	}{
		{"group", errOf(h.client.SetGroupMemberAddMode(h.ctx, &whatsmeowv1.SetGroupMemberAddModeRequest{SessionId: "s1", Group: "111@lid", Mode: admins}))},
		{"mode", errOf(h.client.SetGroupMemberAddMode(h.ctx, &whatsmeowv1.SetGroupMemberAddModeRequest{SessionId: "s1", Group: group}))},
		{"mode", errOf(h.client.SetGroupMemberAddMode(h.ctx, &whatsmeowv1.SetGroupMemberAddModeRequest{SessionId: "s1", Group: group, Mode: 7}))},
		{"group", errOf(h.client.SetGroupDisappearingTimer(h.ctx, &whatsmeowv1.SetGroupDisappearingTimerRequest{SessionId: "s1", Group: "111@lid", Timer: durationpb.New(0)}))},
		{"timer", timer(nil)},
		{"timer", timer(durationpb.New(48 * time.Hour))},
		{"timer", timer(durationpb.New(-24 * time.Hour))},
		{"timer", timer(durationpb.New(24*time.Hour + time.Second))},
		{"timer", timer(&durationpb.Duration{Seconds: 86400, Nanos: -1})},
		{"link", errOf(h.client.GetGroupInfoFromLink(h.ctx, &whatsmeowv1.GetGroupInfoFromLinkRequest{SessionId: "s1"}))},
		{"session_id", errOf(h.client.GetGroupInfoFromLink(h.ctx, &whatsmeowv1.GetGroupInfoFromLinkRequest{Link: "CODE1"}))},
	} {
		wantReason(t, tc.err, codes.InvalidArgument, "INVALID_ARGUMENT", map[string]string{"field": tc.field})
	}

	wantCalls(t, h.stub)
}

func TestTheNewCallsErrorsBecomeTheirReasons(t *testing.T) {
	t.Parallel()

	mode := func(h *harness) error {
		return errOf(h.client.SetGroupMemberAddMode(h.ctx, &whatsmeowv1.SetGroupMemberAddModeRequest{
			SessionId: "s1", Group: group, Mode: whatsmeowv1.GroupMemberAddMode_GROUP_MEMBER_ADD_MODE_ADMINS,
		}))
	}
	timer := func(h *harness) error {
		return errOf(h.client.SetGroupDisappearingTimer(h.ctx, &whatsmeowv1.SetGroupDisappearingTimerRequest{SessionId: "s1", Group: group, Timer: durationpb.New(24 * time.Hour)}))
	}
	link := func(h *harness) error {
		return errOf(h.client.GetGroupInfoFromLink(h.ctx, &whatsmeowv1.GetGroupInfoFromLinkRequest{SessionId: "s1", Link: "https://chat.whatsapp.com/OLD"}))
	}

	for _, tc := range []struct {
		name     string
		call     func(*harness) error
		err      error
		code     codes.Code
		reason   string
		metadata map[string]string
	}{
		{"a mode that timed out", mode, whatsmeow.ErrIQTimedOut, codes.Unknown, "OUTCOME_UNKNOWN", nil},
		{"a mode a member may not set", mode, whatsmeow.ErrIQForbidden, codes.FailedPrecondition, "WHATSAPP_ERROR", map[string]string{"code": "403"}},
		{"a timer that timed out", timer, whatsmeow.ErrIQTimedOut, codes.Unknown, "OUTCOME_UNKNOWN", nil},
		// As whatsmeow wraps a 400 (send.go:687-690).
		{"a timer WhatsApp refuses", timer, &wrapped{whatsmeow.ErrInvalidDisappearingTimer, whatsmeow.ErrIQBadRequest}, codes.FailedPrecondition, "WHATSAPP_ERROR", map[string]string{"code": "400"}},
		{"a timer in a group the session is not in", timer, whatsmeow.ErrIQForbidden, codes.FailedPrecondition, "WHATSAPP_ERROR", map[string]string{"code": "403"}},
		// As whatsmeow wraps them, the same as for a join (group.go:497-502).
		{"a link WhatsApp does not accept, as whatsmeow names a 406", link, fmt.Errorf("%w: %w", whatsmeow.ErrInviteLinkInvalid, whatsmeow.ErrIQNotAcceptable), codes.NotFound, "INVITE_LINK_INVALID", nil},
		{"a reset link", link, fmt.Errorf("%w: %w", whatsmeow.ErrInviteLinkRevoked, whatsmeow.ErrIQGone), codes.NotFound, "INVITE_LINK_INVALID", nil},
		// As WhatsApp answers it, as the join [observed: #120 live run 2026-11-04, live sidecar.log].
		{"an unknown link, as WhatsApp answers a join with it", link, &whatsmeow.IQError{Code: 400, Text: "bad-request"}, codes.NotFound, "INVITE_LINK_INVALID", nil},
		{"a link read that timed out", link, whatsmeow.ErrIQTimedOut, codes.Unavailable, "WHATSAPP_TIMED_OUT", nil},
		{"a link answer without its group", link, &whatsmeow.ElementMissingError{Tag: "group", In: "response to group link info query"}, codes.Internal, "INTERNAL", nil},
	} {
		t.Run(tc.name, func(t *testing.T) {
			t.Parallel()

			h := start(t)
			h.stub.err = tc.err

			wantReason(t, tc.call(h), tc.code, tc.reason, tc.metadata)
		})
	}
}

// wrapped is an error as whatsmeow's wrapIQError makes it (errors.go:158-177): Is the human error, unwrapping to the IQ
// error.
type wrapped struct{ human, iq error }

func (w *wrapped) Error() string       { return w.human.Error() }
func (w *wrapped) Is(other error) bool { return errors.Is(other, w.human) }
func (w *wrapped) Unwrap() error       { return w.iq }

func TestMemberAddModeAndTimerNotificationsMapToTheirChanges(t *testing.T) {
	t.Parallel()

	at := time.Date(2026, 11, 2, 10, 0, 0, 0, time.UTC)
	changed := func(changes ...*whatsmeowv1.GroupChange) *whatsmeowv1.StreamEventsResponse {
		return &whatsmeowv1.StreamEventsResponse{Payload: &whatsmeowv1.StreamEventsResponse_Group{Group: &whatsmeowv1.GroupEvent{
			Group: group, Sender: "777000001@lid", SenderAlt: "5511777000001@s.whatsapp.net", Time: timestamppb.New(at),
			Kind: &whatsmeowv1.GroupEvent_Changed_{Changed: &whatsmeowv1.GroupEvent_Changed{Changes: changes}},
		}}}
	}
	// whatsmeow has no case for member_add_mode and keeps the child as it came (group.go:1006-1007).
	addMode := func(content any) *waBinary.Node { return &waBinary.Node{Tag: "member_add_mode", Content: content} }
	mode := func(m whatsmeowv1.GroupMemberAddMode) *whatsmeowv1.GroupChange {
		return &whatsmeowv1.GroupChange{Change: &whatsmeowv1.GroupChange_MemberAddModeChanged_{MemberAddModeChanged: &whatsmeowv1.GroupChange_MemberAddModeChanged{Mode: m}}}
	}
	timer := func(d time.Duration) *whatsmeowv1.GroupChange {
		return &whatsmeowv1.GroupChange{Change: &whatsmeowv1.GroupChange_DisappearingTimerChanged_{DisappearingTimerChanged: &whatsmeowv1.GroupChange_DisappearingTimerChanged{Timer: durationpb.New(d)}}}
	}

	for _, tc := range []struct {
		name string
		evt  any
		want *whatsmeowv1.StreamEventsResponse
	}{
		{
			"only admins add, in the contract's order",
			eventInfo(at, func(e *events.GroupInfo) {
				e.Join = []types.JID{jid("777000002@lid")}
				e.UnknownChanges = []*waBinary.Node{{Tag: "something_new"}, addMode([]byte("admin_add"))}
				e.Ephemeral = &types.GroupEphemeral{IsEphemeral: true, DisappearingTimer: 604800}
				e.Locked = &types.GroupLocked{IsLocked: true}
			}),
			changed(
				&whatsmeowv1.GroupChange{Change: &whatsmeowv1.GroupChange_LockedChanged_{LockedChanged: &whatsmeowv1.GroupChange_LockedChanged{Locked: true}}},
				mode(whatsmeowv1.GroupMemberAddMode_GROUP_MEMBER_ADD_MODE_ADMINS),
				timer(7*24*time.Hour),
				&whatsmeowv1.GroupChange{Change: &whatsmeowv1.GroupChange_Joined{Joined: &whatsmeowv1.GroupChange_Participants{Jids: []string{"777000002@lid"}}}},
			),
		},
		{
			"every member adds",
			eventInfo(at, func(e *events.GroupInfo) { e.UnknownChanges = []*waBinary.Node{addMode([]byte("all_member_add"))} }),
			changed(mode(whatsmeowv1.GroupMemberAddMode_GROUP_MEMBER_ADD_MODE_ALL_MEMBERS)),
		},
		{
			// not_ephemeral (group.go:963-970).
			"disappearing messages turned off",
			eventInfo(at, func(e *events.GroupInfo) { e.Ephemeral = &types.GroupEphemeral{} }),
			changed(timer(0)),
		},
		{
			"a mode the contract does not have, or one that is not text",
			eventInfo(at, func(e *events.GroupInfo) {
				e.UnknownChanges = []*waBinary.Node{addMode([]byte("owner_add")), addMode(nil), addMode([]waBinary.Node{})}
			}),
			nil,
		},
	} {
		key, got, ok := Event("s1", tc.evt)
		if ok != (tc.want != nil) || key != "" || !proto.Equal(got, tc.want) {
			t.Errorf("%s: Event = %q, %v, %v; want %v", tc.name, key, got, ok, tc.want)
		}
	}
}
