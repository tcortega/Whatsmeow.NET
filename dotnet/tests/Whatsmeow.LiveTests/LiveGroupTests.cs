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
	/// <summary>A group test's timeout: the group's creation on first use, its own work, and putting the group back.</summary>
	private const int GroupTimeout = 600_000;

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

		var result = Assert.Single(creation.Created.Participants);
		Assert.True(LiveChecks.Names(peerAccount, result.Jid), $"the create answer lists a {LiveChecks.Form(result.Jid)} that is not the peer");
		Assert.Equal(new ParticipantResult { Jid = result.Jid, JidAlt = result.JidAlt, Outcome = new ParticipantOutcome.Done() }, result);

		Assert.True(info.Owner is { } owner && LiveChecks.Names(liveAccount, owner), $"the group's owner is a {LiveChecks.Form(info.Owner)} that is not the live account");
		var self = Assert.Single(info.Participants, p => LiveChecks.Names(liveAccount, p.Jid));
		var member = Assert.Single(info.Participants, p => LiveChecks.Names(peerAccount, p.Jid));
		Assert.Equal(
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
		Assert.Equal(info, creation.InfoAfter);
	}

	/// <summary>
	/// The live session sets the name, the topic, announce and locked: the peer gets each as a change by the live account,
	/// in that order, and reads them in the group's info. They are set back after.
	/// </summary>
	[LiveFact(NeedsPeer = true, Timeout = GroupTimeout)]
	public async Task Live_SettingsChangedByTheAdmin_ThePeerSeesEachChange()
	{
		var ct = TestContext.Current.CancellationToken;
		var jid = await group.WithPeerAsync(ct);
		var client = await live.LiveAsync(ct);
		var other = await peer.ConnectedAsync(live, ct);
		var liveAccount = await live.AccountAsync(ct);
		var name = (await client.GetGroupInfoAsync(jid, ct)).Name;
		using var changes = new LiveEvents<GroupInfoChanged>(other, c => c.Group == jid);
		try
		{
			await GroupChecks.SetSettingsAsync(client, jid, $"{name} crew", "wmn plans for Monday", ct);
			var seen = await changes.NextAsync(4, LiveChecks.Watch, ct);
			live.Write($"live: the peer got {seen.Count} group changes for 4 settings, from a {LiveChecks.Form(seen.Count > 0 ? seen[0].Sender : null)}: {string.Join("; ", seen.Select(c => string.Join(", ", c.Changes.Select(x => x.GetType().Name))))}");

			Assert.Equal(4, seen.Count);
			var by = seen[0];
			Assert.True(by.Sender is { } sender && LiveChecks.Names(liveAccount, sender), $"the change came from a {LiveChecks.Form(by.Sender)} that is not the live account");
			Assert.Equal(
				GroupChecks.Settings($"{name} crew", "wmn plans for Monday").Select((change, i) => GroupChecks.Changed(other.SessionId, jid, by.Sender, by.SenderAlt, seen[i].Timestamp, change)),
				seen
			);
			var info = await other.GetGroupInfoAsync(jid, ct);
			Assert.Equal(($"{name} crew", "wmn plans for Monday", true, true), (info.Name, info.Topic, info.IsAnnounce, info.IsLocked));
		}
		finally
		{
			await client.SetGroupLockedAsync(jid, locked: false, CancellationToken.None);
			await client.SetGroupAnnounceAsync(jid, announce: false, CancellationToken.None);
			await client.SetGroupTopicAsync(jid, topic: null, CancellationToken.None);
			await client.SetGroupNameAsync(jid, name, CancellationToken.None);
		}
	}

	/// <summary>
	/// The group locked, the peer, no admin, sets its name and promotes itself: WhatsApp's code for each goes to the log
	/// (the [unknown] #34 left), then the name is checked as the fake-mode test checks it, refused with WhatsApp's code
	/// and unchanged. The group is unlocked after.
	/// </summary>
	[LiveFact(NeedsPeer = true, Timeout = GroupTimeout)]
	public async Task Live_TheNonAdminPeerSetsTheNameOrPromotesItself_WhatsAppRefusesWithItsCode()
	{
		var ct = TestContext.Current.CancellationToken;
		var jid = await group.WithPeerAsync(ct);
		var client = await live.LiveAsync(ct);
		var other = await peer.ConnectedAsync(live, ct);
		var (to, _) = await peer.AccountAsync(ct);
		await client.SetGroupLockedAsync(jid, locked: true, ct);
		try
		{
			var name = (await other.GetGroupInfoAsync(jid, ct)).Name;
			var renamed = await Record.ExceptionAsync(() => other.SetGroupNameAsync(jid, "wmn not mine", ct));
			live.Write($"live: the non-admin peer's rename of a locked group: {Outcome(renamed)}");
			IReadOnlyList<ParticipantResult> promoted = [];
			var promoting = await Record.ExceptionAsync(async () => promoted = await other.PromoteParticipantsAsync(jid, [to], ct).ConfigureAwait(false));
			live.Write(
				$"live: the non-admin peer promoting itself: {(promoting is null ? string.Join(", ", promoted.Select(r => r.Outcome is ParticipantOutcome.Refused refused ? $"refused with {refused.Code}" : r.Outcome.GetType().Name)) : Outcome(promoting))}"
			);

			var thrown = Assert.IsType<WhatsAppErrorException>(renamed);
			Assert.Equal((WhatsmeowErrorCode.WhatsAppError, 403), (thrown.ErrorCode, thrown.Code));
			Assert.Equal(name, (await other.GetGroupInfoAsync(jid, ct)).Name);
		}
		finally
		{
			await client.SetGroupLockedAsync(jid, locked: false, CancellationToken.None);
		}
	}

	/// <summary>
	/// The live session removes the peer and resets the invite link: the old link no longer works, and the peer joins by
	/// the new one, gets <see cref="GroupJoined"/>, and the live session sees it join, as in the fake-mode invite test.
	/// </summary>
	[LiveFact(NeedsPeer = true, Timeout = GroupTimeout)]
	public async Task Live_InviteLinkResetThenJoined_TheOldLinkStopsWorking_AndThePeerJoinsByTheNewOne()
	{
		var ct = TestContext.Current.CancellationToken;
		var jid = await group.WithPeerAsync(ct);
		var client = await live.LiveAsync(ct);
		var other = await peer.ConnectedAsync(live, ct);
		var peerAccount = await peer.AccountAsync(ct);
		var member = (await client.GetGroupInfoAsync(jid, ct)).Participants.Single(p => LiveChecks.Names(peerAccount, p.Jid)).Jid;

		var removed = Assert.Single(await client.RemoveParticipantsAsync(jid, [member], ct));
		try
		{
			Assert.Equal(new ParticipantResult { Jid = removed.Jid, JidAlt = removed.JidAlt, Outcome = new ParticipantOutcome.Done() }, removed);
			var link = await client.GetGroupInviteLinkAsync(jid, ct);
			var reset = await client.ResetGroupInviteLinkAsync(jid, ct);
			Assert.NotEqual(link, reset);
			var old = await Assert.ThrowsAsync<WhatsmeowException>(() => other.JoinGroupWithLinkAsync(link, ct));
			live.Write($"live: the peer joining by the link from before the reset: {Outcome(old)}");
			Assert.Equal(WhatsmeowErrorCode.InviteLinkInvalid, old.ErrorCode);

			var joined = other.WaitForAsync<GroupJoined>(j => j.Group.Jid == jid, LiveChecks.Watch, ct);
			var seen = client.WaitForAsync<GroupInfoChanged>(c => c.Group == jid && c.Changes.Any(x => x is GroupChange.ParticipantJoined p && LiveChecks.Names(peerAccount, p.Jid)), LiveChecks.Watch, ct);
			Assert.Equal(jid, await other.JoinGroupWithLinkAsync(reset, ct));

			var join = await LiveChecks.WithinAsync(joined, "the peer's GroupJoined");
			Assert.True(join.Sender is { } sender && LiveChecks.Names(peerAccount, sender), $"the peer's GroupJoined names a {LiveChecks.Form(join.Sender)} that is not the peer");
			var change = await LiveChecks.WithinAsync(seen, "the peer's join, seen by the live session");
			live.Write($"live: the live session saw the peer join, from a {LiveChecks.Form(change.Sender)}, the member a {LiveChecks.Form(change.Changes.OfType<GroupChange.ParticipantJoined>().First().Jid)}");
			var joiner = change.Changes.OfType<GroupChange.ParticipantJoined>().First().Jid;
			Assert.Equal(GroupChecks.Changed(client.SessionId, jid, change.Sender, change.SenderAlt, change.Timestamp, new GroupChange.ParticipantJoined { Jid = joiner }), change);
		}
		finally
		{
			// A failure above may leave the peer out; the reset link is the group's now.
			_ = await group.WithPeerAsync(CancellationToken.None);
		}
	}

	/// <summary>
	/// The peer leaves: it is in none of its groups' lists, reading the group is refused with WhatsApp's code, and the
	/// live session sees it leave, as in the fake-mode leave test. It joins again by the invite link after.
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

		await other.LeaveGroupAsync(jid, ct);
		try
		{
			Assert.DoesNotContain(await other.GetGroupsAsync(ct), g => g.Jid == jid);
			var thrown = await Assert.ThrowsAsync<WhatsAppErrorException>(() => other.GetGroupInfoAsync(jid, ct));
			live.Write($"live: the peer reading the group it left: {Outcome(thrown)}");
			Assert.Equal(403, thrown.Code);
			var change = await LiveChecks.WithinAsync(left, "the peer's leave, seen by the live session");
			var leaver = change.Changes.OfType<GroupChange.ParticipantLeft>().First().Jid;
			Assert.True(change.Sender is { } sender && LiveChecks.Names(peerAccount, sender), $"the leave came from a {LiveChecks.Form(change.Sender)} that is not the peer");
			Assert.Equal(GroupChecks.Changed(client.SessionId, jid, change.Sender, change.SenderAlt, change.Timestamp, new GroupChange.ParticipantLeft { Jid = leaver }), change);
		}
		finally
		{
			_ = await group.WithPeerAsync(CancellationToken.None);
		}
	}

	/// <summary>
	/// A well formed invite code no group has: what WhatsApp answers goes to the log (whatsmeow maps only 406 and 410),
	/// and the join is refused as the fake-mode test refuses an unknown code, as an invalid link.
	/// </summary>
	[LiveFact(NeedsPeer = true)]
	public async Task Live_JoinWithAWellFormedUnknownInviteCode_IsRefusedAsAnInvalidLink()
	{
		var ct = TestContext.Current.CancellationToken;
		var other = await peer.ConnectedAsync(live, ct);
		var code = RandomNumberGenerator.GetString("ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789", 22);

		var thrown = await Assert.ThrowsAsync<WhatsmeowException>(() => other.JoinGroupWithLinkAsync(new Uri($"https://chat.whatsapp.com/{code}"), ct));

		live.Write($"live: joining by a well formed unknown invite code: {Outcome(thrown)}");
		Assert.Equal(WhatsmeowErrorCode.InviteLinkInvalid, thrown.ErrorCode);
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
		var (target, got) = await ToGroupAsync(client, other, liveAccount, jid, "wmn revoke me, peer", options: null, ct);

		var id = MessageId.New();
		var arrived = LiveChecks.OrNullAsync(LiveChecks.ArrivalAsync(client, id, ct));
		var thrown = await Record.ExceptionAsync(() => other.RevokeMessageAsync(jid, new MessageKey { Id = target, Sender = got.Source.Sender with { Device = 0 } }, id, ct));
		var revoke = await arrived;
		live.Write(
			$"live: the non-admin peer's revoke of the live account's group message: {Outcome(thrown)}; the live session {(revoke is null ? $"got nothing within {LiveChecks.Watch.TotalSeconds:F0} s" : $"got it as {revoke.Content.GetType().Name}")}"
		);
		if (revoke is null)
			return;
		Assert.Equal(LiveChecks.Received(client, revoke, peerAccount, id, new RevokeContent { Target = new MessageKey { Id = target } }), revoke);
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
			var (id, got) = await ToGroupAsync(client, other, liveAccount, jid, text, new SendOptions { Mentions = [mentioned] }, ct).ConfigureAwait(false);
			live.Write($"live: a mention by {LiveChecks.Form(mentioned)} came to the peer mentioning {string.Join(", ", got.Mentions.Select(m => LiveChecks.Form(m)))}");
			Assert.Equal(LiveChecks.Received(other, got, liveAccount, id, new TextContent { Text = text }) with { Mentions = [mentioned] }, got);
		}
	}

	/// <summary>The live account's message, then its reply to it with no sender named; the peer's copy of the reply is checked.</summary>
	private async Task ReplyToOwnAsync(Jid jid, CancellationToken ct)
	{
		var client = await live.LiveAsync(ct).ConfigureAwait(false);
		var other = await peer.ConnectedAsync(live, ct).ConfigureAwait(false);
		var liveAccount = await live.AccountAsync(ct).ConfigureAwait(false);
		live.Write($"live: the group's members: {LiveGroup.Members(await client.GetGroupInfoAsync(jid, ct).ConfigureAwait(false))}");
		var (own, _) = await ToGroupAsync(client, other, liveAccount, jid, "wmn my own", options: null, ct).ConfigureAwait(false);

		var options = new SendOptions { ReplyTo = new MessageReply { Id = own, QuotedText = "wmn my own" } };
		var (id, got) = await ToGroupAsync(client, other, liveAccount, jid, "wmn reply to my own", options, ct).ConfigureAwait(false);
		live.Write($"live: the peer got the reply's quote naming a {LiveChecks.Form(got.ReplyTo?.Sender)}");

		Assert.True(got.ReplyTo?.Sender is { } quoted && LiveChecks.Names(liveAccount, quoted), $"the quote names a {LiveChecks.Form(got.ReplyTo?.Sender)} that is not the live account");
		Assert.Equal(
			LiveChecks.Received(other, got, liveAccount, id, new TextContent { Text = "wmn reply to my own" }) with
			{
				ReplyTo = new QuotedMessage { Id = own, Sender = got.ReplyTo!.Sender, Content = new TextContent { Text = "wmn my own" } },
			},
			got
		);
	}

	/// <summary>A text from the live session to the group, and the peer's copy of it, by the time it came.</summary>
	private static async Task<(MessageId Id, MessageReceived Got)> ToGroupAsync(
		IWhatsAppClient client,
		IWhatsAppClient other,
		(Jid Chat, Jid? Lid) liveAccount,
		Jid jid,
		string text,
		SendOptions? options,
		CancellationToken ct
	)
	{
		var id = MessageId.New();
		var arrived = LiveChecks.ArrivalAsync(other, id, ct);
		_ = await client.SendTextAsync(jid, text, id, options, ct).ConfigureAwait(false);
		var got = await LiveChecks.WithinAsync(arrived, $"the group message \"{text}\"").ConfigureAwait(false);
		Assert.Equal(jid, got.Source.Chat);
		Assert.True(LiveChecks.Names(liveAccount, got.Source.Sender), $"the group message came from a {LiveChecks.Form(got.Source.Sender)} that is not the live account");
		return (id, got);
	}

	/// <summary>A call's outcome, for the log: what it threw, with WhatsApp's code, or that it went through.</summary>
	private static string Outcome(Exception? thrown) =>
		thrown switch
		{
			null => "accepted",
			WhatsAppErrorException e => $"{e.ErrorCode} {e.Code}",
			WhatsmeowException e => $"{e.ErrorCode}",
			{ } e => e.GetType().Name,
		};
}
