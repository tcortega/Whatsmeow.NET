using Google.Protobuf.WellKnownTypes;
using Whatsmeow.Groups;
using Whatsmeow.IntegrationTests.Fake;
using Whatsmeow.IntegrationTests.Sessions;
using Whatsmeow.IntegrationTests.Support;

namespace Whatsmeow.IntegrationTests.Groups;

/// <summary>
/// The Groups slice through the public client, on the shared fake-mode sidecar. WhatsApp's groups, users and refusals
/// are shared by every test, so each test uses sessions, groups and numbers of its own: 55119N..., N...@lid and
/// 12036310N...@g.us, N its own digits. The fake's group clock is fixed (fake.proto, DeliverGroupNotification).
/// </summary>
public sealed class GroupTests(FakeSidecar fixture)
{
	private const int Timeout = 120_000;

	/// <summary>WhatsApp's time on every group call of the fake.</summary>
	private static readonly DateTimeOffset s_at = new(2026, 11, 1, 12, 0, 0, TimeSpan.Zero);

	private static Jid Pn(string user) => new() { User = user, Server = "s.whatsapp.net" };

	private static Jid Lid(string user) => new() { User = user, Server = "lid" };

	private static Jid Group(string user) => new() { User = user, Server = "g.us" };

	/// <summary>A session's own user, as SessionSetup links it.</summary>
	private static Jid Self(string session) => Pn(session);

	private static GroupParticipant Member(Jid jid, GroupRole role = GroupRole.Member, Jid? alt = null) => new() { Jid = jid, JidAlt = alt, Role = role };

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
		GroupChecks.Changed(SessionId.From(session), group, sender, senderAlt: null, s_at, changes);

	[Fact(Timeout = Timeout)]
	public async Task GetGroups_TheSessionsGroups_EachWithNameTopicParticipantsAndRoles()
	{
		var ct = TestContext.Current.CancellationToken;
		var client = await SessionSetup.ConnectedAsync(fixture, "groups-list", ct);
		var self = Self("groups-list");
		_ = await fixture.Fake.AddGroupAsync(
			new AddGroupRequest
			{
				Jid = "120363101000000002@g.us",
				Name = "Work",
				Owner = "5511910100001@s.whatsapp.net",
				Members =
				{
					new AddGroupRequest.Types.Member { Jid = "5511910100001@s.whatsapp.net", Type = "superadmin" },
					new AddGroupRequest.Types.Member { Jid = self.ToString(), Type = "admin" },
				},
			},
			cancellationToken: ct
		);
		_ = await fixture.Fake.AddGroupAsync(
			new AddGroupRequest
			{
				Jid = "120363101000000001@g.us",
				Name = "Family",
				Topic = "Dinner on Sunday",
				Owner = self.ToString(),
				Members =
				{
					new AddGroupRequest.Types.Member { Jid = self.ToString(), Type = "superadmin" },
					new AddGroupRequest.Types.Member { Jid = "5511910100002@s.whatsapp.net" },
				},
			},
			cancellationToken: ct
		);
		// A group the session is not in.
		_ = await fixture.Fake.AddGroupAsync(
			new AddGroupRequest
			{
				Jid = "120363101000000003@g.us",
				Name = "Others",
				Members = { new AddGroupRequest.Types.Member { Jid = "5511910100001@s.whatsapp.net", Type = "superadmin" } },
			},
			cancellationToken: ct
		);

		var groups = await client.GetGroupsAsync(ct);

		Assert.Equal(2, groups.Length);
		Assert.Equal(
			new GroupInfo
			{
				Jid = Group("120363101000000001"),
				Name = "Family",
				Topic = "Dinner on Sunday",
				Owner = self,
				OwnerAlt = null,
				CreatedAt = null,
				IsAnnounce = false,
				IsLocked = false,
				Participants = [Member(self, GroupRole.SuperAdmin), Member(Pn("5511910100002"))],
			},
			groups[0]
		);
		Assert.Equal(
			new GroupInfo
			{
				Jid = Group("120363101000000002"),
				Name = "Work",
				Topic = null,
				Owner = Pn("5511910100001"),
				OwnerAlt = null,
				CreatedAt = null,
				IsAnnounce = false,
				IsLocked = false,
				Participants = [Member(Pn("5511910100001"), GroupRole.SuperAdmin), Member(self, GroupRole.Admin)],
			},
			groups[1]
		);
	}

