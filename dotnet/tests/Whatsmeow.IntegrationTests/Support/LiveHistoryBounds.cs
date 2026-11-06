using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Whatsmeow.History;
using Whatsmeow.Sidecar;

namespace Whatsmeow.IntegrationTests.Support;

/// <summary>
/// #42's record of one fresh pairing's history per sync type (batches, entries, message timestamp bounds, the times
/// its handler saw them), with the pairing, the hold #36's test puts on the history, the options and the sidecar. One
/// JSON line per record, flushed as written, to a new private file that is kept; aggregates and timestamps only, never
/// a JID, id, name or text. It asserts nothing a test depends on. The live fact uses it; its rules are checked offline
/// in <c>LiveHistoryBoundsTests</c>.
/// <para>
/// Times: every deadline and duration is on the provider's monotonic clock (<c>MonoNs</c>, since the record began);
/// UTC only dates the lines. A row's time is when the record's handler ran, not when WhatsApp sent it: the
/// <c>@history</c> lane runs one event at a time, so an event queued behind a held one is seen at the hold's release.
/// Register it before the hold's handler: an event's handlers run in registration order (Events/EventPipeline.cs,
/// <c>On</c> appends, <c>RunAsync</c> runs them one after the other).
/// </para>
/// <para>
/// The window is the half-open span from <see cref="Paired"/> to its close, which is the first of: <c>quiet_window</c>
/// (<c>quiet_no_history</c> when no history came) at <see cref="Quiet"/> after the later of the last history event in
/// the window and the hold's end, never while the hold is on; <c>censored_bound</c> at <see cref="Bound"/> after the
/// pairing, which wins a tie. Those closes are logical times: each is recorded as <c>CloseNs</c> on the <c>finish</c>
/// row, whose own time is when the record noticed it (a timer of the provider, the next event, <see cref="WindowAsync"/>
/// or <see cref="Summarize"/>), so the close does not depend on when the window is awaited. An event is dated when its
/// handler ran, never earlier; one seen at or after the close is outside the window (<c>AfterFinish</c>), and one at or
/// after the bound also <c>Late</c>. Without a rule's close, a cancelled <see cref="WindowAsync"/> closes it as
/// <c>cancelled</c>, and <see cref="Summarize"/> as <c>not_closed</c>; a rule whose time came first still wins.
/// </para>
/// </summary>
internal sealed partial class LiveHistoryBounds : IDisposable
{
	public static readonly TimeSpan Quiet = TimeSpan.FromMinutes(10);
	public static readonly TimeSpan Bound = TimeSpan.FromMinutes(30);

	// whatsmeow store/clientpayload.go:131-134 sends a 10240 MB quota and no days or size limit by default; a value
	// above 0 becomes a sidecar flag (Sidecar/SidecarProcess.cs:229-233) that overrides it (cmd/whatsmeow-net/history.go:30-51).
	private const uint DefaultStorageQuotaMb = 10240;
	private const string ResolvedBasis =
		"whatsmeow 307a2edf117d store/clientpayload.go:131-134,156; sidecar cmd/whatsmeow-net/history.go:30-51; client Sidecar/SidecarProcess.cs:229-233; not measured";

	private readonly Lock _gate = new();
	private readonly TimeProvider _time;
	private readonly long _origin;
	private readonly string _path;
	private readonly SortedDictionary<string, TypeSummary> _types = new(StringComparer.Ordinal);
	private readonly List<IDisposable> _registrations = [];
	private readonly CancellationTokenSource _deadline = new();
	private readonly TaskCompletionSource<string> _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
	private readonly ITimer _quietTimer;
	private ITimer? _deadlineTimer;
	private TextWriter? _writer;
	private string? _session;
	private int _lines;
	private int _writeFailures;
	private int _invalidations;
	private int _excluded;
	private bool _summarized;
	private bool _disposed;
	private TimeSpan? _paired;
	private TimeSpan? _lastHistory;
	private TimeSpan? _holdStart;
	private TimeSpan? _holdEnd;
	private string? _outcome;

