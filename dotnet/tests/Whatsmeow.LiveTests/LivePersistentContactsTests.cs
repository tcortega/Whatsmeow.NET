using Whatsmeow.Core.Errors;
using Whatsmeow.Events;
using Whatsmeow.IntegrationTests.Contacts;
using Whatsmeow.IntegrationTests.Support;
using Whatsmeow.LiveTests.Control;
using Whatsmeow.Presence;

namespace Whatsmeow.LiveTests;

/// <summary>
/// #36's Contacts FullCycle on the persistent sessions, on opt-in only (ADR 0062): the peer (the owner's personal account)
/// is the receiver of every branch, and the live session (the business account) the subject, checked with the fake-mode
/// tests' own records (<see cref="ContactChecks"/>) through the shared <see cref="ContactsCycle"/>, which its fake-mode
/// tests check. No QR code, no new session, no logout, no deletion, and no profile change but branch 5's, on consent.
/// <para>
/// The persistent stores have seen the business: its push name and its business name are on both of the peer's rows
/// [observed: root's #153 measurement of 2026-11-08, 03:36 and 03:38 UTC, before connecting and after catch-up].
/// whatsmeow dispatches those two changes only when the stored value changes (user.go 679-736) [observed: source], so
/// the push name branch fails <c>pushname_precondition_spent</c> by design here, and the business name branch
/// <c>businessname_precondition_spent</c> after checking the answer, unless the read before it finds a row with none.
/// Those failures are the verdict, not a skip: this test passes only when every branch passed.
/// </para>
/// <para>
/// Branch 5 (about) changes the business account's about text once and puts the text back (<see cref="AboutChange"/>,
/// ADR 0063), only with the owner's consent (<see cref="LiveSettings.AboutConsentVariable"/>); without it, it fails
/// <c>about_not_authorized</c> before it reads or sets anything. Branch 6 compares the peer's phone number row of the business whole, with the saved name the owner
/// stated (<see cref="LiveSettings.PeerSavedNameVariable"/>), and the names this run's text and answer carried; without
/// one it fails, and the store's value never stands in. No failure prints a name.
/// </para>
/// <para>
/// Before any WhatsApp network use it reads both accounts and logs the live sidecar's whatsmeow version, local calls
/// that create no session, and refuses the same account in both roles (<c>contacts_same_account</c>). It runs alone, with a filter on this one method (docs/guidelines/tests.md),
/// failing <c>contacts_not_alone</c> before anything starts when a test of the run already used the peer.
/// </para>
/// <para>
/// The business's traffic is counted on the peer's client before either session connects (<see cref="ContactsTraffic"/>),
/// so whatever the peer's connection replays is counted. It leaves the live session unavailable once it connected, the
/// state the other live tests leave it in, not a restore of one read. After the body, whatever it did, three checks run,
/// each on its own bound (<see cref="AfterChecks"/>): each database lists the same sessions with the same accounts and
/// devices, none logged out, and no pairing file is left in the output directory, which the live sidecar's build empties
/// before the baseline; their failures are thrown with the body's.
/// </para>
/// </summary>
[Collection(nameof(Live))]
public sealed class LivePersistentContactsTests(LiveSidecar live, LivePeer peer, LiveGroup group) : IClassFixture<LiveGroup>
{
	/// <summary>The files a pairing writes for the person (docs/guidelines/tests.md Live WhatsApp).</summary>
	private static readonly string[] s_pairingFiles = ["qr.png", "pairing-code.txt"];

	[LiveFact(NeedsPeer = true, NeedsContactsFullCycle = true)]
	public async Task Live_ThePersistentPeer_SeesTheContactChangesAsTheFakeModeChecks_WhereItsStoreLeavesAFirstSight()
	{
		// First, before anything starts: xUnit runs an explicit test when asked to (--explicit on), opt-in or not.
		Assert.True(LiveSettings.ContactsFullCycle, $"contacts_opt_in_missing: {LiveSettings.ContactsFullCycleVariable} is not set, so nothing was read or sent");
		Assert.False(
			peer.Started,
			"contacts_not_alone: another test of this run used the peer, whose traffic may have changed its rows of the business, so nothing was read or sent; run this test alone (docs/guidelines/tests.md)"
		);
		var ct = TestContext.Current.CancellationToken;
		// Local reads only, before any WhatsApp network use: both sidecars start, no session connects, none is created.
		var businessAccount = await live.AccountAsync(ct);
		Assert.True(businessAccount is { Chat.IsPhoneNumber: true, Lid.IsLid: true }, "the live account has no phone number and LID pair");
		var info = await live.InfoAsync(ct);
		live.Write($"live: contacts fullcycle: whatsmeow {info.WhatsmeowVersion}");
		var peerBefore = await peer.SessionsAsync(live, ct);
		var peerAccount = await peer.AccountAsync(ct);
		ContactsCycle.RequireDistinct(businessAccount, peerAccount);
		var liveBefore = await live.SessionsAsync(ct);

		await AfterChecks.RunAsync(
			() => BodyAsync(businessAccount, peerAccount, ct),
			LiveRestore.Bound,
			LiveClock.Time,
			line => live.Write($"live: contacts fullcycle {line}"),
			("postflight, the live database", async t => AfterChecks.SameSessions("live", liveBefore, await live.SessionsAsync(t).ConfigureAwait(false))),
			("postflight, the peer database", async t => AfterChecks.SameSessions("peer", peerBefore, await peer.SessionsAsync(live, t).ConfigureAwait(false))),
			("postflight, pairing outputs", _ => PairingOutputsAsync())
		);
	}