	[Fact(Timeout = Timeout)]
	public async Task GetGroupInfo_AGroup_HasOwnerCreationTimeAnnounceAndLocked_AndOneTheSessionIsNotInThrows()
	{
		var ct = TestContext.Current.CancellationToken;
		var client = await SessionSetup.ConnectedAsync(fixture, "groups-info", ct);
		var self = Self("groups-info");
		var created = new DateTimeOffset(2025, 3, 4, 5, 6, 7, TimeSpan.Zero);
		_ = await fixture.Fake.AddGroupAsync(
			new AddGroupRequest
			{
				Jid = "120363102000000001@g.us",
				Name = "News",
				Topic = "Only admins post",
				Owner = "5511910200001@s.whatsapp.net",
				CreateTime = Timestamp.FromDateTimeOffset(created),
				Announce = true,
				Locked = true,
				Members =
				{
					new AddGroupRequest.Types.Member { Jid = "5511910200001@s.whatsapp.net", Type = "superadmin" },
					new AddGroupRequest.Types.Member { Jid = self.ToString() },
				},
			},
			cancellationToken: ct
		);
		_ = await fixture.Fake.AddGroupAsync(
			new AddGroupRequest
			{
				Jid = "120363102000000002@g.us",
				Name = "Elsewhere",
				Members = { new AddGroupRequest.Types.Member { Jid = "5511910200001@s.whatsapp.net", Type = "superadmin" } },
			},
			cancellationToken: ct
		);

		Assert.Equal(
			new GroupInfo
			{
				Jid = Group("120363102000000001"),
				Name = "News",
				Topic = "Only admins post",
				Owner = Pn("5511910200001"),
				OwnerAlt = null,
				CreatedAt = created,
				IsAnnounce = true,
				IsLocked = true,
				Participants = [Member(Pn("5511910200001"), GroupRole.SuperAdmin), Member(self)],
			},
			await client.GetGroupInfoAsync(Group("120363102000000001"), ct)
		);

		// WhatsApp's own codes: not in the group, and no such group (group.go:674-678).
		var notIn = await Assert.ThrowsAsync<WhatsAppErrorException>(() => client.GetGroupInfoAsync(Group("120363102000000002"), ct));
		Assert.Equal((WhatsmeowErrorCode.WhatsAppError, 403), (notIn.ErrorCode, notIn.Code));
		var unknown = await Assert.ThrowsAsync<WhatsAppErrorException>(() => client.GetGroupInfoAsync(Group("120363102000000009"), ct));
		Assert.Equal((WhatsmeowErrorCode.WhatsAppError, 404), (unknown.ErrorCode, unknown.Code));

		// The group's JID carries no number of a user, but an older one does: none reaches the exception.
		Assert.DoesNotContain("120363102000000002", notIn.ToString(), StringComparison.Ordinal);
		_ = await Assert.ThrowsAsync<WhatsmeowValidationException>(() => client.GetGroupInfoAsync(Pn("5511910200001"), ct));
		_ = await Assert.ThrowsAsync<WhatsmeowValidationException>(() => client.GetGroupInfoAsync(Group("120363102000000001") with { Device = 3 }, ct));
	}

	[Fact(Timeout = Timeout)]
	public async Task CreateGroup_WithParticipants_TheGroupExistsWithThemAndTheApplicationGetsItsInfo()
	{
		var ct = TestContext.Current.CancellationToken;
		var client = await SessionSetup.ConnectedAsync(fixture, "groups-create", ct);
		_ = await fixture.Fake.RefuseGroupAddAsync(new RefuseGroupAddRequest { Jid = "5511910300003@s.whatsapp.net", Code = 403, Invite = true }, cancellationToken: ct);

		var created = await client.CreateGroupAsync("Team", [Pn("5511910300001") with { Device = 2 }, Pn("5511910300002"), Pn("5511910300003")], ct);

		// The JID is WhatsApp's to choose; everything else is what was asked, and the refused participant is not in it.
		var expected = Created(created.Group.Jid, "groups-create", "Team", Member(Pn("5511910300001")), Member(Pn("5511910300002")));
		Assert.Equal(
			new CreatedGroup
			{
				Group = expected,
				Participants =
				[
					new ParticipantResult { Jid = Pn("5511910300001"), JidAlt = null, Outcome = new ParticipantOutcome.Done() },
					new ParticipantResult { Jid = Pn("5511910300002"), JidAlt = null, Outcome = new ParticipantOutcome.Done() },
					new ParticipantResult
					{
						Jid = Pn("5511910300003"),
						JidAlt = null,
						Outcome = new ParticipantOutcome.Refused
						{
							Code = 403,
							Invite = new GroupAddInvite { Code = "INVITE-5511910300003", ExpiresAt = s_at.AddHours(72) },
						},
					},
				],
			},
			created
		);

		Assert.Equal(expected, await client.GetGroupInfoAsync(created.Group.Jid, ct));
		_ = await Assert.ThrowsAsync<WhatsmeowValidationException>(() => client.CreateGroupAsync("", [], ct));
	}

