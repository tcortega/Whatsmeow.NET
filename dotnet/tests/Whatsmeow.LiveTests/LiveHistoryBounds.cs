using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Whatsmeow.History;

namespace Whatsmeow.LiveTests;

/// <summary>
/// #42's record of one fresh pairing's history per sync type (batches, entries, message timestamp bounds, arrival
/// times), with the pairing, the hold #36's test puts on the history, the options and the sidecar. One JSON line per
/// record, flushed as written, to a new private file that is kept; aggregates and timestamps only, never a JID, id, name
/// or text. Deadlines and durations use the provider's monotonic timestamps; UTC only dates the lines. It asserts
/// nothing a test depends on. Register it before the hold's handler: an event's handlers run in registration order
/// (Events/EventPipeline.cs, <c>On</c> appends, <c>RunAsync</c> runs them one after the other).
/// </summary>
internal sealed partial class LiveHistoryBounds : IDisposable
{
	public static readonly TimeSpan Quiet = TimeSpan.FromMinutes(10);
	public static readonly TimeSpan Bound = TimeSpan.FromMinutes(30);
	public static readonly TimeSpan Poll = TimeSpan.FromSeconds(5);

	private const int MaxRows = 200_000;

	// whatsmeow store/clientpayload.go sends a 10240 MB quota and no days or size limit by default; a value above 0
	// becomes a sidecar flag that overrides it (Sidecar/SidecarProcess.cs, cmd/whatsmeow-net/history.go).
	private const uint DefaultStorageQuotaMb = 10240;
	private const string ResolvedBasis = "source at main e5cf3b1 and whatsmeow 307a2edf117d, not measured";

