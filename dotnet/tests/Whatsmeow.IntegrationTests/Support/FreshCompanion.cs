using System.Runtime.ExceptionServices;
using Whatsmeow.History;
using Whatsmeow.IntegrationTests.Sessions;
using Whatsmeow.Sessions;

namespace Whatsmeow.IntegrationTests.Support;

/// <summary>
/// The one session of a database a run created for it, paired once (ADR 0060): the live tests' fresh companion of the
/// peer's account, and its fake-mode tests. It logs out and deletes the session only once <see cref="RunAsync"/> proved
/// the account, a new device and the run's own database, and only after a logout this cleanup made succeeded; in any
/// other case the session and its keys stay. The database file always stays: it is the caller's, deleted by hand.
/// </summary>
internal sealed class FreshCompanion : IDisposable
{
	/// <summary>The cleanup's own bound: a connect, a wait for a usable connection, the logout, the delete and their reads.</summary>
	public static readonly TimeSpan CleanupBound = TimeSpan.FromMinutes(5);

	/// <summary>What the sidecar and SQLite keep beside a database.</summary>
	private static readonly string[] s_beside = ["-wal", "-shm", ".lock", ".tmp"];

	private readonly WhatsmeowSidecar _sidecar;
	private readonly TimeProvider _time;
	private readonly Action<string> _log;
	private readonly TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);
	private readonly Lock _lock = new();
	private readonly Dictionary<HistorySyncType, int> _historySeen = [];
	private readonly List<IDisposable> _handlers = [];
	private Action? _holdEnd;
	private bool _holding;
	private bool _paired;
	private bool _proven;
	private bool _rejected;

	private FreshCompanion(WhatsmeowSidecar sidecar, IWhatsAppClient client, TimeProvider time, Action<string> log)
	{
		_sidecar = sidecar;
		Client = client;
		_time = time;
		_log = log;
	}

	/// <summary>The session's client.</summary>
	public IWhatsAppClient Client { get; }

	/// <summary>
	/// Creates <paramref name="path"/> empty and owner-only in one exclusive create, so it is no existing file, store or
	/// link; its directory must be the owner's alone and no link, and nothing the sidecar keeps beside a database may
	/// exist yet.
	/// </summary>
	public static void CreateExclusive(string path)
	{
		var directory = new DirectoryInfo(Path.GetDirectoryName(Path.GetFullPath(path))!);
		if (!directory.Exists || directory.LinkTarget is not null)
			throw new InvalidOperationException("The fresh database's directory must exist and not be a link.");
		if (!OperatingSystem.IsWindows() && (directory.UnixFileMode & ~(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute)) != UnixFileMode.None)
			throw new InvalidOperationException("The fresh database's directory must be the owner's alone (0700).");
		if (s_beside.Any(suffix => Path.Exists(path + suffix)))
			throw new InvalidOperationException("The fresh database has a store's files beside it already.");

		var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
		if (!OperatingSystem.IsWindows())
			options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
		using var created = new FileStream(path, options);
	}

	/// <summary>
	/// The session <paramref name="id"/> on <paramref name="sidecar"/>, which must have none yet: a database created by
	/// <see cref="CreateExclusive"/> for this run.
	/// </summary>
	public static async Task<FreshCompanion> OpenAsync(WhatsmeowSidecar sidecar, SessionId id, TimeProvider time, Action<string> log, CancellationToken cancellationToken)
	{
		var before = await sidecar.Sessions.ListAsync(cancellationToken).ConfigureAwait(false);
		Assert.True(before.IsEmpty, "the fresh database has a session already: it is not this run's own");
		var client = await sidecar.Sessions.GetOrCreateAsync(id, cancellationToken).ConfigureAwait(false);
		return new FreshCompanion(sidecar, client, time, log);
	}

	/// <summary>
	/// Holds the session's history from its first history event until the first sight steps of <see cref="RunAsync"/>
	/// ended; <paramref name="holdStart"/> and <paramref name="holdEnd"/> are told, once each. A handler registered before
	/// this one sees each history event first. The hold stops recording once the sidecar's window of 4 unacked history
	/// events is full; it does not stop a download (ADR 0035, ADR 0060).
	/// </summary>
	public void HoldHistory(Action holdStart, Action holdEnd)
	{
		_holdEnd = holdEnd;
		_handlers.Add(Client.On<HistoryReceived>((e, ct) => HoldAsync(e.SyncType, holdStart, ct)));
		_handlers.Add(Client.On<HistorySyncProgressed>((e, ct) => HoldAsync(e.SyncType, holdStart, ct)));
	}

	/// <summary>Tells the companion its pairing yielded Paired, so a failure before the proof says the device may be linked.</summary>
	public void Paired() => _paired = true;

	/// <summary>
	/// Pairs, proves the session (<see cref="ProveAsync"/>), runs <see cref="FreshRun.FirstSight"/> with the history
	/// held, releases it, runs <see cref="FreshRun.Then"/> and the window, then logs out (<see cref="LogoutAsync"/>).
	/// A step that fails is kept and the next one runs; a step after <see cref="FreshRun.Deadline"/> does not run. The
	/// hold is released and the logout runs whatever the steps and the body's token did. A failed proof stops at once:
	/// nothing is logged out. Every failure is thrown at the end.
	/// </summary>
	public async Task RunAsync(FreshRun run, CancellationToken cancellationToken)
	{
		var failures = new List<ExceptionDispatchInfo>();
		try
		{
			await run.PairAsync(this, cancellationToken).ConfigureAwait(false);
			if (await ProveAsync(run, cancellationToken).ConfigureAwait(false) is { } rejected)
			{
				run.Record(FreshEvent.IdentityRejected);
				_rejected = true;
				_log($"live: fresh companion rejected ({rejected}): nothing is logged out, the device may still be linked, the database is kept");
				Assert.Fail($"the fresh companion is not the run's own new device of the expected account: {rejected}");
			}

			run.Record(FreshEvent.IdentityVerified);
			using var steps = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, run.Deadline);
			foreach (var step in run.FirstSight)
				await StepAsync(failures, step, describeHistory: true, steps.Token).ConfigureAwait(false);
			ReleaseHold();
			foreach (var step in run.Then)
				await StepAsync(failures, step, describeHistory: false, steps.Token).ConfigureAwait(false);

			_log($"live: history window ended {await run.WindowAsync(cancellationToken).ConfigureAwait(false)}");
		}
		finally
		{
			ReleaseHold();
			if (_paired && !_proven && !_rejected)
				_log("live: fresh companion paired but not proven: nothing is logged out, the device may still be linked, the database is kept");
			if (_proven)
			{
				var (logout, detail) = await LogoutAsync().ConfigureAwait(false);
				_log($"live: fresh companion cleanup: {logout} {detail}");
				var (recorded, failure) = logout switch
				{
					FreshLogout.Verified => ((FreshEvent?)FreshEvent.LogoutVerified, (Exception?)null),
					FreshLogout.LoggedOutExternally => (FreshEvent.LoggedOutExternally, new InvalidOperationException($"the fresh companion was logged out before its cleanup: {detail}")),
					FreshLogout.Kept or _ => (null, new InvalidOperationException($"the fresh companion is kept, it may still be linked: {detail}")),
				};
				if (recorded is { } kind)
					run.Record(kind);
				if (failure is not null)
					failures.Add(ExceptionDispatchInfo.Capture(failure));
			}
		}

		if (failures.Count == 1)
			failures[0].Throw();
		if (failures.Count > 1)
			throw new AggregateException(failures.Select(f => f.SourceException));
	}

	/// <summary>
	/// Null when the paired session is <paramref name="run"/>'s expected account, not the forbidden one, on a device no
	/// other known store holds, and alone on this database; else which failed, with no identifier.
	/// </summary>
	public async Task<string?> ProveAsync(FreshRun run, CancellationToken cancellationToken)
	{
		if (!ValidKnownDevices(run.Expected, run.KnownDevices))
			return "known_devices_invalid";

		var mine = await _sidecar.Sessions.ListAsync(cancellationToken).ConfigureAwait(false);
		if (mine is not [{ Jid: { } device } info] || info.Id != Client.SessionId)
			return "not_the_only_paired_session";
		if (!Names(run.Expected, device) || Names(run.Forbidden, device))
			return "not_the_expected_account";
		if (run.OtherDevices.Concat(run.KnownDevices).Any(o => o == device || (info.Lid is { } lid && o == lid)))
			return "not_a_new_device";

		_proven = true;
		return null;
	}

	/// <summary>
	/// Logs a proven session out on <see cref="CleanupBound"/>, whatever the body's token did: from Disconnected it
	/// connects, from Connecting or Reconnecting it waits for Connected or CaughtUp, then it logs out, and deletes the
	/// session only once it reads LoggedOut with code 0 after that logout. A session logged out already, any code, was
	/// logged out by WhatsApp or the phone, not here: it is reported and kept, as is one that turns LoggedOut while the cleanup waits. Any other state, error or the bound keeps
	/// the session and its keys.
	/// </summary>
	public async Task<(FreshLogout Logout, string Detail)> LogoutAsync()
	{
		if (!_proven)
			throw new InvalidOperationException("The fresh session is logged out only once it is proven the run's own (ADR 0060).");

		using var bound = new CancellationTokenSource(CleanupBound, _time);
		try
		{
			var state = await SessionSetup.StateAsync(Client, bound.Token).ConfigureAwait(false);
			if (state is SessionState.LoggedOut before)
				return (FreshLogout.LoggedOutExternally, $"LoggedOut {before.Code} before the cleanup's logout");
			if (state is SessionState.Disconnected)
			{
				_log("live: fresh companion cleanup connects");
				await Client.ConnectAsync(bound.Token).ConfigureAwait(false);
			}

			if (state is not (SessionState.Connected or SessionState.CaughtUp))
				state = await UsableAsync(bound.Token).ConfigureAwait(false);
			if (state is SessionState.LoggedOut during)
				return (FreshLogout.LoggedOutExternally, $"LoggedOut {during.Code} while the cleanup waited for a usable connection");
			if (state is not (SessionState.Connected or SessionState.CaughtUp))
				return (FreshLogout.Kept, $"{state.GetType().Name}, not a connection to log out on");

			await Client.LogoutAsync(bound.Token).ConfigureAwait(false);
			var after = await SessionSetup.StateAsync(Client, bound.Token).ConfigureAwait(false);
			if (after != new SessionState.LoggedOut { Code = 0 })
				return (FreshLogout.Kept, $"{after.GetType().Name} after the logout, not LoggedOut 0");

			await _sidecar.Sessions.DeleteAsync(Client.SessionId, bound.Token).ConfigureAwait(false);
			var sessions = await _sidecar.Sessions.ListAsync(bound.Token).ConfigureAwait(false);
			return sessions.Any(s => s.Id == Client.SessionId)
				? (FreshLogout.Kept, "still listed after its delete")
				: (FreshLogout.Verified, "logged out, LoggedOut 0, deleted");
		}
#pragma warning disable CA1031 // Justification: whatever failed, keeping the session and its keys is the safe end; the kind is reported.
		catch (Exception e)
#pragma warning restore CA1031
		{
			return (
				FreshLogout.Kept,
				e switch
				{
					WhatsmeowException w => $"{e.GetType().Name} {w.ErrorCode}",
					OperationCanceledException => $"not done within {CleanupBound.TotalMinutes:F0} min",
					_ => e.GetType().Name,
				}
			);
		}
	}

	public void Dispose()
	{
		ReleaseHold();
		foreach (var handler in _handlers)
			handler.Dispose();
	}

	/// <summary>Whether <paramref name="jid"/> names <paramref name="account"/>'s user, by either form, on any device.</summary>
	private static bool Names((Jid Chat, Jid? Lid) account, Jid jid) => (jid with { Device = 0 }) == account.Chat || (jid with { Device = 0 }) == account.Lid;

	/// <summary>Known devices are given, distinct, each a device (not device 0) of the expected account.</summary>
	private static bool ValidKnownDevices((Jid Chat, Jid? Lid) expected, IReadOnlyCollection<Jid> known) =>
		known.Count > 0 && known.ToHashSet().Count == known.Count && known.All(d => d.Device != 0 && Names(expected, d));

	/// <summary>The first of Connected, CaughtUp or a state that ends the wait (LoggedOut, Replaced, an error, a ban).</summary>
	private async Task<SessionState> UsableAsync(CancellationToken cancellationToken)
	{
		_log("live: fresh companion cleanup waits for a usable connection");
		var watch = StateWatch.Start(Client, cancellationToken);
		await using (watch.ConfigureAwait(false))
		{
			var state = await watch.NextAsync().ConfigureAwait(false);
			while (state is SessionState.Connecting or SessionState.Reconnecting or SessionState.Disconnected)
				state = await watch.NextAsync().ConfigureAwait(false);
			return state;
		}
	}

	private async Task StepAsync(List<ExceptionDispatchInfo> failures, FreshStep step, bool describeHistory, CancellationToken cancellationToken)
	{
		if (cancellationToken.IsCancellationRequested)
		{
			_log($"live: {step.Name}: not run, the capture bound or the test's token ended first");
			failures.Add(ExceptionDispatchInfo.Capture(new OperationCanceledException($"{step.Name} was not run: the capture bound or the test's token ended first")));
			return;
		}

		if (describeHistory)
			_log($"live: {step.Name} starts with the session {await DescribeStateAsync(cancellationToken).ConfigureAwait(false)} and history seen: {DescribeHistory()}");

		try
		{
			await step.Run(cancellationToken).ConfigureAwait(false);
			_log($"live: {step.Name}: passed");
		}
#pragma warning disable CA1031 // Justification: thrown at the end of the run, after the other steps, the window and the cleanup ran.
		catch (Exception e)
#pragma warning restore CA1031
		{
			_log($"live: {step.Name}: failed, {e.GetType().Name}");
			failures.Add(ExceptionDispatchInfo.Capture(e));
		}
	}

	/// <summary>The session's state for the log; a failed read is said and does not stop the step, the window or the cleanup.</summary>
	private async Task<string> DescribeStateAsync(CancellationToken cancellationToken)
	{
		try
		{
			return (await SessionSetup.StateAsync(Client, cancellationToken).ConfigureAwait(false)).GetType().Name;
		}
#pragma warning disable CA1031 // Justification: a diagnostic read; the step runs whatever it threw, and reports its own failure.
		catch (Exception e)
#pragma warning restore CA1031
		{
			return $"state unknown (the read failed: {e.GetType().Name})";
		}
	}

	private string DescribeHistory()
	{
		lock (_lock)
			return _historySeen.Count == 0 ? "none" : string.Join(", ", _historySeen.OrderBy(t => t.Key).Select(t => $"{t.Key} {t.Value}"));
	}

	private async ValueTask HoldAsync(HistorySyncType type, Action holdStart, CancellationToken cancellationToken)
	{
		lock (_lock)
		{
			_historySeen[type] = _historySeen.GetValueOrDefault(type) + 1;
			if (_released.Task.IsCompleted)
				return;
			if (!_holding)
			{
				_holding = true;
				holdStart();
			}
		}

		await _released.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
	}

	private void ReleaseHold()
	{
		lock (_lock)
		{
			if (!_released.TrySetResult() || !_holding)
				return;
		}

		_holdEnd?.Invoke();
	}
}