	[Fact(Timeout = Timeout)]
	public async Task Participants_AddPromoteDemoteRemove_EachSaysWhetherItWorkedAndAFailureForSomeNeverThrows()
	{
		var ct = TestContext.Current.CancellationToken;
		var client = await SessionSetup.ConnectedAsync(fixture, "groups-participants", ct);
		var self = Self("groups-participants");
		_ = await fixture.Fake.RefuseGroupAddAsync(new RefuseGroupAddRequest { Jid = "5511910400002@s.whatsapp.net", Code = 403, Invite = true }, cancellationToken: ct);
		var group = (await client.CreateGroupAsync("Team", [], ct)).Group.Jid;

		// Added, refused with an invite as their privacy settings ask, and already a member [inferred: 409].
		Assert.Equal(
			[
				new ParticipantResult { Jid = Pn("5511910400001"), JidAlt = null, Outcome = new ParticipantOutcome.Done() },
				new ParticipantResult
				{
					Jid = Pn("5511910400002"),
					JidAlt = null,
					Outcome = new ParticipantOutcome.Refused
					{
						Code = 403,
						Invite = new GroupAddInvite { Code = "INVITE-5511910400002", ExpiresAt = s_at.AddHours(72) },
					},
				},
				new ParticipantResult { Jid = self, JidAlt = null, Outcome = new ParticipantOutcome.Refused { Code = 409, Invite = null } },
			],
			await client.AddParticipantsAsync(group, [Pn("5511910400001"), Pn("5511910400002"), self], ct)
		);
		Assert.Equal(Created(group, "groups-participants", "Team", Member(Pn("5511910400001"))), await client.GetGroupInfoAsync(group, ct));

		var done = new ParticipantResult { Jid = Pn("5511910400001"), JidAlt = null, Outcome = new ParticipantOutcome.Done() };
		Assert.Equal([done], await client.PromoteParticipantsAsync(group, [Pn("5511910400001")], ct));
		Assert.Equal(Created(group, "groups-participants", "Team", Member(Pn("5511910400001"), GroupRole.Admin)), await client.GetGroupInfoAsync(group, ct));

		Assert.Equal([done], await client.DemoteParticipantsAsync(group, [Pn("5511910400001")], ct));
		Assert.Equal(Created(group, "groups-participants", "Team", Member(Pn("5511910400001"))), await client.GetGroupInfoAsync(group, ct));

		// Removed, and not a member [inferred: 404].
		Assert.Equal(
			[
				done,
				new ParticipantResult { Jid = Pn("5511910400009"), JidAlt = null, Outcome = new ParticipantOutcome.Refused { Code = 404, Invite = null } },
			],
			await client.RemoveParticipantsAsync(group, [Pn("5511910400001"), Pn("5511910400009")], ct)
		);
		Assert.Equal(Created(group, "groups-participants", "Team"), await client.GetGroupInfoAsync(group, ct));

		Assert.Empty(await client.AddParticipantsAsync(group, [], ct));
	}

	[Fact(Timeout = Timeout)]
	public async Task SetGroupName_NotAnAdminOfALockedGroup_ThrowsWhatsAppErrorWithItsCode()
	{
		var ct = TestContext.Current.CancellationToken;
		var owner = await SessionSetup.ConnectedAsync(fixture, "groups-refused-a", ct);
		var member = await SessionSetup.ConnectedAsync(fixture, "groups-refused-b", ct);
		var group = (await owner.CreateGroupAsync("Team", [Self("groups-refused-b")], ct)).Group.Jid;
		await owner.SetGroupLockedAsync(group, locked: true, ct);

		var thrown = await Assert.ThrowsAsync<WhatsAppErrorException>(() => member.SetGroupNameAsync(group, "Mine", ct));

		// A refusal for lack of admin rights has no reason of its own: WhatsApp's code says it [observed: 401, #36 live run 2026-11-04].
		Assert.Equal((WhatsmeowErrorCode.WhatsAppError, 401), (thrown.ErrorCode, thrown.Code));
		Assert.Equal("Team", (await member.GetGroupInfoAsync(group, ct)).Name);
	}

