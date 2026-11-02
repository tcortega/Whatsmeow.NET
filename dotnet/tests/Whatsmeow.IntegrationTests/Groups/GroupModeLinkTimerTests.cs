using Google.Protobuf.WellKnownTypes;
using Whatsmeow.Groups;
using Whatsmeow.IntegrationTests.Fake;
using Whatsmeow.IntegrationTests.Sessions;
using Whatsmeow.IntegrationTests.Support;

namespace Whatsmeow.IntegrationTests.Groups;

/// <summary>
/// Who may add members (#104), a group's info from its invite link (#105) and a group's disappearing timer (#106),
/// through the public client, on the shared fake-mode sidecar. As in <see cref="GroupTests"/>, each test has sessions,
/// groups and numbers of its own: 55119N..., N...@lid and 12036312N...@g.us, N its own digits.
/// </summary>
public sealed class GroupModeLinkTimerTests(FakeSidecar fixture)
{
	private const int Timeout = 120_000;

	/// <summary>WhatsApp's time on every group call of the fake.</summary>
	private static readonly DateTimeOffset s_at = new(2026, 11, 1, 12, 0, 0, TimeSpan.Zero);

	private static Jid Pn(string user) => new() { User = user, Server = "s.whatsapp.net" };

	private static Jid Lid(string user) => new() { User = user, Server = "lid" };

	/// <summary>A session's own user, as SessionSetup links it.</summary>
	private static Jid Self(string session) => Pn(session);

	private static GroupParticipant Member(Jid jid, GroupRole role = GroupRole.Member) => new() { Jid = jid, JidAlt = null, Role = role };

	/// <summary>
	/// A group the session created alone in the fake, as WhatsApp answers it: with the member add mode whatsmeow's create
	/// asks for (group.go:59-63); what WhatsApp answers after a create is [unknown].
	/// </summary>
	private static GroupInfo Created(Jid jid, string session, string name, params GroupParticipant[] others) =>
		new()
		{
			Jid = jid,
			Name = name,
			Topic = null,
			Owner = Self(session),
			OwnerAlt = null,
			CreatedAt = s_at,
			IsAnnounce = false,
			IsLocked = false,
			Participants = [Member(Self(session), GroupRole.SuperAdmin), .. others],
			MemberAddMode = GroupMemberAddMode.AllMembers,
		};

	private static GroupInfoChanged Changed(string session, Jid group, Jid? sender, params GroupChange[] changes) =>
		new()
		{
			SessionId = SessionId.From(session),
			Group = group,
			Sender = sender,
			SenderAlt = null,
			Timestamp = s_at,
			Changes = [.. changes],
		};

	[Fact(Timeout = Timeout)]
	public async Task FullCycle_MemberAddMode_AdminsOnlyThenAllMembers_MembersSeeEachChangeAndOnlyThoseAllowedAdd()
	{
		var ct = TestContext.Current.CancellationToken;
		var admin = await SessionSetup.ConnectedAsync(fixture, "groups-addmode-a", ct);
		var member = await SessionSetup.ConnectedAsync(fixture, "groups-addmode-b", ct);
		using var changes = new Received<GroupInfoChanged>(member);
		var group = (await admin.CreateGroupAsync("Team", [Self("groups-addmode-b")], ct)).Group.Jid;
		var by = Self("groups-addmode-a");

		await admin.SetGroupMemberAddModeAsync(group, GroupMemberAddMode.AdminsOnly, ct);

		Assert.Equal(Changed("groups-addmode-b", group, by, new GroupChange.MemberAddModeChanged { Mode = GroupMemberAddMode.AdminsOnly }), await changes.NextAsync(ct));
		var expected = Created(group, "groups-addmode-a", "Team", Member(Self("groups-addmode-b"))) with { MemberAddMode = GroupMemberAddMode.AdminsOnly };
		Assert.Equal(expected, await member.GetGroupInfoAsync(group, ct));
		// A member who is not an admin can no longer add [inferred: 403].
		var refused = await Assert.ThrowsAsync<WhatsAppErrorException>(() => member.AddParticipantsAsync(group, [Pn("5511921000003")], ct));
		Assert.Equal((WhatsmeowErrorCode.WhatsAppError, 403), (refused.ErrorCode, refused.Code));

		await admin.SetGroupMemberAddModeAsync(group, GroupMemberAddMode.AllMembers, ct);

		Assert.Equal(Changed("groups-addmode-b", group, by, new GroupChange.MemberAddModeChanged { Mode = GroupMemberAddMode.AllMembers }), await changes.NextAsync(ct));
		Assert.Equal(
			[new ParticipantResult { Jid = Pn("5511921000003"), JidAlt = null, Outcome = new ParticipantOutcome.Done() }],
			await member.AddParticipantsAsync(group, [Pn("5511921000003")], ct)
		);
		Assert.Equal(
			expected with { MemberAddMode = GroupMemberAddMode.AllMembers, Participants = [.. expected.Participants, Member(Pn("5511921000003"))] },
			await admin.GetGroupInfoAsync(group, ct)
		);
	}

