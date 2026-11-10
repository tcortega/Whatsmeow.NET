using System.Security.Cryptography;
using System.Text.Json;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.Time.Testing;
using Whatsmeow.Events;
using Whatsmeow.History;
using Whatsmeow.IntegrationTests.Fake;
using Whatsmeow.IntegrationTests.History;
using Whatsmeow.Sidecar;
using Conversation = Whatsmeow.IntegrationTests.Fake.DeliverHistorySyncRequest.Types.Conversation;
using Row = Whatsmeow.IntegrationTests.Support.LiveHistoryBounds.Row;
using WebMessage = Whatsmeow.IntegrationTests.Fake.DeliverHistorySyncRequest.Types.Message;

namespace Whatsmeow.IntegrationTests.Support;

/// <summary>
/// The live tests' history bounds record (<see cref="LiveHistoryBounds"/>, #42), which no live run can check for
/// itself: its window rules on a <see cref="FakeTimeProvider"/>, with the history the fake phone sends through the shared
/// fake-mode sidecar to a session of each test's own, read back from the file the analyzer reads.
/// </summary>
public sealed class LiveHistoryBoundsTests(FakeSidecar fixture, SidecarBinaries binaries)
{
	private const int Timeout = 120_000;

	private const string Chat = "5511930000201@s.whatsapp.net";

	private static readonly DateTimeOffset s_t0 = new(2026, 11, 7, 10, 0, 0, TimeSpan.Zero);

	private static readonly DateTimeOffset s_day = new(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);

	private static readonly TimeSpan s_tick = TimeSpan.FromTicks(1);