	[Fact(Timeout = Timeout)]
	public async Task Settings_ChangedByAnAdmin_MembersSeeTheChange()
	{
		var ct = TestContext.Current.CancellationToken;
		var admin = await SessionSetup.ConnectedAsync(fixture, "groups-settings-a", ct);
		var member = await SessionSetup.ConnectedAsync(fixture, "groups-settings-b", ct);
		using var changes = new Received<GroupInfoChanged>(member);
		var group = (await admin.CreateGroupAsync("Team", [Self("groups-settings-b")], ct)).Group.Jid;
		var by = Self("groups-settings-a");

		await GroupChecks.SetSettingsAsync(admin, group, "Crew", "Plans for Monday", ct);

		Assert.Equal(GroupChecks.Settings("Crew", "Plans for Monday").Select(change => Changed("groups-settings-b", group, by, change)), await changes.NextAsync(4, ct));

		var expected = Created(group, "groups-settings-a", "Crew", Member(Self("groups-settings-b"))) with { Topic = "Plans for Monday", IsAnnounce = true, IsLocked = true };
		Assert.Equal(expected, await member.GetGroupInfoAsync(group, ct));

		// An empty topic removes it.
		await admin.SetGroupTopicAsync(group, topic: null, ct);
		Assert.Equal(Changed("groups-settings-b", group, by, new GroupChange.TopicChanged { Topic = null }), await changes.NextAsync(ct));
		Assert.Equal(expected with { Topic = null }, await member.GetGroupInfoAsync(group, ct));
	}

	[Fact(Timeout = Timeout)]
	public async Task InviteLink_ResetThenJoined_TheOldLinkStopsWorkingAndTheNewOneMakesTheSessionAMember()
	{
		var ct = TestContext.Current.CancellationToken;
		var admin = await SessionSetup.ConnectedAsync(fixture, "groups-invite-a", ct);
		var joiner = await SessionSetup.ConnectedAsync(fixture, "groups-invite-b", ct);
		using var adminChanges = new Received<GroupInfoChanged>(admin);
		using var joined = new Received<GroupJoined>(joiner);
		var group = (await admin.CreateGroupAsync("Team", [], ct)).Group.Jid;

		var link = await admin.GetGroupInviteLinkAsync(group, ct);
		Assert.StartsWith("https://chat.whatsapp.com/", link.ToString(), StringComparison.Ordinal);
		Assert.Equal(link, await admin.GetGroupInviteLinkAsync(group, ct));

		var reset = await admin.ResetGroupInviteLinkAsync(group, ct);
		Assert.NotEqual(link, reset);

		var old = await Assert.ThrowsAsync<WhatsmeowException>(() => joiner.JoinGroupWithLinkAsync(link, ct));
		Assert.Equal(WhatsmeowErrorCode.InviteLinkInvalid, old.ErrorCode);
		// A well formed code no group has, which WhatsApp refuses with 400 [observed: #36 live run 2026-11-04].
		var bogus = await Assert.ThrowsAsync<WhatsmeowException>(() => joiner.JoinGroupWithLinkAsync(new Uri("https://chat.whatsapp.com/NoSuchCode0000000000AB"), ct));
		Assert.Equal(WhatsmeowErrorCode.InviteLinkInvalid, bogus.ErrorCode);
		Assert.Empty(await joiner.GetGroupsAsync(ct));

		Assert.Equal(group, await joiner.JoinGroupWithLinkAsync(reset, ct));

		var expected = Created(group, "groups-invite-a", "Team", Member(Self("groups-invite-b")));
		var groups = await joiner.GetGroupsAsync(ct);
		_ = Assert.Single(groups);
		Assert.Equal(expected, groups[0]);

		// The joiner gets the group, and the members who joined, each from no sender, as WhatsApp sent them (ARCHITECTURE §14).
		Assert.Equal(
			new GroupJoined { SessionId = SessionId.From("groups-invite-b"), Group = expected, Sender = null, SenderAlt = null },
			await joined.NextAsync(ct)
		);
		Assert.Equal(
			Changed("groups-invite-a", group, sender: null, new GroupChange.ParticipantJoined { Jid = Self("groups-invite-b") }),
			await adminChanges.NextAsync(ct)
		);
	}