	[Fact(Timeout = Timeout)]
	public async Task SetGroupMemberAddMode_NotAnAdmin_ThrowsWhatsAppErrorWithItsCode_AndTheModeStays()
	{
		var ct = TestContext.Current.CancellationToken;
		var admin = await SessionSetup.ConnectedAsync(fixture, "groups-addmode-refused-a", ct);
		var member = await SessionSetup.ConnectedAsync(fixture, "groups-addmode-refused-b", ct);
		var group = (await admin.CreateGroupAsync("Team", [Self("groups-addmode-refused-b")], ct)).Group.Jid;
		await admin.SetGroupMemberAddModeAsync(group, GroupMemberAddMode.AdminsOnly, ct);

		var thrown = await Assert.ThrowsAsync<WhatsAppErrorException>(() => member.SetGroupMemberAddModeAsync(group, GroupMemberAddMode.AllMembers, ct));

		// A refusal for lack of admin rights has no reason of its own [inferred: 403].
		Assert.Equal((WhatsmeowErrorCode.WhatsAppError, 403), (thrown.ErrorCode, thrown.Code));
		Assert.Equal(GroupMemberAddMode.AdminsOnly, (await member.GetGroupInfoAsync(group, ct)).MemberAddMode);
	}

	[Fact(Timeout = Timeout)]
	public async Task GroupNotification_ModeAndTimerChangedByAnotherAdmin_TheHandlerGetsWhoChangedThemAndToWhat()
	{
		var ct = TestContext.Current.CancellationToken;
		var client = await SessionSetup.ConnectedAsync(fixture, "groups-addmode-notified", ct);
		using var changes = new Received<GroupInfoChanged>(client);
		var at = new DateTimeOffset(2026, 10, 30, 8, 0, 0, TimeSpan.Zero);
		var group = Jid.Parse("120363121000000001@g.us");

		foreach (var (id, mode, timer) in new[] { ("GN-ADDMODE-1", "admin_add", TimeSpan.FromDays(7)), ("GN-ADDMODE-2", "all_member_add", TimeSpan.Zero) })
		{
			_ = await fixture.Fake.DeliverGroupNotificationAsync(
				new DeliverGroupNotificationRequest
				{
					SessionId = "groups-addmode-notified",
					Id = id,
					Group = group.ToString(),
					Sender = "921000001@lid",
					SenderPn = "5511921000001@s.whatsapp.net",
					Time = Timestamp.FromDateTimeOffset(at),
					MemberAddMode = mode,
					DisappearingTimer = Duration.FromTimeSpan(timer),
				},
				cancellationToken: ct
			);
		}

		var expected = new GroupInfoChanged
		{
			SessionId = SessionId.From("groups-addmode-notified"),
			Group = group,
			Sender = Lid("921000001"),
			SenderAlt = Pn("5511921000001"),
			Timestamp = at,
			Changes = [new GroupChange.MemberAddModeChanged { Mode = GroupMemberAddMode.AdminsOnly }, new GroupChange.DisappearingTimerChanged { Timer = TimeSpan.FromDays(7) }],
		};
		Assert.Equal(expected, await changes.NextAsync(ct));
		Assert.Equal(
			expected with
			{
				Changes = [new GroupChange.MemberAddModeChanged { Mode = GroupMemberAddMode.AllMembers }, new GroupChange.DisappearingTimerChanged { Timer = TimeSpan.Zero }],
			},
			await changes.NextAsync(ct)
		);
	}

