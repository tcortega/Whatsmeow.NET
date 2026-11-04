using System.Security.Cryptography;
using Whatsmeow.Events;
using Whatsmeow.Groups;
using Whatsmeow.IntegrationTests.Groups;
using Whatsmeow.Messaging;

namespace Whatsmeow.LiveTests;

/// <summary>
/// Groups (#34) and group messages (#30) through real WhatsApp, on the run's one group with the peer (<see cref="LiveGroup"/>):
/// what a member sees of an admin's changes, an invite link, a leave, a non-admin's refusals, and mentions, replies and
/// revokes in the group. Each is compared with the fake-mode tests' records (<see cref="GroupChecks"/>,
/// <see cref="LiveChecks.Received"/>), and what #30 and #34 left [unknown] goes to the log. Each test leaves the group as
/// it found it: its settings off and the peer a member.
/// </summary>
[Collection(nameof(Live))]
public sealed class LiveGroupTests(LiveSidecar live, LivePeer peer, LiveGroup group) : IClassFixture<LiveGroup>
{
	/// <summary>
	/// A group test's timeout: a session's backlog on first use (<see cref="LiveFactAttribute.CatchUpTimeout"/>), the
	/// group's creation on first use (closing what an earlier run left, then waits of 60 s), its own work (at most two
	/// 60 s waits), and putting the group back (at most five restores of 30 s each, <see cref="LiveRestore.Bound"/>),
	/// with room to spare.
	/// </summary>
	private const int GroupTimeout = 600_000 + LiveFactAttribute.CatchUpTimeout;

	// The letters of an invite code [inferred: the links the live tests got].
	private const string CodeLetters = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789";

	/// <summary>
	/// The create answer is the group's info, as GetGroupInfo reads it: the run's name, no topic or settings, the live
	/// account its owner and super admin and the peer a member, each in the form WhatsApp chose (the addressing mode).
	/// Whether the creator and the peer get <see cref="GroupJoined"/> goes to the log.
	/// </summary>
	[LiveFact(NeedsPeer = true, Timeout = GroupTimeout)]
	public async Task Live_CreateAGroupWithThePeer_TheAnswerIsItsInfo_WithTheLiveAccountItsOwnerAndThePeerAMember()
	{
		var ct = TestContext.Current.CancellationToken;
		var creation = await group.CreatedAsync(ct);
		var liveAccount = await live.AccountAsync(ct);
		var peerAccount = await peer.AccountAsync(ct);
		var info = creation.Created.Group;

		var result = LiveChecks.Redacting(() => Assert.Single(creation.Created.Participants));
		Assert.True(LiveChecks.Names(peerAccount, result.Jid), $"the create answer lists a {LiveChecks.Form(result.Jid)} that is not the peer");
		LiveChecks.Equal(new ParticipantResult { Jid = result.Jid, JidAlt = result.JidAlt, Outcome = new ParticipantOutcome.Done() }, result);

		Assert.True(info.Owner is { } owner && LiveChecks.Names(liveAccount, owner), $"the group's owner is a {LiveChecks.Form(info.Owner)} that is not the live account");
		var self = LiveChecks.Redacting(() => Assert.Single(info.Participants, p => LiveChecks.Names(liveAccount, p.Jid)));
		var member = LiveChecks.Redacting(() => Assert.Single(info.Participants, p => LiveChecks.Names(peerAccount, p.Jid)));
		LiveChecks.Equal(
			info with
			{
				Name = creation.Name,
				Topic = null,
				IsAnnounce = false,
				IsLocked = false,
				Participants = [self with { Role = GroupRole.SuperAdmin }, member with { Role = GroupRole.Member }],
			},
			info
		);
		LiveChecks.Equal(info, creation.InfoAfter);
	}