	[Fact(Timeout = Timeout)]
	public async Task LeaveGroup_TheSessionIsNoLongerAMember_AndTheMembersSeeItLeave()
	{
		var ct = TestContext.Current.CancellationToken;
		var admin = await SessionSetup.ConnectedAsync(fixture, "groups-leave-a", ct);
		var leaver = await SessionSetup.ConnectedAsync(fixture, "groups-leave-b", ct);
		using var adminChanges = new Received<GroupInfoChanged>(admin);
		var group = (await admin.CreateGroupAsync("Team", [Self("groups-leave-b")], ct)).Group.Jid;

		await leaver.LeaveGroupAsync(group, ct);

		Assert.Empty(await leaver.GetGroupsAsync(ct));
		var thrown = await Assert.ThrowsAsync<WhatsAppErrorException>(() => leaver.GetGroupInfoAsync(group, ct));
		Assert.Equal(403, thrown.Code);
		Assert.Equal(Created(group, "groups-leave-a", "Team"), await admin.GetGroupInfoAsync(group, ct));
		Assert.Equal(
			Changed("groups-leave-a", group, Self("groups-leave-b"), new GroupChange.ParticipantLeft { Jid = Self("groups-leave-b") }),
			await adminChanges.NextAsync(ct)
		);
	}

	[Fact(Timeout = Timeout)]
	public async Task GroupNotification_ByAnyoneForAGroupNeverListed_TheHandlerGetsWhoChangedWhat()
	{
		var ct = TestContext.Current.CancellationToken;
		var client = await SessionSetup.ConnectedAsync(fixture, "groups-notified", ct);
		using var changes = new Received<GroupInfoChanged>(client);
		var at = new DateTimeOffset(2026, 10, 30, 8, 0, 0, TimeSpan.Zero);

		_ = await fixture.Fake.DeliverGroupNotificationAsync(
			new DeliverGroupNotificationRequest
			{
				SessionId = "groups-notified",
				Id = "GN-NOTIFIED-1",
				Group = "120363109000000001@g.us",
				Sender = "910900001@lid",
				SenderPn = "5511910900001@s.whatsapp.net",
				Time = Timestamp.FromDateTimeOffset(at),
				Name = "Crew",
				Topic = "",
				Announce = false,
				Locked = true,
				Join = { "910900002@lid", "910900003@lid" },
				Leave = { "910900004@lid" },
				Promote = { "910900002@lid" },
				Demote = { "910900005@lid" },
			},
			cancellationToken: ct
		);
		// Only a change the contract has no case for: not delivered.
		_ = await fixture.Fake.DeliverGroupNotificationAsync(
			new DeliverGroupNotificationRequest
			{
				SessionId = "groups-notified",
				Id = "GN-NOTIFIED-2",
				Group = "120363109000000001@g.us",
				Time = Timestamp.FromDateTimeOffset(at),
				MembershipApproval = true,
			},
			cancellationToken: ct
		);
		// No sender.
		_ = await fixture.Fake.DeliverGroupNotificationAsync(
			new DeliverGroupNotificationRequest
			{
				SessionId = "groups-notified",
				Id = "GN-NOTIFIED-3",
				Group = "120363109000000001@g.us",
				Time = Timestamp.FromDateTimeOffset(at),
				Name = "Crew 2",
			},
			cancellationToken: ct
		);

		Assert.Equal(
			new GroupInfoChanged
			{
				SessionId = SessionId.From("groups-notified"),
				Group = Group("120363109000000001"),
				Sender = Lid("910900001"),
				SenderAlt = Pn("5511910900001"),
				Timestamp = at,
				Changes =
				[
					new GroupChange.Renamed { Name = "Crew" },
					new GroupChange.TopicChanged { Topic = null },
					new GroupChange.AnnounceChanged { IsAnnounce = false },
					new GroupChange.LockedChanged { IsLocked = true },
					new GroupChange.ParticipantJoined { Jid = Lid("910900002") },
					new GroupChange.ParticipantJoined { Jid = Lid("910900003") },
					new GroupChange.ParticipantLeft { Jid = Lid("910900004") },
					new GroupChange.ParticipantPromoted { Jid = Lid("910900002") },
					new GroupChange.ParticipantDemoted { Jid = Lid("910900005") },
				],
			},
			await changes.NextAsync(ct)
		);
		Assert.Equal(
			Changed("groups-notified", Group("120363109000000001"), sender: null, new GroupChange.Renamed { Name = "Crew 2" }) with { Timestamp = at },
			await changes.NextAsync(ct)
		);

		// Each was acked to WhatsApp once the sidecar recorded it, the one it did not deliver too (invariant 2).
		var acks = new List<Ack>();
		ulong after = 0;
		for (var i = 0; i < 3; i++)
		{
			var sent = (await fixture.Fake.WaitForSentAsync(new WaitForSentRequest { SessionId = "groups-notified", After = after }, cancellationToken: ct)).Sent;
			acks.Add(sent.Ack);
			after = sent.Index;
		}

		Assert.Equal(
			[
				new Ack { Class = "notification", Id = "GN-NOTIFIED-1", Type = "w:gp2" },
				new Ack { Class = "notification", Id = "GN-NOTIFIED-2", Type = "w:gp2" },
				new Ack { Class = "notification", Id = "GN-NOTIFIED-3", Type = "w:gp2" },
			],
			acks
		);
	}

