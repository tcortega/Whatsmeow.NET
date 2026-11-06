using System.Collections.Immutable;
using System.Runtime.ExceptionServices;
using Whatsmeow.Contacts;
using Whatsmeow.Events;
using Whatsmeow.History;
using Whatsmeow.IntegrationTests.Contacts;
using Whatsmeow.IntegrationTests.Pairing;
using Whatsmeow.IntegrationTests.Sessions;
using Whatsmeow.LiveTests.Control;
using Whatsmeow.Pairing;

namespace Whatsmeow.LiveTests;

/// <summary>
/// One fresh companion of the peer's account, paired by QR code on opt-in (<see cref="LiveFresh"/>, ADR 0060), for what a
/// new store has not cached yet, checked before each stimulus: the Contacts FullCycle's changes, checked with the
/// fake-mode tests' own records (<see cref="ContactChecks"/>), and #42's history bounds per sync type
/// (<see cref="LiveHistoryBounds"/>). The live
/// session (the business account) is the other side.
/// <para>
/// Branches 2 (business name), 1 (push name), 3 and 4 (the run's group photo set and removed) run. Branch 5 (about)
/// needs an account-wide profile change that is not authorized, and branch 6 (the saved name read back) an expected value
/// only the owner can give, so both are written to the log as not run: this test is not the FullCycle passing live, and
/// #36 stays open for Contacts until they run.
/// </para>
/// <para>
/// The companion's history is held from its first history event until branch 1 ended, so the sidecar downloads no
/// further history blob, in particular no push names, before the push name branch [observed: the sidecar's history
/// worker, ADR 0035]. It does not make the push name new: a push name blob that came first fills the row, and the branch
/// then fails on its precondition. Every branch runs whatever an earlier one did, the hold is released and #42's window
/// runs to its end either way, and every failure is reported at the end. A failed identity proof stops the companion's
/// sidecar before any change and logs nothing out.
/// </para>
/// </summary>
[Collection(nameof(Live))]
public sealed class LiveFreshCompanionTests(LiveSidecar live, LivePeer peer, LiveGroup group) : IClassFixture<LiveGroup>
{
	[LiveFact(NeedsPeer = true, NeedsFreshPairing = true)]
	public async Task Live_AFreshCompanion_SeesTheContactChangesAsTheFakeModeChecks_AndRecordsItsHistoryBounds()
	{
		var ct = TestContext.Current.CancellationToken;
		var business = await live.LiveAsync(ct);
		var businessAccount = await live.AccountAsync(ct);
		_ = await peer.ConnectedAsync(live, ct);
		var peerAccount = await peer.AccountAsync(ct);
		Assert.True(businessAccount is { Chat.IsPhoneNumber: true, Lid.IsLid: true }, "the live account has no phone number and LID pair");

		using var bounds = new LiveHistoryBounds(LiveClock.Time, LiveSettings.HistoryBounds);
		var failures = new List<ExceptionDispatchInfo>();
		var proven = false;
		try
		{
			await using var fresh = await LiveFresh.StartAsync(live, bounds, ct);
			var client = fresh.Client;
			// #42's handlers first, so they date each history event's arrival, not the hold's release.
			bounds.Register(client);
			var hold = new Hold(bounds);
			using var holding = hold.Register(client);
			using var traffic = new BusinessTraffic(client, businessAccount);
			try
			{
				await PairAsync(client, bounds, ct);
				if (await fresh.ProveAsync(live, peer, ct) is { } rejected)
				{
					bounds.FreshIdentityRejected();
					live.Write($"live: fresh companion rejected ({rejected}): its sidecar stops, nothing is logged out, the database stays");
					Assert.Fail($"the fresh companion is not the run's own peer device: {rejected}");
				}

				bounds.FreshIdentityVerified();
				proven = true;

				await RunAsync(failures, "catch up", () => live.StayUntilCaughtUpAsync(client, ct));
				await RunAsync(failures, "branch 2, business name", () => BusinessNameAsync(client, businessAccount, traffic, ct));
				await RunAsync(failures, "branch 1, push name", () => PushNameAsync(client, business, businessAccount, peerAccount, ct));
				hold.Release();
				await RunAsync(failures, "branches 3 and 4, picture", () => PictureAsync(client, ct));
				live.Write("live: branch 5, about: not run, no authorized stimulus (ADR 0060)");
				live.Write("live: branch 6, saved name read back: not run, no expected value from the owner (ADR 0060)");

				live.Write($"live: history window ended {await bounds.WindowAsync(ct)}");
			}
			finally
			{
				hold.Release();
				if (proven)
				{
					var unrestored = await LiveRestore.RunAsync(live, ("log the fresh companion out and delete it", t => fresh.LogoutAsync(live, peer, t)));
					if (unrestored.Count == 0)
						bounds.LogoutVerified();
					failures.AddRange(unrestored.Select(u => ExceptionDispatchInfo.Capture(new InvalidOperationException(LiveRedaction.Redact(u)))));
				}
			}
		}
		finally
		{
			bounds.Summarize();
		}

		if (failures.Count == 1)
			failures[0].Throw();
		if (failures.Count > 1)
			throw new AggregateException(failures.Select(f => f.SourceException));
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

	/// <summary>Branch 1: the business's text carries its push name, which the companion's store did not have.</summary>
	private async Task PushNameAsync(IWhatsAppClient client, IWhatsAppClient business, (Jid Chat, Jid? Lid) businessAccount, (Jid Chat, Jid? Lid) peerAccount, CancellationToken ct)
	{
		var rows = await RowsAsync(client, businessAccount, ct).ConfigureAwait(false);
		live.Write($"live: before branch 1, the business's rows: {Describe(rows)}");
		var id = MessageId.From($"WMNFRESH{LiveClock.Time.GetUtcNow():yyyyMMddHHmmss}");
		const string Text = "wmn fresh companion";
		var arrival = LiveChecks.ArrivalAsync(client, id, ct);
		var change = client.WaitForAsync<ContactUpdated>(e => LiveChecks.Names(businessAccount, e.Jid) && e.Change is ContactChange.PushName, LiveChecks.Watch, ct);
		_ = await business.SendTextAsync(peerAccount.Chat, Text, id, cancellationToken: ct).ConfigureAwait(false);

		var got = await LiveChecks.WithinAsync(arrival, "the business's text").ConfigureAwait(false);
		LiveChecks.Equal(LiveChecks.Received(client, got, businessAccount, id, new TextContent { Text = Text }), got);
		live.Write($"live: branch 1's text came from a {LiveChecks.Form(got.Source.Sender)} with a {LiveChecks.Form(got.Source.SenderAlt)} beside it");
		var pushName = got.PushName;
		Assert.NotNull(pushName);
		Assert.NotEmpty(pushName);

		var sender = got.Source.Sender with { Device = 0 };
		Assert.True(rows.SingleOrDefault(r => r.Jid == sender)?.PushName is null, "pushname_precondition_spent: the sender's row had a push name before the text");
		// whatsmeow names the other form from the message, else from the store's pair (user.go:688-691).
		var alt = got.Source.SenderAlt is { } senderAlt ? senderAlt with { Device = 0 } : sender.IsLid ? businessAccount.Chat : businessAccount.Lid;

		LiveChecks.Equal(ContactChecks.PushName(client.SessionId, sender, alt, pushName), await LiveChecks.WithinAsync(change, "the push name change").ConfigureAwait(false));
	}

	/// <summary>Branches 3 and 4: the live session sets the run's group photo, then removes it; the companion is a member's device.</summary>
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
				var answer = await control.SetGroupPhotoAsync(setPhoto, cancellationToken: ct).ConfigureAwait(false);
				Assert.NotEmpty(answer.PictureId);
				LiveChecks.Equal(ContactChecks.Picture(client.SessionId, jid, answer.PictureId), await LiveChecks.WithinAsync(set, "the group photo change").ConfigureAwait(false));

				var removed = client.WaitForAsync<ContactUpdated>(e => e.Jid == jid && e.Change is ContactChange.Picture { Id: null }, LiveChecks.Watch, ct);
				_ = await control.SetGroupPhotoAsync(removePhoto, cancellationToken: ct).ConfigureAwait(false);
				photoSet = false;
				LiveChecks.Equal(ContactChecks.Picture(client.SessionId, jid, id: null), await LiveChecks.WithinAsync(removed, "the group photo removal").ConfigureAwait(false));
			},
			(
				"remove the group photo",
				async t =>
				{
					if (photoSet)
						_ = await control.SetGroupPhotoAsync(removePhoto, cancellationToken: t).ConfigureAwait(false);
				}
		)
		).ConfigureAwait(false);
	}

	/// <summary>Pairs by QR code, the codes in <c>qr.png</c>, telling #42's record when it is paired.</summary>
	private async Task PairAsync(IWhatsAppClient client, LiveHistoryBounds bounds, CancellationToken ct)
	{
		var updates = new List<PairingUpdate>();
		await foreach (var update in client.PairWithQrCodeAsync(ct).ConfigureAwait(false))
		{
			updates.Add(update);
			if (update is PairingUpdate.Paired)
				bounds.Paired();
			if (update is not PairingUpdate.QrCode qrCode)
				continue;
			if (updates.Count == 1)
				PairingChecks.ValidatePairingStarted(await SessionSetup.StateAsync(client, ct).ConfigureAwait(false));
			await live.ShowQrCodeAsync(qrCode.Code, ct).ConfigureAwait(false);
		}

		live.DeleteShown();
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

	/// <summary>Runs one step; its failure is kept for the end, so the later steps, the hold's release and #42's window still run.</summary>
	private async Task RunAsync(List<ExceptionDispatchInfo> failures, string step, Func<Task> run)
	{
		try
		{
			await run().ConfigureAwait(false);
			live.Write($"live: {step}: passed");
		}
#pragma warning disable CA1031 // Justification: rethrown at the end of the test, after the other steps and the cleanup ran.
		catch (Exception e)
#pragma warning restore CA1031
		{
			live.Write($"live: {step}: failed, {LiveRestore.Describe(e)}");
			failures.Add(ExceptionDispatchInfo.Capture(e));
		}
	}

	/// <summary>
	/// Holds the companion's history lane from its first history event until <see cref="Release"/>, so the sidecar keeps
	/// at most its window of history events unacked and downloads no further blob (ADR 0035). An event that comes after
	/// the release is not held.
	/// </summary>
	private sealed class Hold(LiveHistoryBounds bounds)
	{
		private readonly TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);
		private readonly Lock _lock = new();
		private bool _started;

		public IDisposable Register(IWhatsAppClient client) =>
			new Both(client.On<HistoryReceived>((_, ct) => WaitAsync(ct)), client.On<HistorySyncProgressed>((_, ct) => WaitAsync(ct)));

		public void Release()
		{
			lock (_lock)
			{
				if (!_released.TrySetResult() || !_started)
					return;
			}

			bounds.HoldEnd();
		}

		private async ValueTask WaitAsync(CancellationToken cancellationToken)
		{
			lock (_lock)
			{
				if (_released.Task.IsCompleted)
					return;
				if (!_started)
				{
					_started = true;
					bounds.HoldStart();
				}
			}

			await _released.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
		}

		private sealed class Both(IDisposable first, IDisposable second) : IDisposable
		{
			public void Dispose()
			{
				first.Dispose();
				second.Dispose();
			}
		}
	}

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