	/// <summary>
	/// The live session sets the name, the topic, announce and locked: the peer gets each as a change by the live account,
	/// in that order, and reads them in the group's info. Each is set back after to what it was, whatever happened.
	/// </summary>
	[LiveFact(NeedsPeer = true, Timeout = GroupTimeout)]
	public async Task Live_SettingsChangedByTheAdmin_ThePeerSeesEachChange()
	{
		var ct = TestContext.Current.CancellationToken;
		var jid = await group.WithPeerAsync(ct);
		var client = await live.LiveAsync(ct);
		var other = await peer.ConnectedAsync(live, ct);
		var liveAccount = await live.AccountAsync(ct);
		var before = await client.GetGroupInfoAsync(jid, ct);
		var name = LiveGroup.RenamedBy(before.Name, "crew");
		var settings = GroupChecks.Settings(name, "wmn plans for Monday");
		var since = LiveChecks.Since();
		// Only this test's changes: a late one from a test before it has another value or an earlier time.
		using var changes = new LiveEvents<GroupInfoChanged>(other, c => c.Group == jid && LiveChecks.IsSince(since, c.Timestamp) && c.Changes.Any(settings.Contains));

		await LiveRestore.AfterAsync(
			live,
			async () =>
			{
				await GroupChecks.SetSettingsAsync(client, jid, name, "wmn plans for Monday", ct).ConfigureAwait(false);
				var seen = await changes.NextAsync(4, LiveChecks.Watch, ct).ConfigureAwait(false);
				live.Write($"live: the peer got {seen.Count} group changes for 4 settings, from a {LiveChecks.Form(seen.Count > 0 ? seen[0].Sender : null)}: {Kinds(seen)}");

				LiveChecks.Equal(4, seen.Count);
				var by = seen[0];
				Assert.True(by.Sender is { } sender && LiveChecks.Names(liveAccount, sender), $"the change came from a {LiveChecks.Form(by.Sender)} that is not the live account");
				LiveChecks.Equal(settings.Select((change, i) => GroupChecks.Changed(other.SessionId, jid, by.Sender, by.SenderAlt, seen[i].Timestamp, change)), seen);
				var info = await other.GetGroupInfoAsync(jid, ct).ConfigureAwait(false);
				LiveChecks.Equal((name, "wmn plans for Monday", true, true), (info.Name, info.Topic, info.IsAnnounce, info.IsLocked));
			},
			SetBack(client, jid, "lock", i => i.IsLocked, before.IsLocked, (locked, t) => client.SetGroupLockedAsync(jid, locked, t)),
			SetBack(client, jid, "announce", i => i.IsAnnounce, before.IsAnnounce, (announce, t) => client.SetGroupAnnounceAsync(jid, announce, t)),
			SetBack(client, jid, "topic", i => i.Topic, before.Topic, (topic, t) => client.SetGroupTopicAsync(jid, topic, t)),
			SetBack(client, jid, "name", i => i.Name, before.Name, (original, t) => client.SetGroupNameAsync(jid, original, t))
		);
	}

	/// <summary>
	/// The group locked, the peer, no admin, sets its name, who may add members and the disappearing timer, and promotes
	/// itself: WhatsApp's code for each goes to the log (the [unknown]s #34 and #104 to #106 left), then the name and the
	/// add mode are checked as the fake-mode tests check them, refused with WhatsApp's code. Whatever WhatsApp took is
	/// undone after: the peer's role, the name, the add mode, the timer (off, unless WhatsApp refused the peer's) and the
	/// lock, as it was.
	/// </summary>
	[LiveFact(NeedsPeer = true, Timeout = GroupTimeout)]
	public async Task Live_TheNonAdminPeerChangesTheLockedGroup_WhatsAppRefusesWithItsCode()
	{
		var ct = TestContext.Current.CancellationToken;
		var jid = await group.WithPeerAsync(ct);
		var client = await live.LiveAsync(ct);
		var other = await peer.ConnectedAsync(live, ct);
		var (to, _) = await peer.AccountAsync(ct);
		var peerAccount = await peer.AccountAsync(ct);
		var before = await client.GetGroupInfoAsync(jid, ct);
		var mode = before.MemberAddMode;
		live.Write($"live: the group's member add mode is {mode?.ToString() ?? "unknown"}");
		// The timer is reset only when the peer's may have taken: a needless reset is a change a later test could count.
		var timerTried = false;
		Exception? timer = null;

		async Task DemotePeerAsync(CancellationToken token)
		{
			var info = await client.GetGroupInfoAsync(jid, token).ConfigureAwait(false);
			var admins = info.Participants.Where(p => LiveChecks.Names(peerAccount, p.Jid) && p.Role is not GroupRole.Member).Select(p => p.Jid).ToList();
			if (admins.Count > 0)
				_ = await client.DemoteParticipantsAsync(jid, admins, token).ConfigureAwait(false);
		}

		Task TimerOffAsync(CancellationToken token) =>
			timerTried && timer is not WhatsAppErrorException ? client.SetDisappearingTimerAsync(jid, TimeSpan.Zero, cancellationToken: token) : Task.CompletedTask;

		await LiveRestore.AfterAsync(
			live,
			async () =>
			{
				await client.SetGroupLockedAsync(jid, locked: true, ct).ConfigureAwait(false);
				var renamed = await Record.ExceptionAsync(() => other.SetGroupNameAsync(jid, LiveGroup.RenamedBy(before.Name, "not mine"), ct)).ConfigureAwait(false);
				live.Write($"live: the non-admin peer's rename of a locked group: {Outcome(renamed)}");
				IReadOnlyList<ParticipantResult> promoted = [];
				var promoting = await Record.ExceptionAsync(async () => promoted = await other.PromoteParticipantsAsync(jid, [to], ct).ConfigureAwait(false)).ConfigureAwait(false);
				live.Write(
					$"live: the non-admin peer promoting itself: {(promoting is null ? string.Join(", ", promoted.Select(r => r.Outcome is ParticipantOutcome.Refused refused ? $"refused with {refused.Code}" : r.Outcome.GetType().Name)) : Outcome(promoting))}"
				);

				var addMode = await Record.ExceptionAsync(() => other.SetGroupMemberAddModeAsync(jid, mode is GroupMemberAddMode.AllMembers ? GroupMemberAddMode.AdminsOnly : GroupMemberAddMode.AllMembers, ct)).ConfigureAwait(false);
				live.Write($"live: the non-admin peer setting who may add members: {Outcome(addMode)}");
				timerTried = true;
				timer = await Record.ExceptionAsync(() => other.SetDisappearingTimerAsync(jid, TimeSpan.FromDays(7), cancellationToken: ct)).ConfigureAwait(false);
				live.Write($"live: the non-admin peer setting the group's disappearing timer to 7 days: {Outcome(timer)}");

				var thrown = Assert.IsType<WhatsAppErrorException>(renamed);
				LiveChecks.Equal((WhatsmeowErrorCode.WhatsAppError, 401), (thrown.ErrorCode, thrown.Code));
				LiveChecks.Equal(before.Name, (await other.GetGroupInfoAsync(jid, ct).ConfigureAwait(false)).Name);
				var refusedMode = Assert.IsType<WhatsAppErrorException>(addMode);
				LiveChecks.Equal((WhatsmeowErrorCode.WhatsAppError, 401), (refusedMode.ErrorCode, refusedMode.Code));
				LiveChecks.Equal(mode, (await other.GetGroupInfoAsync(jid, ct).ConfigureAwait(false)).MemberAddMode);
			},
			// What WhatsApp took, if it took any, is undone, so the later tests see a non-admin peer and the run's name.
			("demote the peer", DemotePeerAsync),
			SetBack(client, jid, "name", i => i.Name, before.Name, (name, t) => client.SetGroupNameAsync(jid, name, t)),
			SetBack(client, jid, "member add mode", i => i.MemberAddMode, mode, (m, t) => m is { } known ? client.SetGroupMemberAddModeAsync(jid, known, t) : Task.CompletedTask),
			("turn the group's timer off", TimerOffAsync),
			SetBack(client, jid, "lock", i => i.IsLocked, before.IsLocked, (locked, t) => client.SetGroupLockedAsync(jid, locked, t))
		);
	}