	[Fact(Timeout = Timeout)]
	public async Task GroupNotification_SentTwice_TheTwoEventsAreEqual()
	{
		var ct = TestContext.Current.CancellationToken;
		var client = await SessionSetup.ConnectedAsync(fixture, "groups-twice", ct);
		using var changes = new Received<GroupInfoChanged>(client);
		var notification = new DeliverGroupNotificationRequest
		{
			SessionId = "groups-twice",
			Group = "120363112000000001@g.us",
			Sender = "911200001@lid",
			Time = Timestamp.FromDateTimeOffset(s_at),
			Name = "Crew",
			Join = { "911200002@lid", "911200003@lid" },
		};

		// As WhatsApp sends one again that was not acked: nothing tells the two apart.
		_ = await fixture.Fake.DeliverGroupNotificationAsync(notification, cancellationToken: ct);
		_ = await fixture.Fake.DeliverGroupNotificationAsync(notification, cancellationToken: ct);

		var first = await changes.NextAsync(ct);
		var again = await changes.NextAsync(ct);
		Assert.Equal(
			Changed(
				"groups-twice",
				Group("120363112000000001"),
				Lid("911200001"),
				new GroupChange.Renamed { Name = "Crew" },
				new GroupChange.ParticipantJoined { Jid = Lid("911200002") },
				new GroupChange.ParticipantJoined { Jid = Lid("911200003") }
			),
			first
		);
		Assert.Equal(first, again);
		Assert.Equal(first.GetHashCode(), again.GetHashCode());
	}

	[Fact(Timeout = Timeout)]
	public async Task AddParticipants_TheSessionIsAdded_ItGetsGroupJoinedWithTheGroupsInfo()
	{
		var ct = TestContext.Current.CancellationToken;
		var admin = await SessionSetup.ConnectedAsync(fixture, "groups-added-a", ct);
		var added = await SessionSetup.ConnectedAsync(fixture, "groups-added-b", ct);
		using var joined = new Received<GroupJoined>(added);
		var group = (await admin.CreateGroupAsync("Team", [], ct)).Group.Jid;

		_ = await admin.AddParticipantsAsync(group, [Self("groups-added-b")], ct);

		Assert.Equal(
			new GroupJoined
			{
				SessionId = SessionId.From("groups-added-b"),
				Group = Created(group, "groups-added-a", "Team", Member(Self("groups-added-b"))),
				Sender = Self("groups-added-a"),
				SenderAlt = null,
			},
			await joined.NextAsync(ct)
		);
	}