	/// <summary>Creates <paramref name="path"/> new (0600 at creation) in a private directory that is not a link.</summary>
	public LiveHistoryBounds(TimeProvider time, string path)
	{
		_time = time;
		_origin = time.GetTimestamp();
		LoggerProvider = new Provider(this);
		_path = Path.GetFullPath(path);
		var directory = new DirectoryInfo(Path.GetDirectoryName(_path)!);
		if (!directory.Exists || directory.LinkTarget is not null)
			throw new InvalidOperationException("the history record needs an existing directory that is not a link");
		if (!OperatingSystem.IsWindows() && (directory.UnixFileMode & (UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute)) != UnixFileMode.None)
			throw new InvalidOperationException("the history record's directory must be private");

		_writer = new StreamWriter(new FileStream(_path, Create()));
		_quietTimer = time.CreateTimer(_ => Observe(), state: null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
	}

	/// <summary>
	/// Cancelled at <see cref="Bound"/> after <see cref="Paired"/>, on the provider's clock, whatever closed the window:
	/// work that feeds the capture stops at its bound. Never cancelled before a pairing.
	/// </summary>
	public CancellationToken Deadline => _deadline.Token;

	/// <summary>Before pairing, and before the hold's handler.</summary>
	public void Register(IWhatsAppClient client)
	{
		ArgumentNullException.ThrowIfNull(client);
		lock (_gate)
			_session = client.SessionId.Value;
		_registrations.Add(client.On<HistoryReceived>(OnBatchAsync));
		_registrations.Add(client.On<HistorySyncProgressed>(OnProgressAsync));
	}

	public void Provenance(SidecarInfo info, string binary, string build)
	{
		ArgumentNullException.ThrowIfNull(info);
		string sha;
		using (var stream = File.OpenRead(binary))
			sha = Convert.ToHexStringLower(SHA256.HashData(stream));
		Mark("run_start", $"build={build} sidecar_sha256={sha} version={info.Version} commit={info.Commit} whatsmeow={info.WhatsmeowVersion} go={info.GoVersion} protocol={info.Protocol}");
	}

	/// <summary>The options the pairing's sidecar started with: requested as given, nulls as null, and resolved from the source.</summary>
	public void Options(HistorySyncOptions options)
	{
		ArgumentNullException.ThrowIfNull(options);
		var line = JsonSerializer.Serialize(new OptionsRow
		{
			Utc = _time.GetUtcNow(),
			RequestedFullSync = options.RequestFullSync,
			RequestedDaysLimit = options.FullSyncDaysLimit,
			RequestedSizeLimitMb = options.FullSyncSizeLimitMegabytes,
			RequestedStorageQuotaMb = options.StorageQuotaMegabytes,
			ResolvedRequireFullSync = options.RequestFullSync,
			ResolvedDaysLimit = Positive(options.FullSyncDaysLimit),
			ResolvedSizeLimitMb = Positive(options.FullSyncSizeLimitMegabytes),
			ResolvedStorageQuotaMb = Positive(options.StorageQuotaMegabytes) ?? DefaultStorageQuotaMb,
			ResolvedBasis = ResolvedBasis,
		}, HistoryJson.Default.OptionsRow);
		lock (_gate)
		{
			if (StoppedLocked)
				return;
			WriteLocked(line);
		}
	}

	/// <summary>The pairing finished: the window, the quiet and <see cref="Deadline"/> start here; only the first call counts.</summary>
	public void Paired()
	{
		lock (_gate)
		{
			if (StoppedLocked || _paired is not null)
				return;
			var now = Now();
			_paired = now;
			AddLocked(RowLocked("paired", now));
			_deadlineTimer = _time.CreateTimer(_ => OnDeadline(), state: null, Bound, Timeout.InfiniteTimeSpan);
			UpdateLocked(now);
		}
	}

	public void FreshIdentityVerified() => Mark("fresh_identity_verified");

	public void FreshIdentityRejected() => Mark("fresh_identity_rejected", ok: false);

	/// <summary>Only after this cleanup's own logout succeeded and the session then read LoggedOut.</summary>
	public void LogoutVerified() => Mark("logout_verified");

	/// <summary>The session was logged out before this cleanup logged it out: not a verified logout.</summary>
	public void LoggedOutExternally() => Mark("logged_out_externally", ok: false);

	/// <summary>From inside the first history handler that blocks; only the first call counts. The window cannot close quiet until <see cref="HoldEnd"/>.</summary>
	public void HoldStart()
	{
		lock (_gate)
		{
			if (StoppedLocked || _holdStart is not null)
				return;
			var now = Now();
			UpdateLocked(now);
			_holdStart = now;
			AddLocked(RowLocked("hold_start", now));
			UpdateLocked(now);
		}
	}

	/// <summary>The hold's release; only the first after <see cref="HoldStart"/> counts. The quiet starts again here at the latest.</summary>
	public void HoldEnd()
	{
		lock (_gate)
		{
			if (StoppedLocked || _holdStart is not { } start || _holdEnd is not null)
				return;
			var now = Now();
			UpdateLocked(now);
			_holdEnd = now;
			AddLocked(RowLocked("hold_end", now) with { Detail = $"hold_seconds={(now - start).TotalSeconds:F3}" });
			UpdateLocked(now);
		}
	}

	/// <summary>
	/// Waits for the window's close and returns its outcome; <c>not_paired</c> at once without a pairing. A token that is
	/// or becomes cancelled before a close closes it as <c>cancelled</c>, recorded, never thrown. Calling it again returns
	/// the same outcome.
	/// </summary>
	public async Task<string> WindowAsync(CancellationToken cancellationToken)
	{
		lock (_gate)
		{
			var now = Now();
			UpdateLocked(now);
			if (_outcome is null && _paired is null)
				CloseLocked("not_paired", now, now);
			if (_outcome is { } outcome)
				return outcome;
		}

		try
		{
			return await _closed.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
		}
		catch (OperationCanceledException)
		{
			lock (_gate)
			{
				var now = Now();
				UpdateLocked(now);
				if (_outcome is null)
					CloseLocked("cancelled", now, now);
				return _outcome!;
			}
		}
	}

	/// <summary>
	/// Once, in the caller's finally: closes an open window as <c>not_closed</c>, stops recording (a callback the dispatcher
	/// already took after this point writes nothing), writes the summary per type and the record's own, closes the file,
	/// then writes the receipt only if the close succeeded. Without the receipt the record is not complete.
	/// </summary>
	public void Summarize()
	{
		lock (_gate)
		{
			if (StoppedLocked)
				return;
			var now = Now();
			UpdateLocked(now);
			if (_outcome is null)
				CloseLocked("not_closed", now, now);
			_summarized = true;
		}

		DisposeRegistrations();

		lock (_gate)
		{
			var now = Now();
			var batches = 0L;
			var entries = 0L;
			foreach (var (type, summary) in _types)
			{
				batches += summary.Batches;
				entries += summary.Entries;
				AddLocked(RowLocked("history_type_summary", now) with
				{
					SyncType = type,
					Batches = summary.Batches,
					Entries = summary.Entries,
					MinMessage = summary.MinMessage,
					MaxMessage = summary.MaxMessage,
					FirstObserved = summary.FirstObserved,
					LastObserved = summary.LastObserved,
				});
			}

			AddLocked(RowLocked("capture_summary", now) with
			{
				Ok = _writeFailures == 0 && _invalidations == 0,
				Batches = batches,
				Entries = entries,
				Detail = $"write_failures={_writeFailures} invalidations={_invalidations} excluded={_excluded}",
			});

			// The receipt attests only the close and the bytes; validity stays in capture_summary, which the analyzer reads.
			if (CloseWriterLocked())
				WriteReceipt();
		}
	}

	/// <summary>
	/// Add this, not the record, to the pairing sidecar's logger factory: it does not own the record, so the factory's
	/// disposal cannot close it before <see cref="Summarize"/>. In the client's category (<see cref="SidecarLog.Category"/>),
	/// the sidecar's exit (2), restart (3) or failed restart (4) (Sidecar/SidecarLog.cs:12-19), and for the registered
	/// session a history message skipped (41, History/HistoryLog.cs:5) or any event skipped unread (32, Events/EventLog.cs:11,
	/// whose kind is not named, so possibly history) each make the record invalid, whenever they come before the summary.
	/// </summary>
	public ILoggerProvider LoggerProvider { get; }

	/// <summary>After <see cref="Summarize"/>. Before it, the record says so in its last line and gets no receipt.</summary>
	public void Dispose()
	{
		lock (_gate)
		{
			if (_disposed)
				return;
			if (!_summarized && _writer is not null)
			{
				_writeFailures++;
				AddLocked(RowLocked("disposed_before_summary", Now()) with { Ok = false });
			}

			_disposed = true;
			_ = CloseWriterLocked();
			_quietTimer.Dispose();
			_deadlineTimer?.Dispose();
		}

		DisposeRegistrations();
		_deadline.Dispose();
	}

	private bool StoppedLocked => _summarized || _disposed;

	private TimeSpan Now() => _time.GetElapsedTime(_origin);

	// An empty HistoryReceived does not reach here: HistoryMapping.ToEvent returns no event for a batch without a readable
	// message (History/HistoryMapping.cs:52), so Messages[0] exists.
	private ValueTask OnBatchAsync(HistoryReceived e, CancellationToken cancellationToken)
	{
		var min = e.Messages[0].Timestamp;
		var max = min;
		foreach (var message in e.Messages)
		{
			if (message.Timestamp < min)
				min = message.Timestamp;
			if (message.Timestamp > max)
				max = message.Timestamp;
		}

		lock (_gate)
		{
			if (StoppedLocked)
				return ValueTask.CompletedTask;
			var now = Now();
			var row = RowLocked("history_batch", now) with { SyncType = e.SyncType.ToString(), Entries = e.Messages.Length, MinMessage = min, MaxMessage = max };
			if (EnterLocked(now, ref row))
				CountLocked(row);
			AddLocked(row);
		}

		return ValueTask.CompletedTask;
	}

	private ValueTask OnProgressAsync(HistorySyncProgressed e, CancellationToken cancellationToken)
	{
		lock (_gate)
		{
			if (StoppedLocked)
				return ValueTask.CompletedTask;
			var now = Now();
			var row = RowLocked("history_progress", now) with
			{
				SyncType = e.SyncType.ToString(),
				ChunkOrder = e.ChunkOrder,
				Progress = e.Progress,
				MessageCount = e.MessageCount,
				Skipped = e.SkippedCount,
				Failure = e.Failure?.ToString(),
			};
			_ = EnterLocked(now, ref row);
			AddLocked(row);
		}

		return ValueTask.CompletedTask;
	}

	// A history event seen now: any close due by now is recorded first, so the event is in the window only if it came
	// before the close; one in the window moves the quiet start. Returns whether it is in the window.
	private bool EnterLocked(TimeSpan now, ref Row row)
	{
		UpdateLocked(now);
		if (_outcome is not null)
		{
			_excluded++;
			row = row with { AfterFinish = true, Late = _paired is { } paired && now >= paired + Bound ? true : null };
			return false;
		}

		_lastHistory = now;
		UpdateLocked(now);
		return true;
	}

	private void CountLocked(Row batch)
	{
		var type = batch.SyncType!;
		var summary = _types.TryGetValue(type, out var found)
			? found
			: new TypeSummary { MinMessage = batch.MinMessage!.Value, MaxMessage = batch.MaxMessage!.Value, FirstObserved = batch.Utc, LastObserved = batch.Utc };
		_types[type] = summary with
		{
			Batches = summary.Batches + 1,
			Entries = summary.Entries + batch.Entries!.Value,
			MinMessage = batch.MinMessage < summary.MinMessage ? batch.MinMessage.Value : summary.MinMessage,
			MaxMessage = batch.MaxMessage > summary.MaxMessage ? batch.MaxMessage.Value : summary.MaxMessage,
			FirstObserved = batch.Utc < summary.FirstObserved ? batch.Utc : summary.FirstObserved,
			LastObserved = batch.Utc > summary.LastObserved ? batch.Utc : summary.LastObserved,
		};
	}

	private void Observe()
	{
		lock (_gate)
			UpdateLocked(Now());
	}

	private void OnDeadline()
	{
		Observe();
		try
		{
			_deadline.Cancel();
		}
		catch (ObjectDisposedException)
		{
			// Disposed while the timer fired: nothing waits on the bound any more.
		}
	}

	// Records the close whose logical time came by now, the earlier of the quiet and the bound, the bound on a tie;
	// otherwise sets the quiet timer for the next possible close.
	private void UpdateLocked(TimeSpan now)
	{
		if (_disposed || _outcome is not null || _paired is not { } paired)
			return;

		var bound = paired + Bound;
		var quiet = QuietLocked(paired);
		if (quiet < bound && quiet <= now)
		{
			CloseLocked(_lastHistory is null ? "quiet_no_history" : "quiet_window", quiet.Value, now);
			return;
		}

		if (bound <= now)
		{
			CloseLocked("censored_bound", bound, now);
			return;
		}

		_ = _quietTimer.Change(quiet < bound ? quiet.Value - now : Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
	}

	// When the window closes quiet without a further history event: Quiet after the later of the pairing, the last
	// history event and the hold's end; never while the hold is on.
	private TimeSpan? QuietLocked(TimeSpan paired)
	{
		if (_holdStart is not null && _holdEnd is null)
			return null;
		var start = paired;
		if (_lastHistory > start)
			start = _lastHistory.Value;
		if (_holdEnd > start)
			start = _holdEnd.Value;
		return start + Quiet;
	}

	private void CloseLocked(string outcome, TimeSpan at, TimeSpan now)
	{
		_outcome = outcome;
		_ = _quietTimer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
		AddLocked(RowLocked("finish", now) with { Detail = outcome, CloseNs = Nanoseconds(at) });
		_ = _closed.TrySetResult(outcome);
	}

	private void Invalidate(string kind)
	{
		lock (_gate)
		{
			if (StoppedLocked)
				return;
			_invalidations++;
			AddLocked(RowLocked(kind, Now()) with { Ok = false });
		}
	}

	private bool ForSession(string? session)
	{
		lock (_gate)
			return session is null || _session is null || string.Equals(session, _session, StringComparison.Ordinal);
	}

	private void Mark(string kind, string? detail = null, bool ok = true)
	{
		lock (_gate)
		{
			if (StoppedLocked)
				return;
			AddLocked(RowLocked(kind, Now()) with { Detail = detail, Ok = ok });
		}
	}

	private Row RowLocked(string kind, TimeSpan now) => new() { Kind = kind, Utc = _time.GetUtcNow(), MonoNs = Nanoseconds(now) };

	private static long Nanoseconds(TimeSpan since) => since.Ticks * 100;

	private void AddLocked(Row row) => WriteLocked(JsonSerializer.Serialize(row, HistoryJson.Default.Row));

	private void WriteLocked(string line)
	{
		if (_writer is null)
		{
			_writeFailures++;
			return;
		}

		try
		{
			_writer.WriteLine(line);
			_writer.Flush();
			_lines++;
		}
#pragma warning disable CA1031 // Justification: a full disk or a closed file makes the record invalid; it never fails the test or prints a path.
		catch (Exception)
#pragma warning restore CA1031
		{
			_writeFailures++;
		}
	}

	private bool CloseWriterLocked()
	{
		if (_writer is null)
			return false;
		try
		{
			_writer.Dispose();
			return true;
		}
#pragma warning disable CA1031 // Justification: as in WriteLocked; a failed close leaves no receipt.
		catch (Exception)
#pragma warning restore CA1031
		{
			_writeFailures++;
			return false;
		}
		finally
		{
			_writer = null;
		}
	}

	// The record's sha256 and line count, read back after the close, in a new file beside it; any failure leaves no receipt.
	private void WriteReceipt()
	{
		try
		{
			string sha;
			using (var stream = File.OpenRead(_path))
				sha = Convert.ToHexStringLower(SHA256.HashData(stream));
			using var receipt = new StreamWriter(new FileStream(_path + ".receipt.json", Create()));
			receipt.WriteLine(JsonSerializer.Serialize(new Receipt { Sha256 = sha, Lines = _lines }, HistoryJson.Default.Receipt));
		}
#pragma warning disable CA1031 // Justification: no receipt is the failure the analyzer refuses.
		catch (Exception)
#pragma warning restore CA1031
		{
			_writeFailures++;
		}
	}

	private void DisposeRegistrations()
	{
		IDisposable[] registrations;
		lock (_gate)
		{
			registrations = [.. _registrations];
			_registrations.Clear();
		}

		foreach (var registration in registrations)
			registration.Dispose();
	}

	private static FileStreamOptions Create()
	{
		var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.Read };
		if (!OperatingSystem.IsWindows())
			options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
		return options;
	}

	private static uint? Positive(int? value) => value is > 0 ? (uint)value.Value : null;

	private sealed record TypeSummary
	{
		public long Batches { get; init; }
		public long Entries { get; init; }
		public required DateTimeOffset MinMessage { get; init; }
		public required DateTimeOffset MaxMessage { get; init; }
		public required DateTimeOffset FirstObserved { get; init; }
		public required DateTimeOffset LastObserved { get; init; }
	}

	private sealed class Provider(LiveHistoryBounds owner) : ILoggerProvider
	{
		public ILogger CreateLogger(string categoryName) => new Records(owner, categoryName);

		public void Dispose()
		{
		}
	}

	private sealed class Records(LiveHistoryBounds owner, string category) : ILogger
	{
		public IDisposable? BeginScope<TState>(TState state)
			where TState : notnull => null;

		public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

		public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
		{
			if (!string.Equals(category, SidecarLog.Category, StringComparison.Ordinal))
				return;
			var kind = eventId.Id switch
			{
				2 => "sidecar_exit",
				3 => "sidecar_restart",
				4 => "sidecar_restart_failed",
				41 when owner.ForSession(Session(state)) => "history_message_skipped",
				32 when owner.ForSession(Session(state)) => "event_skipped",
				_ => null,
			};
			if (kind is not null)
				owner.Invalidate(kind);
		}

		// The SessionId a [LoggerMessage] record carries as a structured value; null without one.
		private static string? Session<TState>(TState state) =>
			state is IReadOnlyList<KeyValuePair<string, object?>> values
				? values.FirstOrDefault(v => string.Equals(v.Key, "SessionId", StringComparison.Ordinal)).Value as string
				: null;
	}

	internal sealed record Row
	{
		public required string Kind { get; init; }
		public required DateTimeOffset Utc { get; init; }
		public required long MonoNs { get; init; }
		public bool Ok { get; init; } = true;
		public string? Detail { get; init; }
		public string? SyncType { get; init; }
		public long? Entries { get; init; }
		public DateTimeOffset? MinMessage { get; init; }
		public DateTimeOffset? MaxMessage { get; init; }
		public long? ChunkOrder { get; init; }
		public long? Progress { get; init; }
		public long? MessageCount { get; init; }
		public long? Skipped { get; init; }
		public string? Failure { get; init; }
		public long? Batches { get; init; }
		public DateTimeOffset? FirstObserved { get; init; }
		public DateTimeOffset? LastObserved { get; init; }
		public bool? AfterFinish { get; init; }
		public bool? Late { get; init; }
		public long? CloseNs { get; init; }
	}

	internal sealed record OptionsRow
	{
		public string Kind { get; init; } = "run_options";
		public required DateTimeOffset Utc { get; init; }
		public required bool RequestedFullSync { get; init; }
		[JsonIgnore(Condition = JsonIgnoreCondition.Never)]
		public required int? RequestedDaysLimit { get; init; }
		[JsonIgnore(Condition = JsonIgnoreCondition.Never)]
		public required int? RequestedSizeLimitMb { get; init; }
		[JsonIgnore(Condition = JsonIgnoreCondition.Never)]
		public required int? RequestedStorageQuotaMb { get; init; }
		public required bool ResolvedRequireFullSync { get; init; }
		[JsonIgnore(Condition = JsonIgnoreCondition.Never)]
		public required uint? ResolvedDaysLimit { get; init; }
		[JsonIgnore(Condition = JsonIgnoreCondition.Never)]
		public required uint? ResolvedSizeLimitMb { get; init; }
		[JsonIgnore(Condition = JsonIgnoreCondition.Never)]
		public required uint? ResolvedStorageQuotaMb { get; init; }
		public required string ResolvedBasis { get; init; }
	}

	internal sealed record Receipt
	{
		public string Kind { get; init; } = "completion_receipt";
		public required string Sha256 { get; init; }
		public required int Lines { get; init; }
	}

	[JsonSourceGenerationOptions(DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
	[JsonSerializable(typeof(Row))]
	[JsonSerializable(typeof(OptionsRow))]
	[JsonSerializable(typeof(Receipt))]
	internal sealed partial class HistoryJson : JsonSerializerContext;
}
