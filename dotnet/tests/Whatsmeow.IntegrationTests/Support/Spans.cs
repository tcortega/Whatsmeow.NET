using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading.Channels;

namespace Whatsmeow.IntegrationTests.Support;

/// <summary>
/// Records every span of the library's <see cref="ActivitySource"/>, and of the tests' own <see cref="Test"/>, that
/// stopped while it is alive. A listener is process-wide, so only the tests of the <see cref="TelemetryListeners"/>
/// collection use one: none runs beside them.
/// </summary>
internal sealed class Spans : IDisposable
{
	private readonly ConcurrentQueue<Activity> _stopped = new();
	private readonly Channel<Activity> _arrived = Channel.CreateUnbounded<Activity>();
	private readonly ActivityListener _listener;

	public Spans()
	{
		_listener = new ActivityListener
		{
			// By name: reading Test here would create that source inside the listener's own registration, which could miss it.
			ShouldListenTo = static source => source.Name is WhatsmeowTelemetry.ActivitySourceName or TestSourceName,
			Sample = static (ref _) => ActivitySamplingResult.AllDataAndRecorded,
			ActivityStopped = span =>
			{
				_stopped.Enqueue(span);
				_ = _arrived.Writer.TryWrite(span);
			},
		};
		ActivitySource.AddActivityListener(_listener);
	}

	private const string TestSourceName = "Whatsmeow.IntegrationTests";

	/// <summary>The tests' own spans: an application's around a library call, or a handler's own work.</summary>
	public static ActivitySource Test { get; } = new(TestSourceName);

	/// <summary>The spans named <paramref name="name"/> of <paramref name="session"/> that stopped so far, in order.</summary>
	public IReadOnlyList<Activity> Of(string name, string session) =>
		[.. _stopped.Where(span => span.OperationName == name && Equals(span.GetTagItem("whatsmeow.session.id"), session))];

	/// <summary>Waits until a stopped span matches; the test's timeout ends the wait.</summary>
	public async Task<Activity> WaitForAsync(Func<Activity, bool> match, CancellationToken cancellationToken)
	{
		while (true)
		{
			if (_stopped.FirstOrDefault(match) is { } found)
				return found;
			_ = await _arrived.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);
		}
	}

	public void Dispose() => _listener.Dispose();
}

/// <summary>The tests that listen to the library's spans, or count what no session id tells apart: never in parallel with another test.</summary>
[CollectionDefinition(nameof(TelemetryListeners), DisableParallelization = true)]
public sealed class TelemetryListeners;
