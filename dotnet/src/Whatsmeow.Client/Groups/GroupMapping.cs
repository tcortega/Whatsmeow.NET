using System.Collections.Immutable;
using Whatsmeow.Core.Errors;
using Whatsmeow.Events;
using Whatsmeow.Protocol.V1;
using Change = Whatsmeow.Protocol.V1.GroupChange.ChangeOneofCase;
using Kind = Whatsmeow.Protocol.V1.GroupEvent.KindOneofCase;

namespace Whatsmeow.Groups;

/// <summary>group.proto as the public records.</summary>
internal static class GroupMapping
{
	public static ImmutableArray<GroupInfo> ToGroups(ListGroupsResponse response) => [.. response.Groups.Select(ToGroup)];

	public static GroupInfo ToGroup(Group group) =>
		new()
		{
			Jid = SidecarValues.Jid(group.Jid, "Group.jid"),
			Name = group.Name,
			Topic = NullIfEmpty(group.Topic),
			Owner = ToJid(group.Owner),
			OwnerAlt = ToJid(group.OwnerAlt),
			CreatedAt = group.CreateTime is null ? null : EventMapping.ToTime(group.CreateTime),
			IsAnnounce = group.Announce,
			IsLocked = group.Locked,
			Participants = [.. group.Participants.Select(ToParticipant)],
			MemberAddMode = ToMode(group.MemberAddMode),
		};

	/// <summary>The public mode as the contract's: a value the enum does not name is unspecified, which the sidecar refuses.</summary>
	public static Protocol.V1.GroupMemberAddMode ToProto(GroupMemberAddMode mode) =>
		mode switch
		{
			GroupMemberAddMode.AdminsOnly => Protocol.V1.GroupMemberAddMode.Admins,
			GroupMemberAddMode.AllMembers => Protocol.V1.GroupMemberAddMode.AllMembers,
			_ => Protocol.V1.GroupMemberAddMode.Unspecified,
		};

	/// <summary>A mode WhatsApp did not say, or one this client does not know, is null.</summary>
	private static GroupMemberAddMode? ToMode(Protocol.V1.GroupMemberAddMode mode) =>
		mode switch
		{
			Protocol.V1.GroupMemberAddMode.Admins => GroupMemberAddMode.AdminsOnly,
			Protocol.V1.GroupMemberAddMode.AllMembers => GroupMemberAddMode.AllMembers,
			Protocol.V1.GroupMemberAddMode.Unspecified => null,
			_ => null,
		};

	public static CreatedGroup ToCreated(CreateGroupResponse response) =>
		new() { Group = ToGroup(response.Group), Participants = ToResults(response.Participants) };

	public static ImmutableArray<ParticipantResult> ToResults(IEnumerable<Protocol.V1.ParticipantResult> results) =>
		[
			.. results.Select(result => new ParticipantResult
			{
				Jid = SidecarValues.Jid(result.Jid, "ParticipantResult.jid"),
				JidAlt = ToJid(result.JidAlt),
				Outcome = result.ErrorCode == 0
					? new ParticipantOutcome.Done()
					: new ParticipantOutcome.Refused
					{
						Code = result.ErrorCode,
						Invite = result.Invite is null ? null : new GroupAddInvite { Code = result.Invite.Code, ExpiresAt = EventMapping.ToTime(result.Invite.ExpireTime) },
					},
			}),
		];

	/// <summary>The event of a GroupEvent, or null for a kind this client does not know.</summary>
	/// <exception cref="WhatsmeowException">The group's JID is missing or malformed: the event is unusable (<see cref="WhatsmeowErrorCode.Internal"/>).</exception>
	public static WhatsAppEvent? ToEvent(SessionId sessionId, GroupEvent group) =>
		group.KindCase switch
		{
			Kind.Changed => ToChanges(group.Changed) is { IsEmpty: false } changes
				? new GroupInfoChanged
				{
					SessionId = sessionId,
					Group = SidecarValues.Jid(group.Group, "GroupEvent.group"),
					Sender = ToJid(group.Sender),
					SenderAlt = ToJid(group.SenderAlt),
					Timestamp = EventMapping.ToTime(group.Time),
					Changes = changes,
				}
				: null,
			Kind.Joined => new GroupJoined
			{
				SessionId = sessionId,
				Group = ToGroup(group.Joined.Group),
				Sender = ToJid(group.Sender),
				SenderAlt = ToJid(group.SenderAlt),
			},
			Kind.None => null,
			_ => null,
		};

	/// <summary>The changes, one per participant for the participant lists; a change this client does not know is left out.</summary>
	private static ImmutableArray<GroupChange> ToChanges(GroupEvent.Types.Changed changed) =>
		[
			.. changed.Changes.SelectMany<Protocol.V1.GroupChange, GroupChange>(change =>
				change.ChangeCase switch
				{
					Change.Renamed => [new GroupChange.Renamed { Name = change.Renamed.Name }],
					Change.TopicChanged => [new GroupChange.TopicChanged { Topic = NullIfEmpty(change.TopicChanged.Topic) }],
					Change.AnnounceChanged => [new GroupChange.AnnounceChanged { IsAnnounce = change.AnnounceChanged.Announce }],
					Change.LockedChanged => [new GroupChange.LockedChanged { IsLocked = change.LockedChanged.Locked }],
					Change.MemberAddModeChanged => ToMode(change.MemberAddModeChanged.Mode) is { } mode ? [new GroupChange.MemberAddModeChanged { Mode = mode }] : [],
					Change.DisappearingTimerChanged => [new GroupChange.DisappearingTimerChanged { Timer = change.DisappearingTimerChanged.Timer?.ToTimeSpan() ?? TimeSpan.Zero }],
					Change.Joined => [.. change.Joined.Jids.Select(jid => new GroupChange.ParticipantJoined { Jid = SidecarValues.Jid(jid, "GroupChange.Participants.jids") })],
					Change.Left => [.. change.Left.Jids.Select(jid => new GroupChange.ParticipantLeft { Jid = SidecarValues.Jid(jid, "GroupChange.Participants.jids") })],
					Change.Promoted => [.. change.Promoted.Jids.Select(jid => new GroupChange.ParticipantPromoted { Jid = SidecarValues.Jid(jid, "GroupChange.Participants.jids") })],
					Change.Demoted => [.. change.Demoted.Jids.Select(jid => new GroupChange.ParticipantDemoted { Jid = SidecarValues.Jid(jid, "GroupChange.Participants.jids") })],
					Change.None => [],
					_ => [],
				}
			),
		];

	private static GroupParticipant ToParticipant(Protocol.V1.GroupParticipant participant) =>
		new()
		{
			Jid = SidecarValues.Jid(participant.Jid, "GroupParticipant.jid"),
			JidAlt = ToJid(participant.JidAlt),
			Role = participant.Role switch
			{
				Protocol.V1.GroupRole.SuperAdmin => GroupRole.SuperAdmin,
				Protocol.V1.GroupRole.Admin => GroupRole.Admin,
				Protocol.V1.GroupRole.Member or Protocol.V1.GroupRole.Unspecified => GroupRole.Member,
				_ => GroupRole.Member,
			},
		};

	/// <summary>An empty or malformed JID is unknown.</summary>
	private static Jid? ToJid(string value) => Jid.TryParse(value, out var jid) ? jid : null;

	private static string? NullIfEmpty(string value) => value.Length == 0 ? null : value;
}