	/// <summary>
	/// The peer leaves; no member, it reads the group from its link without joining, what it is shown going to the log
	/// (the members a non-member sees, and their forms, are [unknown]). It leaves rather than being removed: WhatsApp
	/// refuses a member an admin removed the link's group with 401 (ARCHITECTURE §14). The live session resets
	/// the link: the old one no longer works, and the peer joins by the new one, gets <see cref="GroupJoined"/> from no
	/// sender, and the live session sees it join, as in the fake-mode invite and preview tests.
	/// </summary>
	[LiveFact(NeedsPeer = true, Timeout = GroupTimeout)]
	public async Task Live_InviteLinkResetThenJoined_TheOldLinkStopsWorking_AndThePeerJoinsByTheNewOne()
	{
		var ct = TestContext.Current.CancellationToken;
		var jid = await group.WithPeerAsync(ct);
		var client = await live.LiveAsync(ct);
		var other = await peer.ConnectedAsync(live, ct);
		var peerAccount = await peer.AccountAsync(ct);

		await LiveRestore.AfterAsync(
			live,
			async () =>
			{
				await other.LeaveGroupAsync(jid, ct).ConfigureAwait(false);
				var link = await client.GetGroupInviteLinkAsync(jid, ct).ConfigureAwait(false);
				var adminView = await client.GetGroupInfoAsync(jid, ct).ConfigureAwait(false);
				var preview = await other.GetGroupInfoFromLinkAsync(link, ct).ConfigureAwait(false);
				live.Write(
					$"live: the peer, no member, read the group from its link: {preview.Participants.Length} of {adminView.Participants.Length} members listed, as {LiveGroup.Members(preview)}; "
						+ $"the owner a {LiveChecks.Form(preview.Owner)}, the add mode {preview.MemberAddMode?.ToString() ?? "unknown"}"
				);
				Assert.True((await other.GetGroupsAsync(ct).ConfigureAwait(false)).All(g => g.Jid != jid), "reading the group from its link made the peer a member");
				var reset = await client.ResetGroupInviteLinkAsync(jid, ct).ConfigureAwait(false);
				Assert.NotEqual(link, reset);
				var old = await Assert.ThrowsAsync<WhatsmeowException>(() => other.JoinGroupWithLinkAsync(link, ct)).ConfigureAwait(false);
				live.Write($"live: the peer joining by the link from before the reset: {Outcome(old)}");
				LiveChecks.Equal(WhatsmeowErrorCode.InviteLinkInvalid, old.ErrorCode);

				var joined = other.WaitForAsync<GroupJoined>(j => j.Group.Jid == jid, LiveChecks.Watch, ct);
				var seen = client.WaitForAsync<GroupInfoChanged>(c => c.Group == jid && c.Changes.Any(x => x is GroupChange.ParticipantJoined p && LiveChecks.Names(peerAccount, p.Jid)), LiveChecks.Watch, ct);
				LiveChecks.Equal(jid, await other.JoinGroupWithLinkAsync(reset, ct).ConfigureAwait(false));

				var join = await LiveChecks.WithinAsync(joined, "the peer's GroupJoined").ConfigureAwait(false);
				// As the fake-mode invite test: a join by link names no sender [observed: #36 live run of 2026-11-04 at dfa2e57].
				live.Write($"live: the peer's GroupJoined by the link came from a {LiveChecks.Form(join.Sender)}, its phone number {LiveChecks.Form(join.SenderAlt)}");
				Assert.Null(join.Sender);
				Assert.Null(join.SenderAlt);
				var change = await LiveChecks.WithinAsync(seen, "the peer's join, seen by the live session").ConfigureAwait(false);
				live.Write($"live: the live session saw the peer join, from a {LiveChecks.Form(change.Sender)}, the member a {LiveChecks.Form(change.Changes.OfType<GroupChange.ParticipantJoined>().First().Jid)}");
				var joiner = change.Changes.OfType<GroupChange.ParticipantJoined>().First().Jid;
				// As the fake-mode invite test: the join by link comes from no sender [observed: #36 live run of 2026-11-04 at 1519d67].
				LiveChecks.Equal(GroupChecks.Changed(client.SessionId, jid, sender: null, senderAlt: null, change.Timestamp, new GroupChange.ParticipantJoined { Jid = joiner }), change);

				// The fake-mode preview test's check, last, so a difference the log shows never hides the rest: a non-member reads
				// the group as its admin does, with no member add mode [observed: #36 live runs of 2026-11-04 at dfa2e57 and 1519d67].
				LiveChecks.Equal(adminView with { MemberAddMode = null }, preview);
			},
			// A failure above may leave the peer out; the reset link is the group's now.
			AddPeerBack()
		);
	}

