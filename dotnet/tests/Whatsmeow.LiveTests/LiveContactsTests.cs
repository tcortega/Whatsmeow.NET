using System.Collections.Immutable;
using Whatsmeow.Contacts;
using Whatsmeow.Events;
using Whatsmeow.IntegrationTests.Contacts;
using Whatsmeow.IntegrationTests.Support;
using Whatsmeow.Presence;

namespace Whatsmeow.LiveTests;

/// <summary>
/// The contacts questions (#33, ADR 0028), recorded for ARCHITECTURE §14: the ones one account answers, and blocking the
/// peer, checked with the fake-mode tests' own checks (<see cref="ContactChecks"/>), and, on opt-in, #36's push name
/// stimulus. A LID with no phone number stays [unknown]: it needs a contact whose number is hidden.
/// </summary>
[Collection(nameof(Live))]
public sealed class LiveContactsTests(LiveSidecar live, LivePeer peer)
{
	/// <summary>
	/// Numbers in the range reserved for fiction (NANP 555-0100 to 0199); +12025550101 is on WhatsApp all the same
	/// [observed: the #10 live run, 2026-11-02], so a batch has both answers.
	/// </summary>
	private static readonly string[] s_unregistered = ["+12025550100", "+12025550101", "+12025550102"];

	/// <summary>What WhatsApp answers for an unregistered number, alone and in a batch, and whether it answers each one.</summary>
	[LiveFact]
	public async Task Live_IsOnWhatsApp_AnUnregisteredNumberAndABatch_TheAnswersAreRecorded()
	{
		var ct = TestContext.Current.CancellationToken;
		var client = await live.LiveAsync(ct);

		var one = await client.IsOnWhatsAppAsync([s_unregistered[0]], ct);
		var batch = await client.IsOnWhatsAppAsync(s_unregistered, ct);

		live.Write($"live: IsOnWhatsApp, 1 asked, {one.Length} answered: {string.Join("; ", one)}");
		live.Write($"live: IsOnWhatsApp, {s_unregistered.Length} asked, {batch.Length} answered: {string.Join("; ", batch)}");
	}

	/// <summary>How WhatsApp keys its answer to a user-info query by LID: under the LID asked, or under the phone number.</summary>
	[LiveFact]
	public async Task Live_GetUserInfo_ByTheAccountsLid_TheAnswersKeyIsRecorded()
	{
		var ct = TestContext.Current.CancellationToken;
		var client = await live.LiveAsync(ct);
		var (account, lid) = await live.AccountAsync(ct);
		lid ??= (await client.ResolveJidsAsync([account], ct))[0].JidAlt;
		Assert.True(lid is { IsLid: true }, "neither the session nor the store has a LID for the account");

		var answers = await client.GetUserInfoAsync([lid.Value], ct);

		live.Write(
			$"live: GetUserInfo by LID, {answers.Length} answers, keyed by {string.Join(", ", answers.Select(a => a.Jid == lid.Value ? "the LID asked" : $"{a.Jid.Server}"))}"
		);
	}

	/// <summary>
	/// Blocks the peer, then unblocks it, and reads the blocklist after each: the peer's entries are the peer alone, by
	/// its LID with its phone number, then none. Only the peer's entries are read: the account's other blocks are the
	/// owner's, and are never compared or written. A peer an earlier run left blocked is unblocked first. WhatsApp's
	/// <c>active</c> flag reaches no public record, and the sidecar lists inactive items too, so an unblocked peer that
	/// WhatsApp still lists as inactive shows here as a listed one; each of the peer's lists goes to the log before it
	/// is checked. The unblock runs whatever happened before it. The peer is never blocked on purpose, so unblocked is
	/// how a test finds it (docs/guidelines/tests.md Live WhatsApp).
	/// </summary>
	[LiveFact(NeedsPeer = true)]
	public async Task Live_BlockThenUnblockThePeer_TheBlocklistReflectsEach()
	{
		var ct = TestContext.Current.CancellationToken;
		var client = await live.LiveAsync(ct);
		_ = await peer.ConnectedAsync(live, ct);
		var account = await peer.AccountAsync(ct);
		var (pn, lid) = account;
		Assert.True(lid is { IsLid: true }, "the peer session has no LID");
		if (!PeersOf(await client.GetBlocklistAsync(ct), account).IsEmpty)
		{
			live.Write("live: the peer was blocked before the test, as an aborted run leaves it; unblocking it first");
			await client.UnblockAsync(pn, ct);
		}

		var blocked = ImmutableArray<BlockedUser>.Empty;
		await LiveRestore.AfterAsync(
			live,
			async () =>
			{
				await client.BlockAsync(pn, ct).ConfigureAwait(false);
				blocked = PeersOf(await client.GetBlocklistAsync(ct).ConfigureAwait(false), account);
				live.Write($"live: after the block the blocklist lists the peer {Listed(blocked)}");
			},
			("unblock the peer", t => client.UnblockAsync(pn, t))
		);
		var unblocked = PeersOf(await client.GetBlocklistAsync(ct), account);
		live.Write($"live: after the unblock the blocklist lists the peer {Listed(unblocked)}");

		LiveChecks.Redacting(() => ContactChecks.ValidateBlocked(blocked, lid.Value, pn));
		LiveChecks.Redacting(() => ContactChecks.ValidateUnblocked(unblocked));
	}

