using Microsoft.Extensions.Logging.Testing;
using Microsoft.Extensions.Options;

namespace Whatsmeow.IntegrationTests.Support;

/// <summary>Captures every log record of one sidecar, and waits for the ones a test needs.</summary>
public sealed class LogCapture : IDisposable
{
	private readonly FakeLoggerProvider _provider;
	private readonly StreamWriter? _file;
	private readonly Lock _fileLock = new();

	/// <param name="sink">
	/// Also gets each record as a line, as it comes, with its exception's type and message after it; the live tests write
	/// their log file with it. The client's own exceptions name a field, never its value (ARCHITECTURE §8.7).
	/// </param>
	public LogCapture(Action<string>? sink = null)
	{
		Collector = new FakeLogCollector(Options.Create(new FakeLogCollectorOptions { OutputSink = sink, OutputFormatter = Line }));
		_provider = new FakeLoggerProvider(Collector);
		Factory = LoggerFactory.Create(builder => builder.AddProvider(_provider).SetMinimumLevel(LogLevel.Trace));
	}

	/// <summary>
	/// Captures in memory, and also writes the client's and the sidecar's records to <paramref name="path"/> as they come,
	/// so a run that fails or hangs leaves them behind. Grpc's own records stay out, and an exception is written as its
	/// message: the file stays small enough to upload.
	/// </summary>
	public LogCapture(string path)
	{
		_ = Directory.CreateDirectory(Path.GetDirectoryName(path)!);
		_file = new StreamWriter(path, append: false) { AutoFlush = true };
		Collector = new FakeLogCollector(Options.Create(new FakeLogCollectorOptions { OutputFormatter = Format, OutputSink = Write }));
		_provider = new FakeLoggerProvider(Collector);
		Factory = LoggerFactory.Create(builder => builder.AddProvider(_provider).SetMinimumLevel(LogLevel.Trace));
	}

	public FakeLogCollector Collector { get; }

	/// <summary>A record as the collector writes it by default, then its exception, which the default leaves out.</summary>
	private static string Line(FakeLogRecord record) =>
		$"[{record.Timestamp:mm:ss.fff}, {Level(record.Level)}] {record.Message}{(record.Exception is { } e ? $" ({e.GetType().Name}: {e.Message})" : "")}";

	private static string Level(LogLevel level) =>
		level switch
		{
			LogLevel.Trace => "trace",
			LogLevel.Debug => "debug",
			LogLevel.Information => " info",
			LogLevel.Warning => " warn",
			LogLevel.Error => "error",
			LogLevel.Critical => " crit",
			LogLevel.None => " none",
			_ => " none",
		};

	public ILoggerFactory Factory { get; }

	public void Dispose()
	{
		Factory.Dispose();
		_provider.Dispose();
		lock (_fileLock)
			_file?.Dispose();
	}

	/// <summary>Writes a line of the test's own to the file, if there is one, between the records around it.</summary>
	public void Note(string line) => Write($"---- {line}");

	private static string Format(FakeLogRecord record) =>
		record.Category?.StartsWith("Whatsmeow.", StringComparison.Ordinal) == true
			? $"{record.Timestamp:HH:mm:ss.fff} {record.Level} {record.Id.Name} {record.Message} {record.Exception?.Message}"
			: "";

	private void Write(string line)
	{
		if (line.Length == 0)
			return;

		lock (_fileLock)
			_file?.WriteLine(line);
	}

	/// <summary>Waits until a record matches; the test's timeout ends the wait.</summary>
	public Task<FakeLogRecord> WaitForAsync(Func<FakeLogRecord, bool> predicate, CancellationToken cancellationToken) => WaitForAsync(predicate, 1, cancellationToken);

	/// <summary>Waits for the <paramref name="count"/>th matching record, including the ones already collected.</summary>
	public async Task<FakeLogRecord> WaitForAsync(Func<FakeLogRecord, bool> predicate, int count, CancellationToken cancellationToken)
	{
		ArgumentOutOfRangeException.ThrowIfLessThan(count, 1);
		await foreach (var record in Collector.GetLogsAsync(cancellationToken).ConfigureAwait(false))
		{
			cancellationToken.ThrowIfCancellationRequested();
			if (predicate(record) && --count == 0)
				return record;
		}

		throw new InvalidOperationException("The log collector ended before the expected record.");
	}

	/// <summary>Waits for the <paramref name="count"/>th SidecarStarted record, which carries the process id and the endpoint.</summary>
	public async Task<(int ProcessId, string Endpoint)> StartedAsync(int count, CancellationToken cancellationToken)
	{
		var record = await WaitForAsync(r => r.Id.Name == "SidecarStarted", count, cancellationToken).ConfigureAwait(false);
		return (int.Parse(record.GetStructuredStateValue("ProcessId")!, System.Globalization.CultureInfo.InvariantCulture), record.GetStructuredStateValue("Endpoint")!);
	}

	/// <summary>Every record with this event name.</summary>
	public IReadOnlyList<FakeLogRecord> Named(string eventName) => [.. Collector.GetSnapshot().Where(r => r.Id.Name == eventName)];
}
