using System.Collections.Immutable;
using Whatsmeow.Contacts;
using Whatsmeow.Core.Errors;
using Whatsmeow.Events;
using Whatsmeow.IntegrationTests.Contacts;
using Whatsmeow.IntegrationTests.Pairing;
using Whatsmeow.IntegrationTests.Sessions;
using Whatsmeow.IntegrationTests.Support;
using Whatsmeow.LiveTests.Control;
using Whatsmeow.Pairing;
using Whatsmeow.Presence;
using Whatsmeow.Sessions;

namespace Whatsmeow.LiveTests;

/// <summary>
/// One fresh companion of the peer's account, paired by QR code on opt-in (<see cref="LiveFresh"/>, ADR 0060), for the
/// Contacts FullCycle's changes a new store has not cached yet, checked before each stimulus with the fake-mode tests'
/// own records (<see cref="ContactChecks"/>), and #42's history bounds per sync type (<see cref="LiveHistoryBounds"/>).
/// The live session (the business account) is the other side. The proof, the steps' failures, the capture bound and
/// the cleanup are the shared <see cref="FreshCompanion"/>'s, which its fake-mode tests check.
/// <para>
/// Branches 2 (business name, before any message from the business account) and 1 (push name) run as soon as the
/// session is Connected, before it waits for CaughtUp: the sidecar's history worker starts only at OfflineSyncCompleted
/// and downloads only while the session is online [observed: <c>history.go</c> 221-222, 403-424], and downloading a
/// <c>PUSH_NAME</c> blob stores its push names with no event [observed: whatsmeow message.go 806-807]. The history is
/// also held from its first event, which stops recording once 4 events are unacked; it does not stop a download: the
/// blob being recorded, and any whose events fit the window, are downloaded first [inferred: <c>history.go</c> 583,
/// 660, 746, 773-785]. So a push name can still be cached before branch 1, and its precondition then fails the branch;
/// no first sight is promised, and no second scan follows. Branches 3 and 4 (the run's group photo set and removed)
/// run after. Branch 5 (about) needs an account-wide profile change that is not authorized, and branch 6 (the saved name
/// read back) an expected value only the owner can give, so both are written as not run: this test is not the
/// FullCycle passing live, and #36 stays open for Contacts.
/// </para>
/// </summary>
[Collection(nameof(Live))]
public sealed class LiveFreshCompanionTests(LiveSidecar live, LivePeer peer, LiveGroup group) : IClassFixture<LiveGroup>
{
	[LiveFact(NeedsPeer = true, NeedsFreshPairing = true)]
	public async Task Live_AFreshCompanion_SeesTheContactChangesAsTheFakeModeChecks_AndRecordsItsHistoryBounds()
	{
		var ct = TestContext.Current.CancellationToken;
		// First: a missing or malformed days limit fails here, before the test starts a sidecar or creates a file.
		var daysLimit = LiveSettings.HistoryDaysLimit;
		var business = await live.LiveAsync(ct);
		var businessAccount = await live.AccountAsync(ct);
		_ = await peer.ConnectedAsync(live, ct);
		var peerAccount = await peer.AccountAsync(ct);
		Assert.True(businessAccount is { Chat.IsPhoneNumber: true, Lid.IsLid: true }, "the live account has no phone number and LID pair");
		var others = (await live.SessionsAsync(ct))
			.AddRange(await peer.SessionsAsync(live, ct))
			.SelectMany(s => new[] { s.Jid, s.Lid })
			.OfType<Jid>()
			.ToImmutableArray();

		using var bounds = new LiveHistoryBounds(LiveClock.Time, LiveSettings.HistoryBounds);
		try
		{
			await using var fresh = await LiveFresh.StartAsync(live, bounds, daysLimit, ct);
			var client = fresh.Companion.Client;
			// #42's handlers first, so they stamp each history event as the handlers see it, before the hold.
			bounds.Register(client);
			fresh.Companion.HoldHistory(bounds.HoldStart, bounds.HoldEnd);
			using var traffic = new BusinessTraffic(client, businessAccount);

			await fresh.Companion.RunAsync(
				new FreshRun
				{
					PairAsync = (companion, token) => PairAsync(companion, bounds, token),
					Expected = peerAccount,
					Forbidden = businessAccount,
					OtherDevices = others,
					KnownDevices = LiveSettings.KnownDevices,
					FirstSight =
					[
						new FreshStep { Name = "branch 2, business name", Run = token => BusinessNameAsync(client, businessAccount, traffic, token) },
						new FreshStep { Name = "branch 1, push name", Run = token => PushNameAsync(client, business, businessAccount, peerAccount, token) },
					],
					Then =
					[
						new FreshStep { Name = "catch up", Run = token => live.StayUntilCaughtUpAsync(client, token) },
						new FreshStep { Name = "branches 3 and 4, picture", Run = token => PictureAsync(client, token) },
					],
					Deadline = bounds.Deadline,
					WindowAsync = bounds.WindowAsync,
					Record = e => Record(bounds, e),
				},
				ct
			);
		}
		finally
		{
			live.Write("live: branch 5, about: not run, no authorized stimulus (ADR 0060)");
			live.Write("live: branch 6, saved name read back: not run, no expected value from the owner (ADR 0060)");
			bounds.Summarize();
		}
	}