	/// <summary>
	/// The connections and the cycle. The business's traffic is counted on the peer's client before either session
	/// connects, so it sees whatever the peer's connection replays; the live session is left unavailable whatever the
	/// cycle did, once it connected.
	/// </summary>
	private async Task BodyAsync((Jid Chat, Jid? Lid) businessAccount, (Jid Chat, Jid? Lid) peerAccount, CancellationToken ct)
	{
		var receiver = await peer.ExistingAsync(live, ct).ConfigureAwait(false);
		using var traffic = new ContactsTraffic(receiver, businessAccount);
		var business = await live.LiveAsync(ct).ConfigureAwait(false);
		await LiveRestore
			.AfterAsync(
				live,
				async () =>
				{
					_ = await peer.ConnectedAsync(live, ct).ConfigureAwait(false);
					live.Write(
						$"live: contacts fullcycle, the live store's rows of the peer: {ContactsCycle.Describe(await ContactsCycle.RowsAsync(business, peerAccount, ct).ConfigureAwait(false))}"
					);
					var others = (await receiver.GetContactsAsync(ct).ConfigureAwait(false))
						.Where(c => LiveChecks.Names(businessAccount, c.Jid) && c.Jid != businessAccount.Chat && c.Jid != businessAccount.Lid)
						.ToArray();
					live.Write($"live: contacts fullcycle, the peer's other rows naming the business: {others.Length}{string.Concat(others.Select(c => $", a {LiveChecks.Form(c.Jid)}"))}");

					await ContactsCycle
						.RunAsync(
							new ContactsRun
							{
								Receiver = receiver,
								Subject = businessAccount,
								Traffic = traffic,
								ReadProfileAsync = (jid, t) => receiver.GetUserInfoAsync([jid], t),
								SendNamedPresenceAsync = t => business.SendPresenceAsync(PresenceState.Unavailable, t),
								SendTextAsync = async (id, text, t) => _ = await business.SendTextAsync(peerAccount.Chat, text, id, cancellationToken: t).ConfigureAwait(false),
								Id = () => MessageId.From($"WMNCONTACTS{LiveClock.Time.GetUtcNow():yyyyMMddHHmmss}"),
								Expected = (got, id, text) => LiveChecks.Received(receiver, got, businessAccount, id, new TextContent { Text = text }),
								Then =
								[
									new FreshStep { Name = "branches 3 and 4, picture", Run = t => LiveGroupPhoto.SetAndRemoveAsync(live, group, receiver, t) },
									new FreshStep { Name = "branch 5, about", Run = t => AboutAsync(receiver, business, businessAccount, t) },
								],
								SavedName = LiveSettings.PeerSavedName,
								Redacting = LiveChecks.Redacting,
								Log = line => live.Write($"live: {line}"),
								Watch = LiveChecks.Watch,
							},
							ct
						)
						.ConfigureAwait(false);
				},
				("leave the live session unavailable", t => business.SendPresenceAsync(PresenceState.Unavailable, t))
			)
			.ConfigureAwait(false);
	}

	/// <summary>
	/// The pairing files left in the output directory, which the live sidecar's build empties before the baseline
	/// (<c>LiveSidecar.BuildAsync</c>, <c>DeleteShown</c>): it shows what remains, not that no file came and went.
	/// </summary>
	private Task PairingOutputsAsync()
	{
		var left = s_pairingFiles.Count(f => File.Exists(Path.Combine(live.OutputDirectory, f)));
		live.Write($"live: contacts fullcycle postflight: pairing files left {left}");
		Assert.True(left == 0, $"postflight_pairing_output: {left} pairing files are in the output directory");
		return Task.CompletedTask;
	}

	/// <summary>
	/// Branch 5 (ADR 0063): without the owner's consent (<see cref="LiveSettings.AboutConsentVariable"/>) it fails before it
	/// reads or sets anything; with it, the live session changes its account's about text once through the live tests'
	/// control and puts the original text back, checked by its own read, and the peer must get the change.
	/// </summary>
	private async Task AboutAsync(IWhatsAppClient receiver, IWhatsAppClient business, (Jid Chat, Jid? Lid) businessAccount, CancellationToken ct) =>
		await AboutChange
			.RunAsync(
				new AboutRun
				{
					Consented = LiveSettings.AboutConsent,
					Receiver = receiver,
					Subject = businessAccount,
					ReadOwnProfileAsync = (jid, t) => business.GetUserInfoAsync([jid], t),
					SetAsync = async (text, t) =>
					{
						var control = await live.ContactControlAsync(t).ConfigureAwait(false);
						_ = await RpcErrors.CallAsync(control.SetAboutAsync(new SetAboutRequest { SessionId = LiveSidecar.LiveSessionId, Text = text }, cancellationToken: t)).ConfigureAwait(false);
					},
					Stimulus = () => $"{AboutChange.StimulusPrefix}{LiveClock.Time.GetUtcNow():yyyyMMddHHmmss}",
					CallBound = AboutChange.CallBound,
					Watch = LiveChecks.Watch,
					Time = LiveClock.Time,
					Log = line => live.Write($"live: {line}"),
				},
				ct
			)
			.ConfigureAwait(false);
}
