using System.Collections.Immutable;
using Whatsmeow.Events;
using Whatsmeow.IntegrationTests.Contacts;
using Whatsmeow.IntegrationTests.Support;
using Whatsmeow.Presence;
using Whatsmeow.Sessions;

namespace Whatsmeow.LiveTests;

/// <summary>
/// #36's Contacts FullCycle on the persistent sessions, on opt-in only (ADR 0062): the peer (the owner's personal account)
/// is the receiver of every branch, and the live session (the business account) the subject, checked with the fake-mode
/// tests' own records (<see cref="ContactChecks"/>) through the shared <see cref="ContactsCycle"/>, which its fake-mode
/// tests check. No QR code, no new session, no logout, no deletion, no profile change.
/// <para>
/// The persistent stores have seen the business: its push name is on both of the peer's rows [observed: ADR 0061's run of
/// 2026-11-06], and at least one of the peer's rows has its business name [observed: the #36 preflight of 2026-11-06].
/// whatsmeow dispatches those two changes only when the stored value changes (user.go 679-736) [observed: source], so the
/// push name branch fails <c>pushname_precondition_spent</c> by design here, and the business name branch fails
/// <c>businessname_precondition_spent</c> unless the read before it finds a row with none. Those failures are the
/// verdict, not a skip: this test passes only when every branch passed.
/// </para>
/// <para>
/// Branch 5 (about) needs an account-wide profile change that is not authorized, so it fails <c>about_not_authorized</c>
/// with no stimulus. Branch 6 compares the peer's phone number row of the business whole, with the saved name the owner
/// stated (<see cref="LiveSettings.PeerSavedNameVariable"/>), and the names this run's text and answer carried; without
/// one it fails, and the store's value never stands in.
/// </para>
/// <para>
/// It runs alone, with a filter on this one method (docs/guidelines/tests.md): it fails <c>contacts_not_alone</c> before
/// anything starts when a test of the run already used the peer. It reads both accounts before connecting, which never
/// creates a session. The business's traffic is counted from right after the peer's catch-up; a message delivered before
/// that whose business name whatsmeow stores on a goroutine of its own (message.go 56-57) could still change a row after
/// the read [inferred], a window the count cannot close. It leaves the live session unavailable, the state the other live
/// tests leave it in, not a restore of one read; and it checks after the body that neither session changed its account
/// or device and that no pairing file was written.
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
		// Neither creates a session: against a database without the session they fail, where LiveAsync would add one.
		var businessAccount = await live.AccountAsync(ct);
		Assert.True(businessAccount is { Chat.IsPhoneNumber: true, Lid.IsLid: true }, "the live account has no phone number and LID pair");
		var liveBefore = Identities(await live.SessionsAsync(ct));
		var peerBefore = Identities(await peer.SessionsAsync(live, ct));

		var business = await live.LiveAsync(ct);
		var receiver = await peer.ConnectedAsync(live, ct);
		var peerAccount = await peer.AccountAsync(ct);
		using var traffic = new ContactsTraffic(receiver, businessAccount);
		live.Write($"live: contacts fullcycle, the live store's rows of the peer: {ContactsCycle.Describe(await ContactsCycle.RowsAsync(business, peerAccount, ct))}");
		var others = (await receiver.GetContactsAsync(ct)).Where(c => LiveChecks.Names(businessAccount, c.Jid) && c.Jid != businessAccount.Chat && c.Jid != businessAccount.Lid).ToArray();
		live.Write($"live: contacts fullcycle, the peer's other rows naming the business: {others.Length}{string.Concat(others.Select(c => $", a {LiveChecks.Form(c.Jid)}"))}");

		await LiveRestore.AfterAsync(
			live,
			() =>
				ContactsCycle.RunAsync(
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
							new FreshStep { Name = "branch 5, about", Run = AboutAsync },
						],
						SavedName = LiveSettings.PeerSavedName,
						Redacting = LiveChecks.Redacting,
						Log = line => live.Write($"live: {line}"),
						Watch = LiveChecks.Watch,
					},
					ct
				),
			("leave the live session unavailable", t => business.SendPresenceAsync(PresenceState.Unavailable, t)),
			(
				"postflight: both sessions kept their account and device, and no pairing file was written",
				async t =>
				{
					var liveAfter = Identities(await live.SessionsAsync(t).ConfigureAwait(false));
					var peerAfter = Identities(await peer.SessionsAsync(live, t).ConfigureAwait(false));
					var pairingFiles = s_pairingFiles.Count(f => File.Exists(Path.Combine(live.OutputDirectory, f)));
					live.Write(
						$"live: contacts fullcycle postflight: live sessions unchanged {liveAfter.SequenceEqual(liveBefore)}, peer sessions unchanged {peerAfter.SequenceEqual(peerBefore)}, pairing files {pairingFiles}"
					);
					Assert.True(liveAfter.SequenceEqual(liveBefore) && peerAfter.SequenceEqual(peerBefore) && pairingFiles == 0, "postflight_identity_changed: a session's account or device changed, or a pairing file was written");
				}
		)
		);
	}

	/// <summary>Branch 5: no authorized stimulus, so it fails, never passes and never changes a profile (ADR 0062).</summary>
	private static Task AboutAsync(CancellationToken _)
	{
		Assert.Fail("about_not_authorized: changing the business account's about text needs the owner's consent, which this run does not have, so branch 5 was not run");
		return Task.CompletedTask;
	}

	/// <summary>Each session's id, account and device, never its state, which a connection changes.</summary>
	private static ImmutableArray<(SessionId Id, Jid? Jid, Jid? Lid)> Identities(ImmutableArray<SessionInfo> sessions) =>
		[.. sessions.Select(s => (s.Id, s.Jid, s.Lid)).OrderBy(s => s.Id.Value, StringComparer.Ordinal)];
}