	/// <summary>
	/// The peer leaves: it is in none of its groups' lists, reading the group is refused with WhatsApp's code, and the
	/// live session sees it leave, as in the fake-mode leave test. The live session, its admin, adds it back after.
	/// </summary>
	[LiveFact(NeedsPeer = true, Timeout = GroupTimeout)]
	public async Task Live_ThePeerLeaves_ItIsNoLongerAMember_AndTheLiveSessionSeesItLeave()
	{
		var ct = TestContext.Current.CancellationToken;
		var jid = await group.WithPeerAsync(ct);
		var client = await live.LiveAsync(ct);
		var other = await peer.ConnectedAsync(live, ct);
		var peerAccount = await peer.AccountAsync(ct);
		var left = client.WaitForAsync<GroupInfoChanged>(c => c.Group == jid && c.Changes.Any(x => x is GroupChange.ParticipantLeft p && LiveChecks.Names(peerAccount, p.Jid)), LiveChecks.Watch, ct);

		await LiveRestore.AfterAsync(
			live,
			async () =>
			{
				await other.LeaveGroupAsync(jid, ct).ConfigureAwait(false);
				Assert.True((await other.GetGroupsAsync(ct).ConfigureAwait(false)).All(g => g.Jid != jid), "the peer still lists the group it left");
				var thrown = await Assert.ThrowsAsync<WhatsAppErrorException>(() => other.GetGroupInfoAsync(jid, ct)).ConfigureAwait(false);
				live.Write($"live: the peer reading the group it left: {Outcome(thrown)}");
				LiveChecks.Equal(403, thrown.Code);
				var change = await LiveChecks.WithinAsync(left, "the peer's leave, seen by the live session").ConfigureAwait(false);
				var leaver = change.Changes.OfType<GroupChange.ParticipantLeft>().First().Jid;
				Assert.True(change.Sender is { } sender && LiveChecks.Names(peerAccount, sender), $"the leave came from a {LiveChecks.Form(change.Sender)} that is not the peer");
				LiveChecks.Equal(GroupChecks.Changed(client.SessionId, jid, change.Sender, change.SenderAlt, change.Timestamp, new GroupChange.ParticipantLeft { Jid = leaver }), change);
			},
			AddPeerBack()
		);
	}

