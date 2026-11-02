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
	/// Blocks the peer, then unblocks it, and reads the blocklist after each: the peer alone, by its LID with its phone
	/// number, then nobody. WhatsApp's <c>active</c> flag reaches no public record, and the sidecar lists inactive items
	/// too, so an unblocked user WhatsApp still lists as inactive shows here as a listed one; each list goes to the log
	/// before it is checked. The unblock runs whatever happened before it.
	/// </summary>
	[LiveFact(NeedsPeer = true)]
	public async Task Live_BlockThenUnblockThePeer_TheBlocklistReflectsEach()
	{
		var ct = TestContext.Current.CancellationToken;
		var client = await live.LiveAsync(ct);
		_ = await peer.ConnectedAsync(live, ct);
		var (pn, lid) = await peer.AccountAsync(ct);
		Assert.True(lid is { IsLid: true }, "the peer session has no LID");
		var before = await client.GetBlocklistAsync(ct);
		ContactChecks.ValidateUnblocked(before);

		ImmutableArray<BlockedUser> blocked;
		ImmutableArray<BlockedUser> unblocked;
		try
		{
			await client.BlockAsync(pn, ct);
			blocked = await client.GetBlocklistAsync(ct);
			live.Write($"live: after the block the blocklist lists {Listed(blocked)}");
		}
		finally
		{
			await client.UnblockAsync(pn, CancellationToken.None);
			unblocked = await client.GetBlocklistAsync(CancellationToken.None);
			live.Write($"live: after the unblock the blocklist lists {Listed(unblocked)}");
		}

		ContactChecks.ValidateBlocked(blocked, lid.Value, pn);
		ContactChecks.ValidateUnblocked(unblocked);
	}

	private static string Listed(ImmutableArray<BlockedUser> list) =>
		list.IsEmpty ? "nobody" : string.Join(", ", list.Select(u => $"a {LiveChecks.Form(u.Jid)} with {LiveChecks.Form(u.JidAlt)} beside it"));
}