/// <summary>What <see cref="FreshCompanion.RunAsync"/> runs, and what it proves the session against.</summary>
internal sealed record FreshRun
{
	/// <summary>Pairs the session and calls <see cref="FreshCompanion.Paired"/> once it is paired.</summary>
	public required Func<FreshCompanion, CancellationToken, Task> PairAsync { get; init; }

	/// <summary>The account the session must be.</summary>
	public required (Jid Chat, Jid? Lid) Expected { get; init; }

	/// <summary>The account it must not be.</summary>
	public required (Jid Chat, Jid? Lid) Forbidden { get; init; }

	/// <summary>The devices of the other stores this run can read, which the session must not be.</summary>
	public required IReadOnlyCollection<Jid> OtherDevices { get; init; }

	/// <summary>The expected account's devices known from elsewhere (root's preflight), which the session must not be.</summary>
	public required IReadOnlyCollection<Jid> KnownDevices { get; init; }

	/// <summary>The steps that need a store that has not cached the value yet: run with the history held.</summary>
	public required IReadOnlyList<FreshStep> FirstSight { get; init; }

	/// <summary>The steps after the hold's release.</summary>
	public required IReadOnlyList<FreshStep> Then { get; init; }

	/// <summary>Ends the steps at the capture bound: a step after it does not run, one running is cancelled.</summary>
	public required CancellationToken Deadline { get; init; }

	/// <summary>The capture's window, which returns how it ended.</summary>
	public required Func<CancellationToken, Task<string>> WindowAsync { get; init; }

	/// <summary>Tells the record what the run proved and how the cleanup ended.</summary>
	public required Action<FreshEvent> Record { get; init; }
}

/// <summary>One step of a <see cref="FreshRun"/>.</summary>
internal sealed record FreshStep
{
	public required string Name { get; init; }

	public required Func<CancellationToken, Task> Run { get; init; }
}

/// <summary>What <see cref="FreshCompanion.RunAsync"/> tells the record.</summary>
internal enum FreshEvent
{
	IdentityVerified,
	IdentityRejected,
	LogoutVerified,
	LoggedOutExternally,
}

/// <summary>How <see cref="FreshCompanion.LogoutAsync"/> ended.</summary>
internal enum FreshLogout
{
	/// <summary>This cleanup logged out, read LoggedOut 0 and deleted the session.</summary>
	Verified,

	/// <summary>The session was LoggedOut before this cleanup's logout: kept, and never this cleanup's verified logout.</summary>
	LoggedOutExternally,

	/// <summary>Kept, with its keys: the device may still be linked.</summary>
	Kept,
}