	[Fact(Timeout = Timeout)]
	public async Task LidAddressedGroup_ParticipantsCarryBothFormsWhenKnown()
	{
		var ct = TestContext.Current.CancellationToken;
		var client = await SessionSetup.ConnectedAsync(fixture, "groups-lid", ct);
		var self = Self("groups-lid");
		_ = await fixture.Fake.RegisterUserAsync(new RegisterUserRequest { PhoneJid = "5511911100001@s.whatsapp.net", Lid = "911100001@lid" }, cancellationToken: ct);
		_ = await fixture.Fake.AddGroupAsync(
			new AddGroupRequest
			{
				Jid = "120363111000000001@g.us",
				Name = "Team",
				Owner = "5511911100001@s.whatsapp.net",
				LidAddressed = true,
				Members =
				{
					new AddGroupRequest.Types.Member { Jid = "5511911100001@s.whatsapp.net", Type = "superadmin" },
					new AddGroupRequest.Types.Member { Jid = self.ToString() },
				},
			},
			cancellationToken: ct
		);

		// The session has no LID in the fake's directory, so WhatsApp answers it by phone number.
		Assert.Equal(
			new GroupInfo
			{
				Jid = Group("120363111000000001"),
				Name = "Team",
				Topic = null,
				Owner = Lid("911100001"),
				OwnerAlt = Pn("5511911100001"),
				CreatedAt = null,
				IsAnnounce = false,
				IsLocked = false,
				Participants = [Member(Lid("911100001"), GroupRole.SuperAdmin, Pn("5511911100001")), Member(self)],
			},
			await client.GetGroupInfoAsync(Group("120363111000000001"), ct)
		);
	}

	[Fact(Timeout = Timeout)]
	public async Task GetGroupInfo_AParticipantTheClientCannotRead_ThrowsInternalNamingTheField()
	{
		var ct = TestContext.Current.CancellationToken;
		var client = await SessionSetup.ConnectedAsync(fixture, "groups-unreadable", ct);
		// A member with no server: the sidecar sends its JID empty, which the client cannot read.
		_ = await fixture.Fake.AddGroupAsync(
			new AddGroupRequest
			{
				Jid = "120363113000000001@g.us",
				Name = "Broken",
				Members =
				{
					new AddGroupRequest.Types.Member { Jid = "5511911300001@" },
					new AddGroupRequest.Types.Member { Jid = Self("groups-unreadable").ToString() },
				},
			},
			cancellationToken: ct
		);

		// The sidecar's fault, not the caller's: no validation exception, and the field named, not its value.
		var error = await Assert.ThrowsAsync<WhatsmeowException>(() => client.GetGroupInfoAsync(Group("120363113000000001"), ct));
		Assert.Equal(WhatsmeowErrorCode.Internal, error.ErrorCode);
		Assert.Contains("GroupParticipant.jid", error.Message, StringComparison.Ordinal);
		Assert.DoesNotContain("5511911300001", error.ToString(), StringComparison.Ordinal);
	}

	/// <summary>
	/// #122: a member the client cannot read, here one without a server, is left out of a group change, and a change
	/// left with none is not recorded; whatsmeow keeps a created group's participant without a jid
	/// (group.go:718, 767, 804-806).
	/// </summary>
	[Fact(Timeout = Timeout)]
	public async Task GroupNotification_AMemberTheClientCannotRead_IsLeftOut()
	{
		var ct = TestContext.Current.CancellationToken;
		const string Session = "groups-member-unreadable";
		var client = await SessionSetup.ConnectedAsync(fixture, Session, ct);
		using var changes = new Received<GroupInfoChanged>(client);
		var at = new DateTimeOffset(2026, 10, 30, 8, 0, 0, TimeSpan.Zero);

		_ = await fixture.Fake.DeliverGroupNotificationAsync(
			new DeliverGroupNotificationRequest
			{
				SessionId = Session,
				Id = "GN-UNREADABLE-1",
				Group = "120363114000000001@g.us",
				Time = Timestamp.FromDateTimeOffset(at),
				Leave = { "5511911400001@" },
			},
			cancellationToken: ct
		);
		_ = await fixture.Fake.DeliverGroupNotificationAsync(
			new DeliverGroupNotificationRequest
			{
				SessionId = Session,
				Id = "GN-UNREADABLE-2",
				Group = "120363114000000001@g.us",
				Time = Timestamp.FromDateTimeOffset(at),
				Join = { "5511911400002@", "911400003@lid" },
			},
			cancellationToken: ct
		);

		Assert.Equal(
			new GroupInfoChanged
			{
				SessionId = SessionId.From(Session),
				Group = Group("120363114000000001"),
				Sender = null,
				SenderAlt = null,
				Timestamp = at,
				Changes = [new GroupChange.ParticipantJoined { Jid = Lid("911400003") }],
			},
			await changes.NextAsync(ct)
		);
		Assert.DoesNotContain(fixture.Logs.Named("EventUnreadable"), r => r.Message.Contains(Session, StringComparison.Ordinal));
	}
}
