using Microsoft.Extensions.Logging.Testing;
using Microsoft.Extensions.Options;

namespace Whatsmeow.IntegrationTests.Support;

/// <summary>Captures every log record of one sidecar, and waits for the ones a test needs.</summary>
public sealed class LogCapture : IDisposable
{
	private readonly FakeLoggerProvider _provider;

	/// <param name="sink">Also gets each record as a line, as it comes; the live tests write their log file with it.</param>
	public LogCapture(Action<string>? sink = null)
	{
		Collector = new FakeLogCollector(Options.Create(new FakeLogCollectorOptions { OutputSink = sink }));
		_provider = new FakeLoggerProvider(Collector);
		Factory = LoggerFactory.Create(builder => builder.AddProvider(_provider).SetMinimumLevel(LogLevel.Trace));
	}

	public FakeLogCollector Collector { get; }

	public ILoggerFactory Factory { get; }

	public void Dispose()
	{
		Factory.Dispose();
		_provider.Dispose();
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
