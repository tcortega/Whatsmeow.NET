using System.Collections.Immutable;
using Whatsmeow.Contacts;
using Whatsmeow.IntegrationTests.Contacts;

namespace Whatsmeow.LiveTests;

/// <summary>
/// The contacts questions (#33, ADR 0028), recorded for ARCHITECTURE §14: the ones one account answers, and blocking the
/// peer, checked with the fake-mode tests' own checks (<see cref="ContactChecks"/>). A LID with no phone number stays
/// [unknown]: it needs a contact whose number is hidden.
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

	/// <summary>The blocklist's entries that name the peer, by either form; the others are never looked at.</summary>
	private static ImmutableArray<BlockedUser> PeersOf(ImmutableArray<BlockedUser> blocklist, (Jid Chat, Jid? Lid) account) =>
		[.. blocklist.Where(u => LiveChecks.Names(account, u.Jid) || (u.JidAlt is { } alt && LiveChecks.Names(account, alt)))];

	private static string Listed(ImmutableArray<BlockedUser> entries) =>
		entries.IsEmpty ? "in no entry" : $"in {entries.Length} entries: {string.Join(", ", entries.Select(u => $"a {LiveChecks.Form(u.Jid)} with {LiveChecks.Form(u.JidAlt)} beside it"))}";
}