	/// <summary>
	/// The live session changes who may add members: the peer gets the change by the live account, as in the fake-mode
	/// add mode test, and reads the new mode. What the notification carried goes to the log, by kind, with no JID. The
	/// mode is set back after, whatever happened.
	/// </summary>
	[LiveFact(NeedsPeer = true, Timeout = GroupTimeout)]
	public async Task Live_TheAdminSetsWhoMayAddMembers_ThePeerSeesTheChange()
	{
		var ct = TestContext.Current.CancellationToken;
		var jid = await group.WithPeerAsync(ct);
		var client = await live.LiveAsync(ct);
		var other = await peer.ConnectedAsync(live, ct);
		var liveAccount = await live.AccountAsync(ct);
		var mode = (await client.GetGroupInfoAsync(jid, ct)).MemberAddMode;
		Assert.True(mode is not null, "WhatsApp did not say the group's member add mode, so the test could not set it back");
		var next = mode is GroupMemberAddMode.AdminsOnly ? GroupMemberAddMode.AllMembers : GroupMemberAddMode.AdminsOnly;
		var since = LiveChecks.Since();
		using var changes = new LiveEvents<GroupInfoChanged>(other, c => c.Group == jid && LiveChecks.IsSince(since, c.Timestamp) && c.Changes.Contains(new GroupChange.MemberAddModeChanged { Mode = next }));

		await LiveRestore.AfterAsync(
			live,
			async () =>
			{
				await client.SetGroupMemberAddModeAsync(jid, next, ct).ConfigureAwait(false);
				var seen = await changes.NextAsync(1, LiveChecks.Watch, ct).ConfigureAwait(false);
				live.Write($"live: the add mode went from {mode} to {next}; the peer got {Kinds(seen)}");

				var by = LiveChecks.Redacting(() => Assert.Single(seen));
				Assert.True(by.Sender is { } sender && LiveChecks.Names(liveAccount, sender), $"the change came from a {LiveChecks.Form(by.Sender)} that is not the live account");
				LiveChecks.Equal(GroupChecks.Changed(other.SessionId, jid, by.Sender, by.SenderAlt, by.Timestamp, new GroupChange.MemberAddModeChanged { Mode = next }), by);
				LiveChecks.Equal(next, (await other.GetGroupInfoAsync(jid, ct).ConfigureAwait(false)).MemberAddMode);
			},
			SetBack(client, jid, "member add mode", i => i.MemberAddMode, mode, (m, t) => client.SetGroupMemberAddModeAsync(jid, m!.Value, t))
		);
	}

	/// <summary>
	/// The live session sets the group's disappearing timer to 7 days, then off: the peer gets each as a change by the
	/// live account, as in the fake-mode group timer test. The timer is turned off after, whatever happened.
	/// </summary>
	[LiveFact(NeedsPeer = true, Timeout = GroupTimeout)]
	public async Task Live_TheAdminSetsTheGroupsTimerToSevenDaysThenOff_ThePeerSeesEachChange()
	{
		var ct = TestContext.Current.CancellationToken;
		var jid = await group.WithPeerAsync(ct);
		var client = await live.LiveAsync(ct);
		var other = await peer.ConnectedAsync(live, ct);
		var liveAccount = await live.AccountAsync(ct);
		var since = LiveChecks.Since();
		var seen = new List<(TimeSpan Timer, IReadOnlyList<GroupInfoChanged> Seen)>();
		await LiveRestore.AfterAsync(
			live,
			async () =>
			{
				foreach (var timer in new[] { TimeSpan.FromDays(7), TimeSpan.Zero })
				{
					// Only this step's change: a late one from a test before it has another value or an earlier time.
					using var changes = new LiveEvents<GroupInfoChanged>(
						other,
						c => c.Group == jid && LiveChecks.IsSince(since, c.Timestamp) && c.Changes.Contains(new GroupChange.DisappearingTimerChanged { Timer = timer })
					);
					await client.SetDisappearingTimerAsync(jid, timer, cancellationToken: ct).ConfigureAwait(false);
					var got = await changes.NextAsync(1, LiveChecks.Watch, ct).ConfigureAwait(false);
					live.Write($"live: the group's timer set to {timer.TotalDays:F0} days; the peer got {Kinds(got)}");
					seen.Add((timer, got));
				}
			},
			("turn the group's timer off", t => client.SetDisappearingTimerAsync(jid, TimeSpan.Zero, cancellationToken: t))
		);

		foreach (var (timer, got) in seen)
		{
			var by = LiveChecks.Redacting(() => Assert.Single(got));
			Assert.True(by.Sender is { } sender && LiveChecks.Names(liveAccount, sender), $"the change came from a {LiveChecks.Form(by.Sender)} that is not the live account");
			LiveChecks.Equal(GroupChecks.Changed(other.SessionId, jid, by.Sender, by.SenderAlt, by.Timestamp, new GroupChange.DisappearingTimerChanged { Timer = timer }), by);
		}
	}