	private static void Record(LiveHistoryBounds bounds, FreshEvent recorded)
	{
		switch (recorded)
		{
			case FreshEvent.IdentityVerified:
				bounds.FreshIdentityVerified();
				break;
			case FreshEvent.IdentityRejected:
				bounds.FreshIdentityRejected();
				break;
			case FreshEvent.LogoutVerified:
				bounds.LogoutVerified();
				break;
			case FreshEvent.LoggedOutExternally:
				bounds.LoggedOutExternally();
				break;
			default:
				throw new ArgumentOutOfRangeException(nameof(recorded), recorded, "an event the record does not know");
		}
	}

	/// <summary>Branch 2: reading the business's profile by its phone number stores its verified name, the first time.</summary>
	private async Task BusinessNameAsync(IWhatsAppClient client, (Jid Chat, Jid? Lid) businessAccount, BusinessTraffic traffic, CancellationToken ct)
	{
		var rows = await RowsAsync(client, businessAccount, ct).ConfigureAwait(false);
		live.Write($"live: before branch 2, the business's rows: {Describe(rows)}; its messages since pairing: {traffic.Messages}, undecryptable: {traffic.Undecryptable}");
		Assert.True(rows.All(r => r.BusinessName is null), "businessname_precondition_spent: the business name is stored already");
		Assert.True(traffic is { Messages: 0, Undecryptable: 0, Changes: 0 }, "businessname_origin_unclear: the business sent something before the query");

		var change = client.WaitForAsync<ContactUpdated>(e => LiveChecks.Names(businessAccount, e.Jid) && e.Change is ContactChange.BusinessName, LiveChecks.Watch, ct);
		var answer = await client.GetUserInfoAsync([businessAccount.Chat], ct).ConfigureAwait(false);
		var name = Assert.Single(answer, a => a.Jid == businessAccount.Chat).VerifiedBusinessName;
		Assert.NotNull(name);
		Assert.NotEmpty(name);

		LiveChecks.Equal(ContactChecks.BusinessName(client.SessionId, businessAccount.Chat, name), await LiveChecks.WithinAsync(change, "the business name change").ConfigureAwait(false));
	}