	private readonly Lock _gate = new();
	private readonly TimeProvider _time;
	private readonly long _origin;
	private readonly string? _path;
	private readonly List<Row> _rows = [];
	private readonly List<IDisposable> _registrations = [];
	private TextWriter? _writer;
	private int _lines;
	private int _writeFailures;
	private int _lifecycleFailures;
	private int _dropped;
	private bool _finished;
	private bool _summarized;
	private long? _paired;
	private long? _lastHistory;
	private long? _holdStart;

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
	}

	/// <summary>Offline replay only: records written to <paramref name="writer"/>; a receipt only for <paramref name="receiptFor"/>.</summary>
	internal LiveHistoryBounds(TimeProvider time, TextWriter writer, string? receiptFor = null)
	{
		_time = time;
		_origin = time.GetTimestamp();
		LoggerProvider = new Provider(this);
		_writer = writer;
		_path = receiptFor;
	}

	/// <summary>The receipt <see cref="Summarize"/> writes beside the record once it is closed.</summary>
	public string? ReceiptPath => _path is null ? null : _path + ".receipt.json";

	public bool Valid
	{
		get
		{
			lock (_gate)
				return _writeFailures == 0 && _lifecycleFailures == 0 && _dropped == 0;
		}
	}

	internal IReadOnlyList<Row> Rows
	{
		get
		{
			lock (_gate)
				return [.. _rows];
		}
	}

	/// <summary>Before pairing, and before the hold's handler.</summary>
	public void Register(IWhatsAppClient client)
	{
		ArgumentNullException.ThrowIfNull(client);
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
			WriteLocked(line, keep: null);
	}

	/// <summary>The pairing finished: the 30-minute bound starts here.</summary>
	public void Paired()
	{
		lock (_gate)
		{
			var row = MakeLocked("paired", out var stamp);
			_paired ??= stamp;
			AddLocked(row);
		}
	}

	public void FreshIdentityVerified() => Mark("fresh_identity_verified");

	public void FreshIdentityRejected() => Mark("fresh_identity_rejected", ok: false);

	public void LogoutVerified() => Mark("logout_verified");

	/// <summary>From inside the first history handler that blocks; only the first call counts.</summary>
	public void HoldStart()
	{
		lock (_gate)
		{
			if (_holdStart is not null)
				return;
			var row = MakeLocked("hold_start", out var stamp);
			_holdStart = stamp;
			AddLocked(row);
		}
	}

	public void HoldEnd()
	{
		lock (_gate)
		{
			var row = MakeLocked("hold_end", out var stamp);
			var detail = _holdStart is { } start ? $"hold_seconds={_time.GetElapsedTime(start, stamp).TotalSeconds:F3}" : "no_hold_start";
			AddLocked(row with { Detail = detail });
		}
	}

	/// <summary>
	/// The window's outcome now, or null while it is open: <c>quiet_window</c> 10
	/// minutes after the last history event, <c>quiet_no_history</c> 10 minutes after the pairing when none came,
	/// <c>censored_bound</c> 30 minutes after the pairing, <c>not_paired</c> without one. Monotonic time only.
	/// </summary>
	public string? Outcome()
	{
		lock (_gate)
			return OutcomeLocked(_time.GetTimestamp());
	}

	/// <summary>
	/// Waits until the window closes, then records its outcome; the decision and the cutoff are one step under the lock
	/// the handlers record under, so an event either counts and keeps the window open, or comes after it. A token that is
	/// or becomes cancelled closes it as <c>cancelled</c>, recorded and incomplete, never thrown.
	/// </summary>
	public async Task<string> WindowAsync(CancellationToken cancellationToken)
	{
		while (true)
		{
			if (cancellationToken.IsCancellationRequested)
				return Close("cancelled");

			lock (_gate)
			{
				if (_finished)
					return "already_closed";
				if (OutcomeLocked(_time.GetTimestamp()) is { } outcome)
					return CloseLocked(outcome);
			}

			try
			{
				await Task.Delay(Poll, _time, cancellationToken).ConfigureAwait(false);
			}
			catch (OperationCanceledException)
			{
				return Close("cancelled");
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
			if (_summarized)
				return;
			if (!_finished)
				_ = CloseLocked("not_closed");
			_summarized = true;
		}

		foreach (var registration in _registrations)
			registration.Dispose();

		Row[] window;
		int after;
		lock (_gate)
		{
			window = [.. _rows.Where(r => r.Kind == "history_batch" && r.AfterFinish is not true)];
			after = _rows.Count(r => r.Kind == "history_batch" && r.AfterFinish is true);
		}

		lock (_gate)
		{
			foreach (var type in window.GroupBy(r => r.SyncType!, StringComparer.Ordinal).OrderBy(g => g.Key, StringComparer.Ordinal))
			{
				AddLocked(MakeLocked("history_type_summary", out _) with
				{
					SyncType = type.Key,
					Batches = type.Count(),
					Entries = type.Sum(r => r.Entries!.Value),
					MinMessage = type.Min(r => r.MinMessage),
					MaxMessage = type.Max(r => r.MaxMessage),
					FirstBatch = type.Min(r => r.Utc),
					LastBatch = type.Max(r => r.Utc),
				});
			}

			AddLocked(MakeLocked("capture_summary", out _) with
			{
				Ok = _writeFailures == 0 && _lifecycleFailures == 0 && _dropped == 0,
				Batches = window.Length,
				Entries = window.Sum(r => r.Entries!.Value),
				Detail = $"write_failures={_writeFailures} lifecycle_failures={_lifecycleFailures} dropped={_dropped} batches_after_finish={after}",
			});

			// The receipt attests only the close and the bytes; validity stays in capture_summary, which the analyzer reads.
			if (CloseWriterLocked() && _path is not null)
				WriteReceipt();
		}
	}

	// Each callback records in one step under the lock the window closes under: its entry time, whether it counts (one
	// that counts moves the quiet start, so the window cannot close past it), its bounds and its line. A callback the
	// dispatcher runs after the summary, from a snapshot taken before the registration ended
	// (Events/EventPipeline.cs RunAsync), writes nothing.
	internal ValueTask OnBatchAsync(HistoryReceived e, CancellationToken cancellationToken)
	{
		lock (_gate)
		{
			if (Enter() is not { } entry)
				return ValueTask.CompletedTask;

			var min = e.Messages[0].Timestamp;
			var max = min;
			foreach (var message in e.Messages)
			{
				if (message.Timestamp < min)
					min = message.Timestamp;
				if (message.Timestamp > max)
					max = message.Timestamp;
			}

			Record(entry, new Row { Kind = "history_batch", Utc = entry.Utc, MonoNs = Nanoseconds(entry.Stamp), SyncType = e.SyncType.ToString(), Entries = e.Messages.Length, MinMessage = min, MaxMessage = max });
		}

		return ValueTask.CompletedTask;
	}

	internal ValueTask OnProgressAsync(HistorySyncProgressed e, CancellationToken cancellationToken)
	{
		lock (_gate)
		{
			if (Enter() is not { } entry)
				return ValueTask.CompletedTask;

			Record(entry, new Row
			{
				Kind = "history_progress",
				Utc = entry.Utc,
				MonoNs = Nanoseconds(entry.Stamp),
				SyncType = e.SyncType.ToString(),
				ChunkOrder = e.ChunkOrder,
				Progress = e.Progress,
				MessageCount = e.MessageCount,
				Skipped = e.SkippedCount,
				Failure = e.Failure?.ToString(),
			});
		}

		return ValueTask.CompletedTask;
	}

	private readonly record struct Entry
	{
		public required long Stamp { get; init; }

		public required DateTimeOffset Utc { get; init; }

		public required bool InWindow { get; init; }
	}

	// Under the lock: null after the summary.
	private Entry? Enter()
	{
		if (_summarized)
			return null;
		var stamp = _time.GetTimestamp();
		var inWindow = !_finished;
		if (inWindow && (_lastHistory is null || stamp > _lastHistory))
			_lastHistory = stamp;
		return new Entry { Stamp = stamp, Utc = _time.GetUtcNow(), InWindow = inWindow };
	}

	// Under the lock.
	private void Record(Entry entry, Row row) => AddLocked(entry.InWindow ? row : row with { AfterFinish = true });

	internal void Lifecycle(int eventId)
	{
		lock (_gate)
		{
			if (_summarized)
				return;
			_lifecycleFailures++;
			AddLocked(MakeLocked(eventId switch { 2 => "sidecar_exit", 3 => "sidecar_restart", _ => "sidecar_restart_failed" }, out _) with { Ok = false });
		}
	}

	private string? OutcomeLocked(long now)
	{
		if (_paired is not { } paired)
			return "not_paired";
		if (_time.GetElapsedTime(_lastHistory ?? paired, now) >= Quiet)
			return _lastHistory is null ? "quiet_no_history" : "quiet_window";
		return _time.GetElapsedTime(paired, now) >= Bound ? "censored_bound" : null;
	}

	private string Close(string outcome)
	{
		lock (_gate)
			return _finished ? "already_closed" : CloseLocked(outcome);
	}

	private string CloseLocked(string outcome)
	{
		_finished = true;
		AddLocked(MakeLocked("finish", out _) with { Detail = outcome });
		return outcome;
	}

	private void Mark(string kind, string? detail = null, bool ok = true)
	{
		lock (_gate)
			AddLocked(MakeLocked(kind, out _) with { Detail = detail, Ok = ok });
	}

	private Row MakeLocked(string kind, out long stamp)
	{
		stamp = _time.GetTimestamp();
		return new Row { Kind = kind, Utc = _time.GetUtcNow(), MonoNs = Nanoseconds(stamp) };
	}

	// Nanoseconds since the record started: a raw timestamp can be far too large to scale (a provider may count ticks
	// since year 1).
	private long Nanoseconds(long stamp) => _time.GetElapsedTime(_origin, stamp).Ticks * 100;

	private void AddLocked(Row row) => WriteLocked(JsonSerializer.Serialize(row, HistoryJson.Default.Row), row);

	private void WriteLocked(string line, Row? keep)
	{
		if (keep is not null)
		{
			if (_rows.Count >= MaxRows)
			{
				_dropped++;
				return;
			}

			_rows.Add(keep);
		}

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

	// The record's sha256 and line count, read back after the close; any failure leaves no receipt.
	private void WriteReceipt()
	{
		try
		{
			string sha;
			using (var stream = File.OpenRead(_path!))
				sha = Convert.ToHexStringLower(SHA256.HashData(stream));
			using var receipt = new StreamWriter(new FileStream(ReceiptPath!, Create()));
			receipt.WriteLine(JsonSerializer.Serialize(new Receipt { Sha256 = sha, Lines = _lines }, HistoryJson.Default.Receipt));
		}
#pragma warning disable CA1031 // Justification: no receipt is the failure the analyzer refuses.
		catch (Exception)
#pragma warning restore CA1031
		{
			_writeFailures++;
		}
	}

	private static FileStreamOptions Create()
	{
		var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.Read };
		if (!OperatingSystem.IsWindows())
			options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
		return options;
	}

	private static uint? Positive(int? value) => value is > 0 ? (uint)value.Value : null;

	/// <summary>
	/// Add this, not the record, to the pairing sidecar's logger factory: it does not own the record, so the factory's
	/// disposal cannot close it before <see cref="Summarize"/>. The client's exit (2), restart (3) or failed restart (4)
	/// of the sidecar (Sidecar/SidecarLog.cs) invalidates the record.
	/// </summary>
	public ILoggerProvider LoggerProvider { get; }

	/// <summary>After <see cref="Summarize"/>. Before it, the record says so in its last line and gets no receipt.</summary>
	public void Dispose()
	{
		lock (_gate)
		{
			if (!_summarized && _writer is not null)
			{
				_writeFailures++;
				AddLocked(MakeLocked("disposed_before_summary", out _) with { Ok = false });
			}

			_ = CloseWriterLocked();
		}
	}

	private sealed class Provider(LiveHistoryBounds owner) : ILoggerProvider
	{
		public ILogger CreateLogger(string categoryName) => new Lifecycles(owner, categoryName);

		public void Dispose()
		{
		}
	}

	private sealed class Lifecycles(LiveHistoryBounds owner, string category) : ILogger
	{
		public IDisposable? BeginScope<TState>(TState state)
			where TState : notnull => null;

		public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

		public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
		{
			if (category == "Whatsmeow.Client" && eventId.Id is 2 or 3 or 4)
				owner.Lifecycle(eventId.Id);
		}
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
		public DateTimeOffset? FirstBatch { get; init; }
		public DateTimeOffset? LastBatch { get; init; }
		public bool? AfterFinish { get; init; }
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