	/// <summary>
	/// A well formed invite code no group has: what WhatsApp answers a join and a preview with it goes to the log, and
	/// both are refused as an invalid link. Each answer is 400 [observed: #36 and #120 live runs 2026-11-04], which the
	/// sidecar maps (#120).
	/// </summary>
	[LiveFact(NeedsPeer = true)]
	public async Task Live_AWellFormedUnknownInviteCode_IsRefusedAsAnInvalidLink_ByAJoinAndAPreview()
	{
		var ct = TestContext.Current.CancellationToken;
		var other = await peer.ConnectedAsync(live, ct);
		var link = new Uri($"https://chat.whatsapp.com/{RandomNumberGenerator.GetString(CodeLetters, 22)}");

		var join = await Assert.ThrowsAnyAsync<WhatsmeowException>(() => other.JoinGroupWithLinkAsync(link, ct));
		live.Write($"live: joining by a well formed unknown invite code: {Answer(join)}");
		var preview = await Assert.ThrowsAnyAsync<WhatsmeowException>(() => other.GetGroupInfoFromLinkAsync(link, ct));
		live.Write($"live: reading the group of a well formed unknown invite code: {Answer(preview)}");

		LiveChecks.Equal((WhatsmeowErrorCode.InviteLinkInvalid, WhatsmeowErrorCode.InviteLinkInvalid), (join.ErrorCode, preview.ErrorCode));
	}

	/// <summary>
	/// #120: a malformed invite code, one too short and one of the right length with characters a code never has
	/// [inferred: the links the live tests got]: what WhatsApp answers a join and a preview with it goes to the log, and
	/// both are refused as an invalid link. Each answer is 400, as for an unknown code [observed: #120 live run
	/// 2026-11-04, live sidecar.log].
	/// </summary>
	[LiveFact(NeedsPeer = true)]
	public async Task Live_AMalformedInviteCode_IsRefusedAsAnInvalidLink_ByAJoinAndAPreview()
	{
		var ct = TestContext.Current.CancellationToken;
		var other = await peer.ConnectedAsync(live, ct);

		foreach (var (form, code) in new[] { ("too short", RandomNumberGenerator.GetString(CodeLetters, 10)), ("of foreign characters", new string('!', 22)) })
		{
			var link = new Uri($"https://chat.whatsapp.com/{code}");
			var join = await Assert.ThrowsAnyAsync<WhatsmeowException>(() => other.JoinGroupWithLinkAsync(link, ct));
			live.Write($"live: joining by an invite code {form}: {Answer(join)}");
			var preview = await Assert.ThrowsAnyAsync<WhatsmeowException>(() => other.GetGroupInfoFromLinkAsync(link, ct));
			live.Write($"live: reading the group of an invite code {form}: {Answer(preview)}");
			LiveChecks.Equal((WhatsmeowErrorCode.InviteLinkInvalid, WhatsmeowErrorCode.InviteLinkInvalid), (join.ErrorCode, preview.ErrorCode));
		}
	}

	/// <summary>
	/// #30: a mention of the peer in the group by its LID, then by its phone number, each with the user in the text as
	/// WhatsApp clients write it: the peer gets each mention as sent. The group's addressing mode goes to the log, since
	/// the question is about a LID-addressed group.
	/// </summary>
	[LiveFact(NeedsPeer = true, Timeout = GroupTimeout)]
	public async Task Live_AMentionInTheGroupByEachAddressForm_ThePeerGetsItAsSent()
	{
		var ct = TestContext.Current.CancellationToken;
		var jid = await group.WithPeerAsync(ct);
		await MentionBothFormsAsync(jid, ct);
	}

	/// <summary>#30, the owner at the peer's phone: the same two mentions, and whether each notified the peer as a mention.</summary>
	[LiveFact(NeedsPeer = true, NeedsOwner = true, Timeout = GroupTimeout + 300_000)]
	public async Task Live_AMentionInTheGroupByEachAddressForm_TheOwnerSaysWhetherEachNotifiedThePeer()
	{
		var ct = TestContext.Current.CancellationToken;
		var jid = await group.WithPeerAsync(ct);
		await MentionBothFormsAsync(jid, ct);

		_ = await live.AskOwnerAsync(
			"On the peer's phone, the group \"wmn live ...\": the messages \"mention 1 of 2 (LID)\" and \"mention 2 of 2 (phone number)\". Did each notify the peer as a mention (the @ mark on the chat, or a mention notification), and does each show the peer's name highlighted?",
			ct
		);
	}

	/// <summary>
	/// #30: a reply in the group to the live account's own message, its quote naming no sender, so the sidecar names the
	/// account: the peer gets the quote as the live account's, in the form that goes to the log with the group's
	/// addressing mode.
	/// </summary>
	[LiveFact(NeedsPeer = true, Timeout = GroupTimeout)]
	public async Task Live_AReplyInTheGroupToItsOwnMessage_ThePeerGetsTheQuoteAsTheLiveAccounts()
	{
		var ct = TestContext.Current.CancellationToken;
		var jid = await group.WithPeerAsync(ct);
		await ReplyToOwnAsync(jid, ct);
	}

	/// <summary>#30, the owner at the peer's phone: the same reply, and whether the quote shows as the live account's message.</summary>
	[LiveFact(NeedsPeer = true, NeedsOwner = true, Timeout = GroupTimeout + 300_000)]
	public async Task Live_AReplyInTheGroupToItsOwnMessage_TheOwnerSaysHowThePeersPhoneShowsTheQuote()
	{
		var ct = TestContext.Current.CancellationToken;
		var jid = await group.WithPeerAsync(ct);
		await ReplyToOwnAsync(jid, ct);

		_ = await live.AskOwnerAsync(
			"On the peer's phone, the group \"wmn live ...\": the last reply, \"wmn reply to my own\". Does its quote show as the live account's message (its name on the quote), and does tapping it go to that message?",
			ct
		);
	}

