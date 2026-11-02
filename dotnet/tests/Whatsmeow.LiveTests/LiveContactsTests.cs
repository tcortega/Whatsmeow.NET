namespace Whatsmeow.LiveTests;

/// <summary>
/// The contacts questions one account can answer (#33, ADR 0028), recorded for ARCHITECTURE §14. The ones that need a
/// second account (block and unblock a contact, a LID with no phone number) wait for #36.
/// </summary>
[Collection(nameof(Live))]
public sealed class LiveContactsTests(LiveSidecar live)
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
}