	/// <summary>
	/// #36's push name stimulus on the persistent sessions, on opt-in only (ADR 0061): the live session, the business
	/// account, connects anew, so nothing sent a presence on its connection whatever ran before, then sends the peer up to
	/// three texts, with no presence, after a named unavailable presence, and after a named available one, and stops at
	/// the first that carries a push name (<see cref="PushNameStimulus"/>). Each text's line says what it carried, never
	/// the name. It passes only on the peer store's first sight, read before the first text and again just before the named
	/// one, with the change equal to the fake-mode check whole; a store that had the push name already gives stimulus
	/// evidence, never a pass, and a first sight here claims nothing about what a fresh device gets.
	/// <para>
	/// It runs alone, with a filter on this one method (docs/guidelines/tests.md), and checks before anything starts: the
	/// opt-in, so it spends nothing even when xUnit runs explicit tests, and that no test of the run used the peer, whose
	/// traffic could have stored the push name unattributed. It reads the live account before connecting, which never creates
	/// a session, so a database without <c>live</c> fails without gaining one; the peer is checked the same way
	/// (<see cref="LivePeer.ConnectedAsync"/>). It never reads the business's profile: <c>GetUserInfo</c> stores a business
	/// name even when its answer drops it (#153), which would spend the peer store's first sight of that too.
	/// </para>
	/// <para>
	/// It leaves the live session unavailable, a deliberate postcondition, not a restore: the presence it had before is
	/// [unknown]. The peer's presence is never touched. The peer's rows of the business are logged again after the texts,
	/// booleans only, to record whether a text stored the business name too (a message's verified name, whatsmeow
	/// message.go 56-57); whatsmeow stores that on a goroutine of its own, so the read may come before it [inferred], and no
	/// business name event is watched for, since this test uses no business name.
	/// </para>
	/// </summary>
	[LiveFact(NeedsPeer = true, NeedsPushNameFirstSight = true)]
	public async Task Live_ThePeer_GetsTheBusinessPushNameAsTheFakeModeCheck_WhenItsFirstSightIsUnspent()
	{
		// First, before anything starts: xUnit runs an explicit test when asked to (--explicit on), opt-in or not.
		Assert.True(LiveSettings.PushNameFirstSight, $"pushname_opt_in_missing: {LiveSettings.PushNameFirstSightVariable} is not set, so nothing was sent and no first sight was spent");
		Assert.False(
			peer.Started,
			"pushname_not_alone: another test of this run used the peer, whose traffic may have stored the business's push name, so nothing was sent; run this test alone (docs/guidelines/tests.md)"
		);
		var ct = TestContext.Current.CancellationToken;
		// Never creates a session: against a database without the live session it fails, where LiveAsync would add one.
		var businessAccount = await live.AccountAsync(ct);
		Assert.True(businessAccount is { Chat.IsPhoneNumber: true, Lid.IsLid: true }, "the live account has no phone number and LID pair");
		// The sidecar sends a presence only for the SendPresence RPC (internal/presence/presence.go 64), so a new one has sent none.
		await live.StopAsync();
		var business = await live.LiveAsync(ct);
		live.Write("live: push name stimulus: the live session connected anew, with no presence sent on this connection");
		var receiver = await peer.ConnectedAsync(live, ct);
		var (peerChat, _) = await peer.AccountAsync(ct);

		await LiveRestore.AfterAsync(
			live,
			() =>
				PushNameStimulus.RunAsync(
					new PushNameLadder
					{
						Receiver = receiver,
						Sender = businessAccount,
						SendPresenceAsync = business.SendPresenceAsync,
						SendTextAsync = (_, id, text, t) => business.SendTextAsync(peerChat, text, id, cancellationToken: t),
						Id = k => MessageId.From($"WMNPUSH{k}{LiveClock.Time.GetUtcNow():yyyyMMddHHmmss}"),
						CheckMessage = (got, id, text) =>
						{
							live.Write($"live: push name stimulus: the text came from a {LiveChecks.Form(got.Source.Sender)} with a {LiveChecks.Form(got.Source.SenderAlt)} beside it");
							LiveChecks.Equal(LiveChecks.Received(receiver, got, businessAccount, id, new TextContent { Text = text }), got);
						},
						Redacting = LiveChecks.Redacting,
						Log = line => live.Write($"live: {line}"),
						Watch = LiveChecks.Watch,
						Time = LiveClock.Time,
					},
					ct
				),
			("leave the live session unavailable", t => business.SendPresenceAsync(PresenceState.Unavailable, t)),
			(
				"read the peer's rows of the business after the texts",
				async t =>
					live.Write(
						$"live: push name stimulus, after the texts, the sender's rows: {PushNameStimulus.Describe(await PushNameStimulus.RowsAsync(receiver, businessAccount, t).ConfigureAwait(false))}"
					)
			)
		);
	}

	/// <summary>The blocklist's entries that name the peer, by either form; the others are never looked at.</summary>
	private static ImmutableArray<BlockedUser> PeersOf(ImmutableArray<BlockedUser> blocklist, (Jid Chat, Jid? Lid) account) =>
		[.. blocklist.Where(u => LiveChecks.Names(account, u.Jid) || (u.JidAlt is { } alt && LiveChecks.Names(account, alt)))];

	private static string Listed(ImmutableArray<BlockedUser> entries) =>
		entries.IsEmpty ? "in no entry" : $"in {entries.Length} entries: {string.Join(", ", entries.Select(u => $"a {LiveChecks.Form(u.Jid)} with {LiveChecks.Form(u.JidAlt)} beside it"))}";
}
