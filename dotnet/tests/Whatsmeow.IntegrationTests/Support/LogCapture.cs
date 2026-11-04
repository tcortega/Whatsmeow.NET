using Microsoft.Extensions.Logging.Testing;
using Microsoft.Extensions.Options;

namespace Whatsmeow.IntegrationTests.Support;

/// <summary>Captures every log record of one sidecar, and waits for the ones a test needs.</summary>
public sealed class LogCapture : IDisposable
{
	private readonly FakeLoggerProvider _provider;
	private readonly StreamWriter? _file;
	private readonly Lock _fileLock = new();

	/// <param name="sink">Also gets each record as a line, as it comes; the live tests write their log file with it.</param>
	public LogCapture(Action<string>? sink = null)
	{
		Collector = new FakeLogCollector(Options.Create(new FakeLogCollectorOptions { OutputSink = sink }));
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
	public async Task<FakeLogRecord> WaitForAsync(Func<FakeLogRecord, bool> predicate, CancellationToken cancellationToken)
	{
		var records = Collector.GetLogsAsync(cancellationToken).GetAsyncEnumerator(cancellationToken);
		await using var disposeRecords = records.ConfigureAwait(false);
		while (true)
		{
			if (Collector.GetSnapshot().FirstOrDefault(predicate) is { } match)
				return match;
			_ = await records.MoveNextAsync().ConfigureAwait(false);
		}
	}

	/// <summary>Waits for the <paramref name="count"/>th SidecarStarted record, which carries the process id and the endpoint.</summary>
	public async Task<(int ProcessId, string Endpoint)> StartedAsync(int count, CancellationToken cancellationToken)
	{
		_ = await WaitForAsync(_ => Started().Count >= count, cancellationToken).ConfigureAwait(false);
		var record = Started()[count - 1];
		return (int.Parse(record.GetStructuredStateValue("ProcessId")!, System.Globalization.CultureInfo.InvariantCulture), record.GetStructuredStateValue("Endpoint")!);
	}

	/// <summary>Every record with this event name.</summary>
	public IReadOnlyList<FakeLogRecord> Named(string eventName) => [.. Collector.GetSnapshot().Where(r => r.Id.Name == eventName)];

	private IReadOnlyList<FakeLogRecord> Started() => Named("SidecarStarted");
}