	/// <summary>
	/// Branch 1: the business's text carries its push name, which the companion's store did not have. The event's other
	/// form is the message's <c>SenderAlt</c> when it has one, else what the store pairs the sender with, read before the
	/// send with the same lookup whatsmeow makes (<c>Device.GetAltJID</c>: contacts.go 252, whatsmeow user.go 688-691).
	/// On 2026-11-06 at 20:55 UTC the text, sent by the live session, a linked companion, arrived with no push name, and
	/// that run sent no named presence first [inferred: no <c>SendPresence</c> call in its log]. At 23:39 UTC, on the
	/// persistent peer, a text before any presence on a connection the test opened anew carried none and the next, after
	/// a named unavailable presence, carried one (ARCHITECTURE §14, docs/evidence/0036-pushname-stimulus-2026-11-06.md)
	/// [observed]. So the live session sends a named unavailable presence just before this text, after the reads and the
	/// waits; a refusal logs its type and code and fails the branch with no text. On 2026-11-07 at 01:38 UTC the text then
	/// carried one and the branch passed (docs/evidence/0042-history-days-limit-2026-11-07.md) [observed]; that the
	/// presence caused it is [inferred]. Whether the text carried one is logged before any check, and one without fails
	/// the branch as a missing stimulus, never a skip. The live session is left unavailable, the state chosen here, not a
	/// restore.
	/// </summary>
	private async Task PushNameAsync(IWhatsAppClient client, IWhatsAppClient business, (Jid Chat, Jid? Lid) businessAccount, (Jid Chat, Jid? Lid) peerAccount, CancellationToken ct)
	{
		var rows = await RowsAsync(client, businessAccount, ct).ConfigureAwait(false);
		var pairs = await client.ResolveJidsAsync([businessAccount.Chat, businessAccount.Lid!.Value], ct).ConfigureAwait(false);
		live.Write($"live: before branch 1, the business's rows: {Describe(rows)}; the store pairs: {string.Join(", ", pairs.Select(p => $"{LiveChecks.Form(p.Jid)} with {LiveChecks.Form(p.JidAlt)}"))}");
		LiveChecks.Redacting(() =>
			Assert.All(pairs, p => Assert.True(p.JidAlt is null || IsOtherForm(businessAccount, p.Jid, p.JidAlt.Value), "the store pairs the business with another account or the same form"))
		);

		var id = MessageId.From($"WMNFRESH{LiveClock.Time.GetUtcNow():yyyyMMddHHmmss}");
		const string Text = "wmn fresh companion";
		var arrival = LiveChecks.ArrivalAsync(client, id, ct);
		var change = client.WaitForAsync<ContactUpdated>(e => LiveChecks.Names(businessAccount, e.Jid) && e.Change is ContactChange.PushName, LiveChecks.Watch, ct);
		// Named, as whatsmeow sends a presence without a MessengerConfig, which the sidecar never sets (presence.go 64-87);
		// unavailable is the state the live session is left in.
		try
		{
			await business.SendPresenceAsync(PresenceState.Unavailable, ct).ConfigureAwait(false);
		}
		catch (WhatsmeowException e)
		{
			live.Write($"live: branch 1's named unavailable presence: refused, {e.GetType().Name} {e.ErrorCode}; no text sent");
			throw;
		}

		live.Write("live: branch 1's named unavailable presence: sent");
		_ = await business.SendTextAsync(peerAccount.Chat, Text, id, cancellationToken: ct).ConfigureAwait(false);

		var got = await LiveChecks.WithinAsync(arrival, "the business's text").ConfigureAwait(false);
		var pushName = got.PushName;
		live.Write($"live: branch 1's text carried a push name: {pushName is not null}");
		LiveChecks.Equal(LiveChecks.Received(client, got, businessAccount, id, new TextContent { Text = Text }), got);
		live.Write($"live: branch 1's text came from a {LiveChecks.Form(got.Source.Sender)} with a {LiveChecks.Form(got.Source.SenderAlt)} beside it");
		Assert.True(
			pushName is not null,
			"pushname_stimulus_missing: the business companion's text carried no push name, so branch 1 has no stimulus and #36's push-name branch stays unfulfilled live"
		);
		Assert.NotEmpty(pushName);

		var sender = got.Source.Sender with { Device = 0 };
		Assert.True(rows.SingleOrDefault(r => r.Jid == sender)?.PushName is null, "pushname_precondition_spent: the sender's row had a push name before the text");
		var senderAlt = got.Source.SenderAlt is { } a ? a with { Device = 0 } : (Jid?)null;
		Assert.True(senderAlt is null || IsOtherForm(businessAccount, sender, senderAlt.Value), "the text's other form is not the business's other form");
		var alt = senderAlt ?? LiveChecks.Redacting(() => Assert.Single(pairs, p => p.Jid == sender).JidAlt);

		LiveChecks.Equal(ContactChecks.PushName(client.SessionId, sender, alt, pushName), await LiveChecks.WithinAsync(change, "the push name change").ConfigureAwait(false));
	}

	/// <summary>Whether <paramref name="alt"/> names <paramref name="account"/> in the form <paramref name="jid"/> is not.</summary>
	private static bool IsOtherForm((Jid Chat, Jid? Lid) account, Jid jid, Jid alt) => LiveChecks.Names(account, alt) && alt.IsLid != jid.IsLid;

	/// <summary>
	/// Branches 3 and 4: the live session sets the run's group photo, then removes it; the companion is a member's
	/// device. A set whose answer is unknown is never sent again; the restore removes the photo if the set may have
	/// happened and the removal did not. A failed call is the client's exception, with its reason (<see cref="RpcErrors"/>).
	/// </summary>
	private async Task PictureAsync(IWhatsAppClient client, CancellationToken ct)
	{
		var jid = await group.WithPeerAsync(ct).ConfigureAwait(false);
		var control = await live.ControlAsync(ct).ConfigureAwait(false);
		var setPhoto = new SetGroupPhotoRequest
		{
			SessionId = LiveSidecar.LiveSessionId,
			Group = jid.ToString(),
			JpegPath = Path.Combine(AppContext.BaseDirectory, "group-photo.jpg"),
		};
		var removePhoto = new SetGroupPhotoRequest { SessionId = LiveSidecar.LiveSessionId, Group = jid.ToString() };
		var photoSet = false;

		await LiveRestore.AfterAsync(
			live,
			async () =>
			{
				var set = client.WaitForAsync<ContactUpdated>(e => e.Jid == jid && e.Change is ContactChange.Picture { Id: not null }, LiveChecks.Watch, ct);
				// Set before the call: an answer lost on the way may still have set the photo.
				photoSet = true;
				var answer = await RpcErrors.CallAsync(control.SetGroupPhotoAsync(setPhoto, cancellationToken: ct)).ConfigureAwait(false);
				Assert.NotEmpty(answer.PictureId);
				LiveChecks.Equal(ContactChecks.Picture(client.SessionId, jid, answer.PictureId), await LiveChecks.WithinAsync(set, "the group photo change").ConfigureAwait(false));

				var removed = client.WaitForAsync<ContactUpdated>(e => e.Jid == jid && e.Change is ContactChange.Picture { Id: null }, LiveChecks.Watch, ct);
				_ = await RpcErrors.CallAsync(control.SetGroupPhotoAsync(removePhoto, cancellationToken: ct)).ConfigureAwait(false);
				photoSet = false;
				LiveChecks.Equal(ContactChecks.Picture(client.SessionId, jid, id: null), await LiveChecks.WithinAsync(removed, "the group photo removal").ConfigureAwait(false));
			},
			(
				"remove the group photo",
				async t =>
				{
					if (photoSet)
						_ = await RpcErrors.CallAsync(control.SetGroupPhotoAsync(removePhoto, cancellationToken: t)).ConfigureAwait(false);
				}
		)
		).ConfigureAwait(false);
	}