	/// <summary>
	/// #30: the peer, no admin, revokes the live account's message in the group. Whether WhatsApp refuses it, with which
	/// code, and whether the live session gets the revoke, go to the log; a revoke that comes is compared as any received
	/// revoke of the account's own message is: no sender.
	/// </summary>
	[LiveFact(NeedsPeer = true, Timeout = GroupTimeout)]
	public async Task Live_TheNonAdminPeerRevokesTheLiveAccountsMessage_WhatWhatsAppDoesIsRecorded()
	{
		var ct = TestContext.Current.CancellationToken;
		var jid = await group.WithPeerAsync(ct);
		var client = await live.LiveAsync(ct);
		var other = await peer.ConnectedAsync(live, ct);
		var liveAccount = await live.AccountAsync(ct);
		var peerAccount = await peer.AccountAsync(ct);
		var role = (await client.GetGroupInfoAsync(jid, ct)).Participants.Single(p => LiveChecks.Names(peerAccount, p.Jid)).Role;
		Assert.True(role is GroupRole.Member, $"the peer is {role} in the group, so its revoke would be an admin's");
		var (target, got) = await ToGroupAsync(client, other, liveAccount, jid, "wmn revoke me, peer", options: null, "the group message \"wmn revoke me, peer\"", ct);

		var id = MessageId.New();
		var arrived = LiveChecks.OrNullAsync(LiveChecks.ArrivalAsync(client, id, ct));
		var thrown = await Record.ExceptionAsync(() => other.RevokeMessageAsync(jid, new MessageKey { Id = target, Sender = got.Source.Sender with { Device = 0 } }, id, ct));
		var revoke = await arrived;
		live.Write(
			$"live: the non-admin peer's revoke of the live account's group message: {Outcome(thrown)}; the live session {(revoke is null ? $"got nothing within {LiveChecks.Watch.TotalSeconds:F0} s" : $"got it as {revoke.Content.GetType().Name}")}"
		);
		if (revoke is null)
			return;
		LiveChecks.Equal(LiveChecks.Received(client, revoke, peerAccount, id, new RevokeContent { Target = new MessageKey { Id = target } }, jid), revoke);
	}

	/// <summary>The two mentions, each checked on the peer's side; the forms go to the log.</summary>
	private async Task MentionBothFormsAsync(Jid jid, CancellationToken ct)
	{
		var client = await live.LiveAsync(ct).ConfigureAwait(false);
		var other = await peer.ConnectedAsync(live, ct).ConfigureAwait(false);
		var liveAccount = await live.AccountAsync(ct).ConfigureAwait(false);
		var (pn, lid) = await peer.AccountAsync(ct).ConfigureAwait(false);
		Assert.True(lid is { IsLid: true }, "the peer session has no LID");
		live.Write($"live: the group's members: {LiveGroup.Members(await client.GetGroupInfoAsync(jid, ct).ConfigureAwait(false))}");

		foreach (var (mentioned, label) in new[] { (lid.Value, "1 of 2 (LID)"), (pn, "2 of 2 (phone number)") })
		{
			var text = $"@{mentioned.User} wmn mention {label}";
			var (id, got) = await ToGroupAsync(client, other, liveAccount, jid, text, new SendOptions { Mentions = [mentioned] }, $"the mention {label}", ct).ConfigureAwait(false);
			live.Write($"live: a mention by {LiveChecks.Form(mentioned)} came to the peer mentioning {string.Join(", ", got.Mentions.Select(m => LiveChecks.Form(m)))}");
			LiveChecks.Equal(LiveChecks.Received(other, got, liveAccount, id, new TextContent { Text = text }, jid) with { Mentions = [mentioned] }, got);
		}
	}