	[Fact(Timeout = Timeout)]
	public async Task FullCycle_AFreshPairing_RecordsEachSyncTypesBounds_AndClosesQuietAfterTheHold()
	{
		var ct = TestContext.Current.CancellationToken;
		const string Session = "bounds-fullcycle";
		var time = new FakeTimeProvider(s_t0);
		var path = NewPath();
		using var bounds = new LiveHistoryBounds(time, path);
		var client = await Histories.CaughtUpAsync(fixture, Session, ct);
		var info = await fixture.Sidecar.GetInfoAsync(ct);
		bounds.Provenance(info, fixture.Binary, "fakewhatsapp");
		bounds.Options(new HistorySyncOptions { RequestFullSync = true, FullSyncDaysLimit = 7 });
		bounds.Register(client);
		// #36's hold, after the record: the first history event blocks the lane until the release.
		var held = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var first = 0;
		using var hold = new Received<HistoryReceived>(client, async (_, token) =>
		{
			if (Interlocked.Increment(ref first) != 1)
				return;
			bounds.HoldStart();
			held.SetResult();
			await release.Task.WaitAsync(token).ConfigureAwait(false);
		});
		using var progress = new Received<HistorySyncProgressed>(client);
		bounds.Paired();
		bounds.FreshIdentityVerified();

		time.Advance(TimeSpan.FromSeconds(5));
		_ = await fixture.Fake.DeliverHistorySyncAsync(Blob(Session, "B1", 0, -9, -2, -5), cancellationToken: ct);
		await held.Task.WaitAsync(ct);
		time.Advance(TimeSpan.FromSeconds(85));
		// Sent while the lane is held: its handler sees it only after the release.
		_ = await fixture.Fake.DeliverHistorySyncAsync(Blob(Session, "R1", 3, -1), cancellationToken: ct);
		time.Advance(TimeSpan.FromSeconds(10));
		bounds.HoldEnd();
		release.SetResult();
		_ = await progress.NextAsync(2, ct);
		time.Advance(TimeSpan.FromSeconds(13));
		_ = await fixture.Fake.DeliverHistorySyncAsync(Blob(Session, "F1", 2, -30, -20), cancellationToken: ct);
		_ = await progress.NextAsync(ct);

		var window = bounds.WindowAsync(ct);
		time.Advance(LiveHistoryBounds.Quiet - s_tick);
		Assert.False(window.IsCompleted);
		time.Advance(s_tick);
		Assert.Equal("quiet_window", await window);
		time.Advance(TimeSpan.FromSeconds(3));
		_ = await fixture.Fake.DeliverHistorySyncAsync(Blob(Session, "L1", 3, 0), cancellationToken: ct);
		_ = await progress.NextAsync(ct);
		bounds.LogoutVerified();
		bounds.Summarize();

		var quiet = TimeSpan.FromSeconds(113) + LiveHistoryBounds.Quiet;
		var after = quiet + TimeSpan.FromSeconds(3);
		string sha;
		var binary = File.OpenRead(fixture.Binary);
		await using (binary.ConfigureAwait(false))
			sha = Convert.ToHexStringLower(await SHA256.HashDataAsync(binary, ct));
		Assert.Equal(
			[
				At("run_start", TimeSpan.Zero) with
				{
					Detail = $"build=fakewhatsapp sidecar_sha256={sha} version={info.Version} commit={info.Commit} whatsmeow={info.WhatsmeowVersion} go={info.GoVersion} protocol={info.Protocol}",
				},
				At("paired", TimeSpan.Zero),
				At("fresh_identity_verified", TimeSpan.Zero),
				Batch(TimeSpan.FromSeconds(5), "InitialBootstrap", 3, -9, -2),
				At("hold_start", TimeSpan.FromSeconds(5)),
				At("hold_end", TimeSpan.FromSeconds(100)) with { Detail = "hold_seconds=95.000" },
				Progress(TimeSpan.FromSeconds(100), "InitialBootstrap", 3),
				Batch(TimeSpan.FromSeconds(100), "Recent", 1, -1, -1),
				Progress(TimeSpan.FromSeconds(100), "Recent", 1),
				Batch(TimeSpan.FromSeconds(113), "Full", 2, -30, -20),
				Progress(TimeSpan.FromSeconds(113), "Full", 2),
				Finish(quiet, "quiet_window", quiet),
				Batch(after, "Recent", 1, 0, 0) with { AfterFinish = true },
				Progress(after, "Recent", 1) with { AfterFinish = true },
				At("logout_verified", after),
				Summary(after, "Full", 1, 2, -30, -20, TimeSpan.FromSeconds(113), TimeSpan.FromSeconds(113)),
				Summary(after, "InitialBootstrap", 1, 3, -9, -2, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5)),
				Summary(after, "Recent", 1, 1, -1, -1, TimeSpan.FromSeconds(100), TimeSpan.FromSeconds(100)),
				Captured(after, batches: 3, entries: 6, invalidations: 0, excluded: 2),
			],
			Rows(path)
		);
		Assert.Equal(
			new LiveHistoryBounds.OptionsRow
			{
				Utc = s_t0,
				RequestedFullSync = true,
				RequestedDaysLimit = 7,
				RequestedSizeLimitMb = null,
				RequestedStorageQuotaMb = null,
				ResolvedRequireFullSync = true,
				ResolvedDaysLimit = 7,
				ResolvedSizeLimitMb = null,
				ResolvedStorageQuotaMb = 10240,
				ResolvedBasis = "whatsmeow 307a2edf117d store/clientpayload.go:131-134,156; sidecar cmd/whatsmeow-net/history.go:30-51; client Sidecar/SidecarProcess.cs:229-233; not measured",
			},
			JsonSerializer.Deserialize(Lines(path)[1], LiveHistoryBounds.HistoryJson.Default.OptionsRow)
		);
		Assert.Equal(new LiveHistoryBounds.Receipt { Sha256 = Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(path, ct))), Lines = Lines(path).Length }, ReceiptOf(path));
		var text = await File.ReadAllTextAsync(path, ct);
		foreach (var secret in new[] { Session, "5511930000201", "@", "B1-", "R1-", "F1-", "history text" })
			Assert.DoesNotContain(secret, text, StringComparison.Ordinal);
		if (!OperatingSystem.IsWindows())
			Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
	}

	[Fact(Timeout = Timeout)]
	public async Task Hold_KeepsTheWindowFromClosingQuiet_ThenTheQuietCountsFromItsEnd()
	{
		var ct = TestContext.Current.CancellationToken;
		const string Session = "bounds-hold";
		var time = new FakeTimeProvider(s_t0);
		var path = NewPath();
		using var bounds = new LiveHistoryBounds(time, path);
		using var progress = await PairedAsync(Session, bounds, ct);

		time.Advance(TimeSpan.FromMinutes(1));
		await DeliverAsync(Session, progress, Blob(Session, "H1", 3, -1), ct);
		bounds.HoldStart();
		var window = bounds.WindowAsync(ct);
		time.Advance(TimeSpan.FromMinutes(12));
		Assert.False(window.IsCompleted);
		bounds.HoldEnd();
		time.Advance(LiveHistoryBounds.Quiet - s_tick);
		Assert.False(window.IsCompleted);
		time.Advance(s_tick);
		Assert.Equal("quiet_window", await window);
		bounds.Summarize();

		var end = TimeSpan.FromMinutes(13) + LiveHistoryBounds.Quiet;
		Assert.Equal(
			[
				At("paired", TimeSpan.Zero),
				Batch(TimeSpan.FromMinutes(1), "Recent", 1, -1, -1),
				Progress(TimeSpan.FromMinutes(1), "Recent", 1),
				At("hold_start", TimeSpan.FromMinutes(1)),
				At("hold_end", TimeSpan.FromMinutes(13)) with { Detail = "hold_seconds=720.000" },
				Finish(end, "quiet_window", end),
				Summary(end, "Recent", 1, 1, -1, -1, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1)),
				Captured(end, batches: 1, entries: 1, invalidations: 0, excluded: 0),
			],
			Rows(path)
		);
	}

	[Fact(Timeout = Timeout)]
	public async Task Bound_WinsOverAHoldPastIt_CancelsTheDeadlineThere_AndMarksLaterHistoryLate()
	{
		var ct = TestContext.Current.CancellationToken;
		const string Session = "bounds-bound";
		var time = new FakeTimeProvider(s_t0);
		var path = NewPath();
		using var bounds = new LiveHistoryBounds(time, path);
		using var progress = await PairedAsync(Session, bounds, ct);

		time.Advance(TimeSpan.FromMinutes(1));
		await DeliverAsync(Session, progress, Blob(Session, "P1", 3, -1), ct);
		bounds.HoldStart();
		time.Advance(TimeSpan.FromMinutes(24));
		bounds.HoldEnd();
		time.Advance(TimeSpan.FromMinutes(5) - s_tick);
		Assert.False(bounds.Deadline.IsCancellationRequested);
		time.Advance(s_tick);
		Assert.True(bounds.Deadline.IsCancellationRequested);
		// A branch past the bound: its history counts no more, and the window, awaited this late, closed at the bound.
		await DeliverAsync(Session, progress, Blob(Session, "P2", 2, -2), ct);
		time.Advance(TimeSpan.FromMinutes(15));
		Assert.Equal("censored_bound", await bounds.WindowAsync(ct));
		bounds.Summarize();

		var bound = LiveHistoryBounds.Bound;
		var end = bound + TimeSpan.FromMinutes(15);
		Assert.Equal(
			[
				At("paired", TimeSpan.Zero),
				Batch(TimeSpan.FromMinutes(1), "Recent", 1, -1, -1),
				Progress(TimeSpan.FromMinutes(1), "Recent", 1),
				At("hold_start", TimeSpan.FromMinutes(1)),
				At("hold_end", TimeSpan.FromMinutes(25)) with { Detail = "hold_seconds=1440.000" },
				Finish(bound, "censored_bound", bound),
				Batch(bound, "Full", 1, -2, -2) with { AfterFinish = true, Late = true },
				Progress(bound, "Full", 1) with { AfterFinish = true, Late = true },
				Summary(end, "Recent", 1, 1, -1, -1, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1)),
				Captured(end, batches: 1, entries: 1, invalidations: 0, excluded: 2),
			],
			Rows(path)
		);
	}

	[Fact(Timeout = Timeout)]
	public async Task Bound_ATimerThatFiresEarly_IsArmedAgain_AndTheWindowClosesAtTheBound()
	{
		var ct = TestContext.Current.CancellationToken;
		var clock = new ManualClock();
		var path = NewPath();
		using var bounds = new LiveHistoryBounds(clock, path);
		bounds.Paired();
		bounds.HoldStart();
		var window = bounds.WindowAsync(ct);
		var deadline = Assert.Single(clock.Timers, t => t.DueAt == LiveHistoryBounds.Bound);

		clock.Set(LiveHistoryBounds.Bound - s_tick);
		deadline.Fire();

		Assert.False(bounds.Deadline.IsCancellationRequested);
		Assert.False(window.IsCompleted);
		Assert.Equal(LiveHistoryBounds.Bound, deadline.DueAt);
		Assert.Equal(["paired", "hold_start"], Rows(path).Select(r => r.Kind), StringComparer.Ordinal);

		clock.Set(LiveHistoryBounds.Bound);
		deadline.Fire();

		Assert.True(bounds.Deadline.IsCancellationRequested);
		Assert.Equal("censored_bound", await window);
		Assert.Contains(Finish(LiveHistoryBounds.Bound, "censored_bound", LiveHistoryBounds.Bound), Rows(path));
	}

	[Fact(Timeout = Timeout)]
	public async Task Bound_AfterAQuietClose_StillCancelsTheDeadline()
	{
		var ct = TestContext.Current.CancellationToken;
		var clock = new ManualClock();
		var path = NewPath();
		using var bounds = new LiveHistoryBounds(clock, path);
		bounds.Paired();
		var deadline = Assert.Single(clock.Timers, t => t.DueAt == LiveHistoryBounds.Bound);
		var quiet = Assert.Single(clock.Timers, t => t.DueAt == LiveHistoryBounds.Quiet);

		clock.Set(LiveHistoryBounds.Quiet);
		quiet.Fire();
		Assert.Equal("quiet_no_history", await bounds.WindowAsync(ct));
		Assert.False(bounds.Deadline.IsCancellationRequested);

		clock.Set(LiveHistoryBounds.Bound);
		deadline.Fire();

		Assert.True(bounds.Deadline.IsCancellationRequested);
		Assert.Equal(["paired", "finish"], Rows(path).Select(r => r.Kind), StringComparer.Ordinal);
	}

	[Fact(Timeout = Timeout)]
	public async Task Quiet_ClosesAtExactlyTenMinutes_AndHistoryAtThatInstantIsOutside()
	{
		var ct = TestContext.Current.CancellationToken;
		const string Session = "bounds-quiet";
		var time = new FakeTimeProvider(s_t0);
		var path = NewPath();
		using var bounds = new LiveHistoryBounds(time, path);
		using var progress = await PairedAsync(Session, bounds, ct);

		time.Advance(TimeSpan.FromMinutes(2));
		await DeliverAsync(Session, progress, Blob(Session, "Q1", 3, -1), ct);
		var window = bounds.WindowAsync(ct);
		time.Advance(LiveHistoryBounds.Quiet - s_tick);
		Assert.False(window.IsCompleted);
		time.Advance(s_tick);
		Assert.Equal("quiet_window", await window);
		await DeliverAsync(Session, progress, Blob(Session, "Q2", 3, -2), ct);
		bounds.Summarize();

		var quiet = TimeSpan.FromMinutes(2) + LiveHistoryBounds.Quiet;
		Assert.Equal(
			[
				At("paired", TimeSpan.Zero),
				Batch(TimeSpan.FromMinutes(2), "Recent", 1, -1, -1),
				Progress(TimeSpan.FromMinutes(2), "Recent", 1),
				Finish(quiet, "quiet_window", quiet),
				Batch(quiet, "Recent", 1, -2, -2) with { AfterFinish = true },
				Progress(quiet, "Recent", 1) with { AfterFinish = true },
				Summary(quiet, "Recent", 1, 1, -1, -1, TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(2)),
				Captured(quiet, batches: 1, entries: 1, invalidations: 0, excluded: 2),
			],
			Rows(path)
		);
	}

	[Fact(Timeout = Timeout)]
	public async Task Quiet_DueAtTheBound_LosesToTheBound()
	{
		var ct = TestContext.Current.CancellationToken;
		const string Session = "bounds-tie";
		var time = new FakeTimeProvider(s_t0);
		var path = NewPath();
		using var bounds = new LiveHistoryBounds(time, path);
		using var progress = await PairedAsync(Session, bounds, ct);

		// History every 7 and 6 minutes keeps the window open until the last one, 10 minutes before the bound.
		time.Advance(TimeSpan.FromMinutes(7));
		await DeliverAsync(Session, progress, Blob(Session, "T1", 3, -1), ct);
		time.Advance(TimeSpan.FromMinutes(7));
		await DeliverAsync(Session, progress, Blob(Session, "T2", 3, -2), ct);
		time.Advance(TimeSpan.FromMinutes(6));
		await DeliverAsync(Session, progress, Blob(Session, "T3", 3, -3), ct);
		time.Advance(LiveHistoryBounds.Quiet);
		Assert.Equal("censored_bound", await bounds.WindowAsync(ct));
		bounds.Summarize();

		var last = LiveHistoryBounds.Bound - LiveHistoryBounds.Quiet;
		var bound = LiveHistoryBounds.Bound;
		Assert.Equal(
			[
				At("paired", TimeSpan.Zero),
				Batch(TimeSpan.FromMinutes(7), "Recent", 1, -1, -1),
				Progress(TimeSpan.FromMinutes(7), "Recent", 1),
				Batch(TimeSpan.FromMinutes(14), "Recent", 1, -2, -2),
				Progress(TimeSpan.FromMinutes(14), "Recent", 1),
				Batch(last, "Recent", 1, -3, -3),
				Progress(last, "Recent", 1),
				Finish(bound, "censored_bound", bound),
				Summary(bound, "Recent", 3, 3, -3, -1, TimeSpan.FromMinutes(7), last),
				Captured(bound, batches: 3, entries: 3, invalidations: 0, excluded: 0),
			],
			Rows(path)
		);
	}

	[Fact(Timeout = Timeout)]
	public async Task Window_AwaitedLateWithTimersBehind_ClosesAtTheQuietsTime_NotTheObservation_AndIgnoresWallClockJumps()
	{
		var ct = TestContext.Current.CancellationToken;
		const string Session = "bounds-late";
		// Its timers never fire, as a thread-pool timer can fire late: only the next event and the window see the close.
		var clock = new StalledClock();
		var path = NewPath();
		using var bounds = new LiveHistoryBounds(clock, path);
		using var progress = await PairedAsync(Session, bounds, ct);

		clock.Set(TimeSpan.FromMinutes(2), s_t0.AddMinutes(2));
		await DeliverAsync(Session, progress, Blob(Session, "W1", 3, -1), ct);
		// Five wall hours later, nine monotonic minutes: still in the window, so the quiet starts again.
		clock.Set(TimeSpan.FromMinutes(9), s_t0.AddHours(5));
		await DeliverAsync(Session, progress, Blob(Session, "W2", 3, -2), ct);
		// The quiet was due at 19 minutes; the next history, at 25 and three wall hours back, finds the window closed.
		clock.Set(TimeSpan.FromMinutes(25), s_t0.AddHours(2));
		await DeliverAsync(Session, progress, Blob(Session, "W3", 2, -3), ct);
		clock.Set(TimeSpan.FromMinutes(40), s_t0.AddHours(3));
		Assert.Equal("quiet_window", await bounds.WindowAsync(ct));
		bounds.Summarize();

		var at9 = TimeSpan.FromMinutes(9);
		var at25 = TimeSpan.FromMinutes(25);
		var at40 = TimeSpan.FromMinutes(40);
		Assert.Equal(
			[
				At("paired", TimeSpan.Zero),
				Batch(TimeSpan.FromMinutes(2), "Recent", 1, -1, -1),
				Progress(TimeSpan.FromMinutes(2), "Recent", 1),
				Batch(at9, "Recent", 1, -2, -2) with { Utc = s_t0.AddHours(5) },
				Progress(at9, "Recent", 1) with { Utc = s_t0.AddHours(5) },
				Finish(at9 + LiveHistoryBounds.Quiet, "quiet_window", at25) with { Utc = s_t0.AddHours(2) },
				Batch(at25, "Full", 1, -3, -3) with { Utc = s_t0.AddHours(2), AfterFinish = true },
				Progress(at25, "Full", 1) with { Utc = s_t0.AddHours(2), AfterFinish = true },
				Summary(at40, "Recent", 2, 2, -2, -1, TimeSpan.FromMinutes(2), TimeSpan.FromHours(5)) with { Utc = s_t0.AddHours(3) },
				Captured(at40, batches: 2, entries: 2, invalidations: 0, excluded: 2) with { Utc = s_t0.AddHours(3) },
			],
			Rows(path)
		);
	}

	[Fact(Timeout = Timeout)]
	public async Task Window_Cancelled_ClosesCancelledOnce_UnlessARuleClosedItFirst()
	{
		var ct = TestContext.Current.CancellationToken;
		var time = new FakeTimeProvider(s_t0);
		var path = NewPath();
		using (var bounds = new LiveHistoryBounds(time, path))
		{
			bounds.Paired();
			time.Advance(TimeSpan.FromMinutes(3));
			using var cancel = CancellationTokenSource.CreateLinkedTokenSource(ct);
			var window = bounds.WindowAsync(cancel.Token);
			await cancel.CancelAsync();
			Assert.Equal("cancelled", await window);
			time.Advance(LiveHistoryBounds.Bound);
			Assert.Equal("cancelled", await bounds.WindowAsync(ct));
			bounds.LoggedOutExternally();
			bounds.Summarize();
		}

		var at3 = TimeSpan.FromMinutes(3);
		var end = at3 + LiveHistoryBounds.Bound;
		Assert.Equal(
			[
				At("paired", TimeSpan.Zero),
				Finish(at3, "cancelled", at3),
				At("logged_out_externally", end) with { Ok = false },
				Captured(end, batches: 0, entries: 0, invalidations: 0, excluded: 0),
			],
			Rows(path)
		);

		// The quiet came due before the cancelled call saw it, its timer behind: the rule's close stands.
		var clock = new StalledClock();
		var late = NewPath();
		using (var bounds = new LiveHistoryBounds(clock, late))
		{
			bounds.Paired();
			clock.Set(TimeSpan.FromMinutes(11), s_t0.AddMinutes(11));
			using var cancelled = new CancellationTokenSource();
			await cancelled.CancelAsync();
			Assert.Equal("quiet_no_history", await bounds.WindowAsync(cancelled.Token));
			bounds.Summarize();
		}

		var at11 = TimeSpan.FromMinutes(11);
		Assert.Equal(
			[
				At("paired", TimeSpan.Zero),
				Finish(LiveHistoryBounds.Quiet, "quiet_no_history", at11),
				Captured(at11, batches: 0, entries: 0, invalidations: 0, excluded: 0),
			],
			Rows(late)
		);
	}

	[Fact(Timeout = Timeout)]
	public async Task Summarize_AnOpenWindow_ClosesItNotClosed_AndWithoutAPairingTheWindowIsNotPaired()
	{
		var ct = TestContext.Current.CancellationToken;
		var time = new FakeTimeProvider(s_t0);
		var open = NewPath();
		using (var bounds = new LiveHistoryBounds(time, open))
		{
			bounds.Paired();
			time.Advance(TimeSpan.FromMinutes(4));
			bounds.Summarize();
		}

		var at4 = TimeSpan.FromMinutes(4);
		Assert.Equal([At("paired", TimeSpan.Zero), Finish(at4, "not_closed", at4), Captured(at4, batches: 0, entries: 0, invalidations: 0, excluded: 0)], Rows(open));

		var later = new FakeTimeProvider(s_t0);
		var unpaired = NewPath();
		using (var bounds = new LiveHistoryBounds(later, unpaired))
		{
			Assert.Equal("not_paired", await bounds.WindowAsync(ct));
			later.Advance(LiveHistoryBounds.Bound);
			Assert.False(bounds.Deadline.IsCancellationRequested);
			bounds.Summarize();
		}

		Assert.Equal(
			[
				Finish(TimeSpan.Zero, "not_paired", TimeSpan.Zero),
				Captured(LiveHistoryBounds.Bound, batches: 0, entries: 0, invalidations: 0, excluded: 0),
			],
			Rows(unpaired)
		);
	}

	[Fact(Timeout = Timeout)]
	public async Task Logs_ASidecarLifecycleOrASkipOfTheSession_InvalidateTheRecord_NothingElseDoes()
	{
		var ct = TestContext.Current.CancellationToken;
		const string Session = "bounds-logs";
		var client = await fixture.Sidecar.Sessions.GetOrCreateAsync(SessionId.From(Session), ct);
		var failure = new InvalidOperationException("unreadable");
		var time = new FakeTimeProvider(s_t0);

		var unrelated = NewPath();
		using (var bounds = new LiveHistoryBounds(time, unrelated))
		{
			bounds.Register(client);
			bounds.Paired();
			// The same ids in the sidecar's own category, and the skips of another session.
			var sidecar = bounds.LoggerProvider.CreateLogger(SidecarLog.SidecarCategory);
			SidecarLog.SidecarExited(sidecar, 1, 2);
			SidecarLog.SidecarRestarted(sidecar, 1, TimeSpan.FromSeconds(1), 1);
			SidecarLog.SidecarRestartFailed(sidecar, failure, 1, TimeSpan.FromSeconds(1));
			HistoryLog.MessageUnreadable(sidecar, failure, Session);
			EventLog.EventUnreadable(sidecar, failure, 7, Session);
			var other = bounds.LoggerProvider.CreateLogger(SidecarLog.Category);
			HistoryLog.MessageUnreadable(other, failure, "bounds-logs-other");
			EventLog.EventUnreadable(other, failure, 7, "bounds-logs-other");
			bounds.Summarize();
		}

		Assert.Equal([At("paired", TimeSpan.Zero), Finish(TimeSpan.Zero, "not_closed", TimeSpan.Zero), Captured(TimeSpan.Zero, batches: 0, entries: 0, invalidations: 0, excluded: 0)], Rows(unrelated));

		var invalid = NewPath();
		using (var bounds = new LiveHistoryBounds(time, invalid))
		{
			bounds.Register(client);
			bounds.Paired();
			var log = bounds.LoggerProvider.CreateLogger(SidecarLog.Category);
			HistoryLog.MessageUnreadable(log, failure, Session);
			EventLog.EventUnreadable(log, failure, 7, Session);
			SidecarLog.SidecarExited(log, 1, 2);
			SidecarLog.SidecarRestarted(log, 1, TimeSpan.FromSeconds(1), 1);
			SidecarLog.SidecarRestartFailed(log, failure, 1, TimeSpan.FromSeconds(1));
			bounds.Summarize();
		}

		Assert.Equal(
			[
				At("paired", TimeSpan.Zero),
				At("history_message_skipped", TimeSpan.Zero) with { Ok = false },
				At("event_skipped", TimeSpan.Zero) with { Ok = false },
				At("sidecar_exit", TimeSpan.Zero) with { Ok = false },
				At("sidecar_restart", TimeSpan.Zero) with { Ok = false },
				At("sidecar_restart_failed", TimeSpan.Zero) with { Ok = false },
				Finish(TimeSpan.Zero, "not_closed", TimeSpan.Zero),
				Captured(TimeSpan.Zero, batches: 0, entries: 0, invalidations: 5, excluded: 0) with { Ok = false },
			],
			Rows(invalid)
		);
	}

	[Fact(Timeout = Timeout)]
	public async Task Batch_CountsItsMessagesByAgeAtThePairingsUtc_EachUpperBoundInclusive_TheFutureApart_AndNoneBeforeAPairing()
	{
		var ct = TestContext.Current.CancellationToken;
		const string Session = "bounds-ages";
		var clock = new StalledClock();
		var path = NewPath();
		using var bounds = new LiveHistoryBounds(clock, path);
		var client = await Histories.CaughtUpAsync(fixture, Session, ct);
		bounds.Register(client);
		using var progress = new Received<HistorySyncProgressed>(client);
		var day = TimeSpan.FromDays(1);
		var second = TimeSpan.FromSeconds(1);

		// Before the pairing there is no time to age a message from.
		var early = s_t0.AddDays(-2);
		await DeliverAsync(Session, progress, Blob(Session, "A0", 3, early), ct);
		var pairedAt = TimeSpan.FromMinutes(1);
		var paired = s_t0 + pairedAt;
		clock.Set(pairedAt, paired);
		bounds.Paired();
		// The batch is seen two wall days after the pairing, two monotonic minutes: its ages count from the pairing.
		var seenAt = TimeSpan.FromMinutes(3);
		var seen = paired + (2 * day);
		clock.Set(seenAt, seen);
		await DeliverAsync(
			Session,
			progress,
			Blob(
				Session,
				"A1",
				2,
				paired + second,
				paired,
				paired - day,
				paired - day - second,
				paired - (3 * day),
				paired - (3 * day) - second,
				paired - (7 * day),
				paired - (7 * day) - second
			),
			ct
		);

		Assert.Equal(
			[
				At("history_batch", TimeSpan.Zero) with { SyncType = "Recent", Entries = 1, MinMessage = early, MaxMessage = early },
				At("history_batch", seenAt) with
				{
					Utc = seen,
					SyncType = "Full",
					Entries = 8,
					MinMessage = paired - (7 * day) - second,
					MaxMessage = paired + second,
					AgeFuture = 1,
					AgeUpTo1Day = 2,
					AgeOver1To3Days = 2,
					AgeOver3To7Days = 2,
					AgeOver7Days = 1,
				},
			],
			Rows(path).Where(r => r.Kind == "history_batch")
		);
	}

	[Theory]
	[InlineData("1", 1)]
	[InlineData("7", 7)]
	[InlineData("07", 7)]
	[InlineData("2147483647", int.MaxValue)]
	public void DaysLimit_DigitsAboveZero_AreTheLimit(string value, int expected) =>
		Assert.Equal(expected, LiveHistoryBounds.ParseDaysLimit("WHATSMEOW_TEST_DAYS", value));

	[Theory]
	[InlineData(null)]
	[InlineData("")]
	[InlineData(" ")]
	public void DaysLimit_MissingOrBlank_FailsNamingTheVariable(string? value) =>
		Assert.Contains("WHATSMEOW_TEST_DAYS", Assert.Throws<InvalidOperationException>(() => LiveHistoryBounds.ParseDaysLimit("WHATSMEOW_TEST_DAYS", value)).Message, StringComparison.Ordinal);

	[Theory]
	[InlineData("0")]
	[InlineData("-1")]
	[InlineData("+7")]
	[InlineData(" 7")]
	[InlineData("7 ")]
	[InlineData("7.0")]
	[InlineData("1,000")]
	[InlineData("1e1")]
	[InlineData("0x7")]
	[InlineData("2147483648")]
	[InlineData("seven")]
	public void DaysLimit_NotDigitsAboveZero_FailsNamingTheVariable_WithoutTheValue(string value)
	{
		var failure = Assert.Throws<InvalidOperationException>(() => LiveHistoryBounds.ParseDaysLimit("WHATSMEOW_TEST_DAYS", value));

		Assert.Contains("WHATSMEOW_TEST_DAYS", failure.Message, StringComparison.Ordinal);
		Assert.DoesNotContain(value, failure.Message, StringComparison.Ordinal);
	}

	[Fact]
	public void Receipt_WhosePathExists_IsNotWritten_AndTheFileThereIsKept()
	{
		var path = NewPath();
		File.WriteAllText(path + ".receipt.json", "kept");
		using (var bounds = new LiveHistoryBounds(new FakeTimeProvider(s_t0), path))
		{
			bounds.Paired();
			bounds.Summarize();
		}

		Assert.Equal("kept", File.ReadAllText(path + ".receipt.json"));
	}

	[Fact]
	public void Record_OutlivesItsLoggerFactory_ButADisposalBeforeTheSummaryIsInIt_AndLeavesNoReceipt()
	{
		var time = new FakeTimeProvider(s_t0);
		var owned = NewPath();
		using (var bounds = new LiveHistoryBounds(time, owned))
		{
			// The factory disposes the providers added to it with itself, as LiveFresh's does.
			using (var factory = new LoggerFactory())
			{
				factory.AddProvider(bounds.LoggerProvider);
				bounds.Paired();
			}

			bounds.Summarize();
		}

		Assert.Equal(new LiveHistoryBounds.Receipt { Sha256 = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(owned))), Lines = Lines(owned).Length }, ReceiptOf(owned));

		var early = NewPath();
		var disposed = new LiveHistoryBounds(time, early);
		disposed.Paired();
		disposed.Dispose();
		disposed.Summarize();

		Assert.Equal([At("paired", TimeSpan.Zero), At("disposed_before_summary", TimeSpan.Zero) with { Ok = false }], Rows(early));
		Assert.False(File.Exists(early + ".receipt.json"));
	}

	[Fact]
	public void Create_AnExistingFile_IsRefused_AndLeftAsItWas()
	{
		var existing = NewPath();
		File.WriteAllText(existing, "store");

		_ = Assert.Throws<IOException>(() => new LiveHistoryBounds(new FakeTimeProvider(s_t0), existing));

		Assert.Equal("store", File.ReadAllText(existing));
	}

	[Fact]
	public void Create_ThroughALinkOrInADirectoryOthersCanRead_IsRefused()
	{
		if (OperatingSystem.IsWindows())
		{
			Assert.Skip("links need a privilege, and Unix modes do not apply, on Windows");
			return;
		}

		var directory = binaries.NewDirectory();
		var target = Path.Combine(directory, "target");
		File.WriteAllText(target, "store");
		var link = Path.Combine(directory, "link.jsonl");
		_ = File.CreateSymbolicLink(link, target);
		var dangling = Path.Combine(directory, "dangling.jsonl");
		var nowhere = Path.Combine(directory, "nowhere");
		_ = File.CreateSymbolicLink(dangling, nowhere);
		var open = Directory.CreateDirectory(Path.Combine(directory, "open")).FullName;
		File.SetUnixFileMode(open, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
		var linked = Path.Combine(directory, "linked");
		_ = Directory.CreateSymbolicLink(linked, binaries.NewDirectory());

		_ = Assert.Throws<IOException>(() => new LiveHistoryBounds(new FakeTimeProvider(s_t0), link));
		_ = Assert.Throws<IOException>(() => new LiveHistoryBounds(new FakeTimeProvider(s_t0), dangling));
		_ = Assert.Throws<InvalidOperationException>(() => new LiveHistoryBounds(new FakeTimeProvider(s_t0), Path.Combine(open, "bounds.jsonl")));
		_ = Assert.Throws<InvalidOperationException>(() => new LiveHistoryBounds(new FakeTimeProvider(s_t0), Path.Combine(linked, "bounds.jsonl")));

		Assert.Equal("store", File.ReadAllText(target));
		Assert.False(File.Exists(nowhere));
		Assert.Empty(Directory.GetFileSystemEntries(open));
		Assert.Empty(Directory.GetFileSystemEntries(linked));
	}

	// A session of the test's own, linked and caught up, the record registered on it before the progress reader, and
	// paired at the clock's start.
	private async Task<Received<HistorySyncProgressed>> PairedAsync(string session, LiveHistoryBounds bounds, CancellationToken cancellationToken)
	{
		var client = await Histories.CaughtUpAsync(fixture, session, cancellationToken).ConfigureAwait(false);
		bounds.Register(client);
		var progress = new Received<HistorySyncProgressed>(client);
		bounds.Paired();
		return progress;
	}

	// The blob, then its progress event, which comes after its batches on the history lane and after the record's
	// handler: once it is read, the record saw both.
	private async Task DeliverAsync(string session, Received<HistorySyncProgressed> progress, DeliverHistorySyncRequest blob, CancellationToken cancellationToken)
	{
		Assert.Equal(session, blob.SessionId);
		_ = await fixture.Fake.DeliverHistorySyncAsync(blob, cancellationToken: cancellationToken).ConfigureAwait(false);
		_ = await progress.NextAsync(cancellationToken).ConfigureAwait(false);
	}

	private string NewPath() => Path.Combine(binaries.NewDirectory(), "bounds.jsonl");

	// One conversation whose messages are the given days from s_day.
	private static DeliverHistorySyncRequest Blob(string session, string id, int syncType, params int[] days) =>
		Blob(session, id, syncType, [.. days.Select(day => s_day.AddDays(day))]);

	// One conversation whose messages are at the given times, whole seconds: WhatsApp's message times have no fraction.
	private static DeliverHistorySyncRequest Blob(string session, string id, int syncType, params DateTimeOffset[] times)
	{
		var conversation = new Conversation { Chat = Chat };
		conversation.Messages.Add(times.Select((time, i) => new WebMessage { Id = $"{id}-{i}", Text = "history text", MessageTime = Timestamp.FromDateTimeOffset(time) }));
		var blob = new DeliverHistorySyncRequest { SessionId = session, MessageId = id, SyncType = syncType, ChunkOrder = 2, Progress = 40 };
		blob.Conversations.Add(conversation);
		return blob;
	}

	// Sharing write: the writer may still hold the file, with FileShare.Read, which Windows holds against a reader that
	// does not share write (File.ReadAllLines).
	private static string[] Lines(string path)
	{
		using var reader = new StreamReader(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite));
		var lines = new List<string>();
		while (reader.ReadLine() is { } line)
		{
			if (line.Length > 0)
				lines.Add(line);
		}

		return [.. lines];
	}

	// Every line but the options, which has a shape of its own.
	private static List<Row> Rows(string path) =>
		[.. Lines(path).Where(l => !l.Contains("\"Kind\":\"run_options\"", StringComparison.Ordinal)).Select(l => JsonSerializer.Deserialize(l, LiveHistoryBounds.HistoryJson.Default.Row)!)];

	private static LiveHistoryBounds.Receipt? ReceiptOf(string path) => JsonSerializer.Deserialize(File.ReadAllText(path + ".receipt.json"), LiveHistoryBounds.HistoryJson.Default.Receipt);

	private static Row At(string kind, TimeSpan at) => new() { Kind = kind, Utc = s_t0 + at, MonoNs = at.Ticks * 100 };

	// The tests that expect rows from this helper send messages at least 37 days before the pairing (s_day itself at the
	// newest, paired at s_t0), so every one counts in the oldest age.
	private static Row Batch(TimeSpan at, string type, long entries, int minDay, int maxDay) =>
		At("history_batch", at) with
		{
			SyncType = type,
			Entries = entries,
			MinMessage = s_day.AddDays(minDay),
			MaxMessage = s_day.AddDays(maxDay),
			AgeFuture = 0,
			AgeUpTo1Day = 0,
			AgeOver1To3Days = 0,
			AgeOver3To7Days = 0,
			AgeOver7Days = entries,
		};

	private static Row Progress(TimeSpan at, string type, long messages) =>
		At("history_progress", at) with { SyncType = type, ChunkOrder = 2, Progress = 40, MessageCount = messages, Skipped = 0 };

	private static Row Finish(TimeSpan close, string outcome, TimeSpan seen) => At("finish", seen) with { Detail = outcome, CloseNs = close.Ticks * 100 };

	private static Row Summary(TimeSpan at, string type, long batches, long entries, int minDay, int maxDay, TimeSpan first, TimeSpan last) =>
		At("history_type_summary", at) with
		{
			SyncType = type,
			Batches = batches,
			Entries = entries,
			MinMessage = s_day.AddDays(minDay),
			MaxMessage = s_day.AddDays(maxDay),
			FirstObserved = s_t0 + first,
			LastObserved = s_t0 + last,
		};

	private static Row Captured(TimeSpan at, long batches, long entries, int invalidations, int excluded) =>
		At("capture_summary", at) with { Batches = batches, Entries = entries, Detail = $"write_failures=0 invalidations={invalidations} excluded={excluded}" };

	/// <summary>
	/// A clock the test sets, whose timers fire only when the test fires them, at whatever time it set: early, as a
	/// system timer may. Each timer knows the monotonic time it is due at.
	/// </summary>
	private sealed class ManualClock : TimeProvider
	{
		private readonly Lock _gate = new();
		private readonly List<Manual> _timers = [];
		private TimeSpan _mono;

		public override long TimestampFrequency => TimeSpan.TicksPerSecond;

		public IReadOnlyList<Manual> Timers
		{
			get
			{
				lock (_gate)
					return [.. _timers];
			}
		}

		public override long GetTimestamp()
		{
			lock (_gate)
				return _mono.Ticks;
		}

		public override DateTimeOffset GetUtcNow()
		{
			lock (_gate)
				return s_t0 + _mono;
		}

		public void Set(TimeSpan mono)
		{
			lock (_gate)
				_mono = mono;
		}

		public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
		{
			var timer = new Manual(this, callback, state);
			_ = timer.Change(dueTime, period);
			lock (_gate)
				_timers.Add(timer);
			return timer;
		}

		public sealed class Manual(ManualClock clock, TimerCallback callback, object? state) : ITimer
		{
			/// <summary>When it is due, on the clock's monotonic time; null while it is off.</summary>
			public TimeSpan? DueAt { get; private set; }

			/// <summary>Runs the callback now, whatever the clock says: a one-shot timer that fired is off until changed again.</summary>
			public void Fire()
			{
				DueAt = null;
				callback(state);
			}

			public bool Change(TimeSpan dueTime, TimeSpan period)
			{
				DueAt = dueTime == global::System.Threading.Timeout.InfiniteTimeSpan ? null : TimeSpan.FromTicks(clock.GetTimestamp()) + dueTime;
				return true;
			}

			public void Dispose() => DueAt = null;

			public ValueTask DisposeAsync()
			{
				Dispose();
				return ValueTask.CompletedTask;
			}
		}
	}

	/// <summary>A clock whose monotonic and wall times the test sets apart, and whose timers never fire.</summary>
	private sealed class StalledClock : TimeProvider
	{
		private readonly Lock _gate = new();
		private TimeSpan _mono;
		private DateTimeOffset _wall = s_t0;

		public override long TimestampFrequency => TimeSpan.TicksPerSecond;

		public override long GetTimestamp()
		{
			lock (_gate)
				return _mono.Ticks;
		}

		public override DateTimeOffset GetUtcNow()
		{
			lock (_gate)
				return _wall;
		}

		public void Set(TimeSpan mono, DateTimeOffset wall)
		{
			lock (_gate)
				(_mono, _wall) = (mono, wall);
		}

		public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) => new Stalled();

		private sealed class Stalled : ITimer
		{
			public bool Change(TimeSpan dueTime, TimeSpan period) => true;

			public void Dispose()
			{
			}

			public ValueTask DisposeAsync() => ValueTask.CompletedTask;
		}
	}
}