	/// <summary>
	/// Pairs by QR code, the codes in <c>qr.png</c>, telling the companion and #42's record when it is paired, then waits
	/// for the first of Connected and CaughtUp, so the first sight steps start as early as the session can use them.
	/// </summary>
	private async Task PairAsync(FreshCompanion companion, LiveHistoryBounds bounds, CancellationToken ct)
	{
		var client = companion.Client;
		var updates = new List<PairingUpdate>();
		await foreach (var update in client.PairWithQrCodeAsync(ct).ConfigureAwait(false))
		{
			updates.Add(update);
			if (update is PairingUpdate.Paired)
			{
				companion.Paired();
				bounds.Paired();
			}

			if (update is not PairingUpdate.QrCode qrCode)
				continue;
			if (updates.Count == 1)
				PairingChecks.ValidatePairingStarted(await SessionSetup.StateAsync(client, ct).ConfigureAwait(false));
			await live.ShowQrCodeAsync(qrCode.Code, ct).ConfigureAwait(false);
		}

		live.DeleteShown();
		var watch = StateWatch.Start(client, ct);
		await using (watch.ConfigureAwait(false))
		{
			var state = await watch.NextAsync().ConfigureAwait(false);
			while (state is SessionState.Connecting or SessionState.Reconnecting)
				state = await watch.NextAsync().ConfigureAwait(false);
			Assert.True(state is SessionState.Connected or SessionState.CaughtUp, $"the paired session is {state.GetType().Name}, not usable");
		}

		PairingChecks.ValidateQrPairing(updates, await SessionSetup.StateAsync(client, ct).ConfigureAwait(false));
	}

	/// <summary>The business's rows in the companion's contacts, by its phone number and its LID; never the others.</summary>
	private static async Task<ImmutableArray<Contact>> RowsAsync(IWhatsAppClient client, (Jid Chat, Jid? Lid) account, CancellationToken ct) =>
		[.. (await client.GetContactsAsync(ct).ConfigureAwait(false)).Where(c => c.Jid == account.Chat || c.Jid == account.Lid)];

	/// <summary>Which of a row's names are set, never the names.</summary>
	private static string Describe(ImmutableArray<Contact> rows) =>
		rows.IsEmpty
			? "none"
			: string.Join(", ", rows.Select(r => $"{LiveChecks.Form(r.Jid)}: saved {r.SavedName is not null}, push {r.PushName is not null}, business {r.BusinessName is not null}"));

	/// <summary>What the companion got from the business since pairing: its messages, undecryptable ones, and business name changes.</summary>
	private sealed class BusinessTraffic : IDisposable
	{
		private readonly IDisposable[] _handlers;
		private int _messages;
		private int _undecryptable;
		private int _changes;

		public BusinessTraffic(IWhatsAppClient client, (Jid Chat, Jid? Lid) account) =>
			_handlers =
			[
				client.On<MessageReceived>((e, _) => CountAsync(LiveChecks.Names(account, e.Source.Sender), ref _messages)),
				client.On<UndecryptableMessageReceived>((e, _) => CountAsync(LiveChecks.Names(account, e.Source.Sender), ref _undecryptable)),
				client.On<ContactUpdated>((e, _) => CountAsync(LiveChecks.Names(account, e.Jid) && e.Change is ContactChange.BusinessName, ref _changes)),
			];

		public int Messages => Volatile.Read(ref _messages);

		public int Undecryptable => Volatile.Read(ref _undecryptable);

		public int Changes => Volatile.Read(ref _changes);

		public void Dispose()
		{
			foreach (var handler in _handlers)
				handler.Dispose();
		}

		private static ValueTask CountAsync(bool matches, ref int count)
		{
			if (matches)
				_ = Interlocked.Increment(ref count);
			return ValueTask.CompletedTask;
		}
	}
}