	/// <summary>The live account's message, then its reply to it with no sender named; the peer's copy of the reply is checked.</summary>
	private async Task ReplyToOwnAsync(Jid jid, CancellationToken ct)
	{
		var client = await live.LiveAsync(ct).ConfigureAwait(false);
		var other = await peer.ConnectedAsync(live, ct).ConfigureAwait(false);
		var liveAccount = await live.AccountAsync(ct).ConfigureAwait(false);
		live.Write($"live: the group's members: {LiveGroup.Members(await client.GetGroupInfoAsync(jid, ct).ConfigureAwait(false))}");
		var (own, _) = await ToGroupAsync(client, other, liveAccount, jid, "wmn my own", options: null, "the group message \"wmn my own\"", ct).ConfigureAwait(false);

		var options = new SendOptions { ReplyTo = new MessageReply { Id = own, QuotedText = "wmn my own" } };
		var (id, got) = await ToGroupAsync(client, other, liveAccount, jid, "wmn reply to my own", options, "the reply \"wmn reply to my own\"", ct).ConfigureAwait(false);
		live.Write($"live: the peer got the reply's quote naming a {LiveChecks.Form(got.ReplyTo?.Sender)}");

		Assert.True(got.ReplyTo?.Sender is { } quoted && LiveChecks.Names(liveAccount, quoted), $"the quote names a {LiveChecks.Form(got.ReplyTo?.Sender)} that is not the live account");
		LiveChecks.Equal(
			LiveChecks.Received(other, got, liveAccount, id, new TextContent { Text = "wmn reply to my own" }, jid) with
			{
				ReplyTo = new QuotedMessage { Id = own, Sender = got.ReplyTo!.Sender, Content = new TextContent { Text = "wmn my own" } },
			},
			got
		);
	}

	/// <summary>
	/// A text from the live session to the group, and the peer's copy of it, by the time it came; <paramref name="what"/>
	/// names it in a failure, never with a number (<see cref="LiveChecks.WithinAsync"/>).
	/// </summary>
	private static async Task<(MessageId Id, MessageReceived Got)> ToGroupAsync(
		IWhatsAppClient client,
		IWhatsAppClient other,
		(Jid Chat, Jid? Lid) liveAccount,
		Jid jid,
		string text,
		SendOptions? options,
		string what,
		CancellationToken ct
	)
	{
		var id = MessageId.New();
		var arrived = LiveChecks.ArrivalAsync(other, id, ct);
		_ = await client.SendTextAsync(jid, text, id, options, ct).ConfigureAwait(false);
		var got = await LiveChecks.WithinAsync(arrived, what).ConfigureAwait(false);
		Assert.True(got.Source.Chat == jid, $"the group message came in a {LiveChecks.Form(got.Source.Chat)} chat, not the group");
		Assert.True(LiveChecks.Names(liveAccount, got.Source.Sender), $"the group message came from a {LiveChecks.Form(got.Source.Sender)} that is not the live account");
		return (id, got);
	}

	/// <summary>
	/// A restore that reads the group's <paramref name="what"/> and sets it back to <paramref name="original"/> only when
	/// it differs: a needless set is a change a later test could count.
	/// </summary>
	private static (string What, Func<CancellationToken, Task> Run) SetBack<T>(IWhatsAppClient client, Jid jid, string what, Func<GroupInfo, T> read, T original, Func<T, CancellationToken, Task> set)
	{
		async Task RunAsync(CancellationToken token)
		{
			if (!EqualityComparer<T>.Default.Equals(read(await client.GetGroupInfoAsync(jid, token).ConfigureAwait(false)), original))
				await set(original, token).ConfigureAwait(false);
		}

		return ($"set the group's {what} back", RunAsync);
	}

	/// <summary>The restore that adds the peer back to the run's group (<see cref="LiveGroup.WithPeerAsync"/>), when a test took it out.</summary>
	private (string What, Func<CancellationToken, Task> Run) AddPeerBack() => ("add the peer back to the group", async token => _ = await group.WithPeerAsync(token).ConfigureAwait(false));

	/// <summary>The kinds of change each event carried, for the log, with the mode or the timer they set and never a JID.</summary>
	private static string Kinds(IReadOnlyList<GroupInfoChanged> seen) =>
		seen.Count == 0
			? $"nothing within {LiveChecks.Watch.TotalSeconds:F0} s"
			: string.Join(
				"; ",
				seen.Select(c =>
					string.Join(
						", ",
						c.Changes.Select(x =>
							x switch
							{
								GroupChange.MemberAddModeChanged m => $"MemberAddModeChanged({m.Mode})",
								GroupChange.DisappearingTimerChanged t => $"DisappearingTimerChanged({t.Timer.TotalDays:F0} days)",
								GroupChange.AnnounceChanged a => $"AnnounceChanged({a.IsAnnounce})",
								GroupChange.LockedChanged l => $"LockedChanged({l.IsLocked})",
								_ => x.GetType().Name,
							}
						)
					)
				)
			);

	/// <summary>A call's outcome, for the log: what it threw, with WhatsApp's code, or that it went through.</summary>
	/// <summary>
	/// The outcome and the status message, which keeps WhatsApp's 400 for an invite code, and names whatsmeow's 406 ("not
	/// valid") and 410 ("revoked") apart (sidecar groups.classifyInvite).
	/// </summary>
	private static string Answer(WhatsmeowException thrown) => $"{Outcome(thrown)} ({thrown.Message})";

	private static string Outcome(Exception? thrown) =>
		thrown switch
		{
			null => "accepted",
			WhatsAppErrorException e => $"{e.ErrorCode} {e.Code}",
			WhatsmeowException e => $"{e.ErrorCode}",
			{ } e => e.GetType().Name,
		};
}