	[Fact(Timeout = Timeout)]
	public async Task GetGroupInfoFromLink_TheLinkOrItsCode_GivesTheGroupAsANonMemberSeesIt_AndTheSessionDoesNotJoin()
	{
		var ct = TestContext.Current.CancellationToken;
		var admin = await SessionSetup.ConnectedAsync(fixture, "groups-preview-a", ct);
		var outsider = await SessionSetup.ConnectedAsync(fixture, "groups-preview-b", ct);
		var group = (await admin.CreateGroupAsync("Team", [Pn("5511922000002")], ct)).Group.Jid;
		await admin.SetGroupTopicAsync(group, "Plans", ct);
		var link = await admin.GetGroupInviteLinkAsync(group, ct);
		var code = new Uri(link.ToString()["https://chat.whatsapp.com/".Length..], UriKind.Relative);

		// Which participants WhatsApp lists for a non-member is [unknown]; the fake lists every member.
		var expected = Created(group, "groups-preview-a", "Team", Member(Pn("5511922000002"))) with { Topic = "Plans" };
		Assert.Equal(expected, await outsider.GetGroupInfoFromLinkAsync(link, ct));
		Assert.Equal(expected, await outsider.GetGroupInfoFromLinkAsync(code, ct));

		Assert.Empty(await outsider.GetGroupsAsync(ct));
		Assert.Equal(expected, await admin.GetGroupInfoAsync(group, ct));
	}

	[Fact(Timeout = Timeout)]
	public async Task GetGroupInfoFromLink_AResetOrUnknownLink_ThrowsInviteLinkInvalid_AsAJoinWithItDoes()
	{
		var ct = TestContext.Current.CancellationToken;
		var admin = await SessionSetup.ConnectedAsync(fixture, "groups-preview-invalid-a", ct);
		var outsider = await SessionSetup.ConnectedAsync(fixture, "groups-preview-invalid-b", ct);
		var group = (await admin.CreateGroupAsync("Team", [], ct)).Group.Jid;
		var old = await admin.GetGroupInviteLinkAsync(group, ct);
		_ = await admin.ResetGroupInviteLinkAsync(group, ct);

		foreach (var link in new[] { old, new Uri("https://chat.whatsapp.com/NOSUCHCODE") })
		{
			var preview = await Assert.ThrowsAsync<WhatsmeowException>(() => outsider.GetGroupInfoFromLinkAsync(link, ct));
			var join = await Assert.ThrowsAsync<WhatsmeowException>(() => outsider.JoinGroupWithLinkAsync(link, ct));
			Assert.Equal((WhatsmeowErrorCode.InviteLinkInvalid, WhatsmeowErrorCode.InviteLinkInvalid), (preview.ErrorCode, join.ErrorCode));
		}
	}

	[Fact(Timeout = Timeout)]
	public async Task FullCycle_SetDisappearingTimer_AGroup_MembersSeeTheTimerThenItsEnd_AndOneNotOfferedIsRefusedUnsent()
	{
		var ct = TestContext.Current.CancellationToken;
		var admin = await SessionSetup.ConnectedAsync(fixture, "groups-timer-a", ct);
		var member = await SessionSetup.ConnectedAsync(fixture, "groups-timer-b", ct);
		using var changes = new Received<GroupInfoChanged>(member);
		var group = (await admin.CreateGroupAsync("Team", [Self("groups-timer-b")], ct)).Group.Jid;
		var by = Self("groups-timer-a");

		await admin.SetDisappearingTimerAsync(group, TimeSpan.FromDays(7), ct);
		Assert.Equal(Changed("groups-timer-b", group, by, new GroupChange.DisappearingTimerChanged { Timer = TimeSpan.FromDays(7) }), await changes.NextAsync(ct));

		// Only 24 hours, 7 days, 90 days and off are offered; nothing reaches WhatsApp, so the next change members see
		// is the one after.
		var thrown = await Assert.ThrowsAsync<WhatsmeowValidationException>(() => admin.SetDisappearingTimerAsync(group, TimeSpan.FromDays(2), ct));
		Assert.Equal(WhatsmeowErrorCode.InvalidArgument, thrown.ErrorCode);

		await admin.SetDisappearingTimerAsync(group, TimeSpan.Zero, ct);
		Assert.Equal(Changed("groups-timer-b", group, by, new GroupChange.DisappearingTimerChanged { Timer = TimeSpan.Zero }), await changes.NextAsync(ct));
	}
}
