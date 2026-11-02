using System.Collections.Concurrent;
using System.Diagnostics;

namespace Whatsmeow.IntegrationTests.Support;

/// <summary>
/// Records every span of the library's <see cref="ActivitySource"/> that stopped while it is alive. A listener is
/// process-wide, so only the tests of the <see cref="TelemetryListeners"/> collection use one: none runs beside them.
/// </summary>
internal sealed class Spans : IDisposable
{
	private readonly ConcurrentQueue<Activity> _stopped = new();
	private readonly ActivityListener _listener;

	public Spans()
	{
		_listener = new ActivityListener
		{
			ShouldListenTo = static source => source.Name == WhatsmeowTelemetry.ActivitySourceName,
			Sample = static (ref _) => ActivitySamplingResult.AllDataAndRecorded,
			ActivityStopped = _stopped.Enqueue,
		};
		ActivitySource.AddActivityListener(_listener);
	}

	/// <summary>The spans named <paramref name="name"/> of <paramref name="session"/> that stopped so far, in order.</summary>
	public IReadOnlyList<Activity> Of(string name, string session) =>
		[.. _stopped.Where(span => span.OperationName == name && Equals(span.GetTagItem("whatsmeow.session.id"), session))];

	public void Dispose() => _listener.Dispose();
}

/// <summary>The tests that listen to the library's spans, or count what no session id tells apart: never in parallel with another test.</summary>
[CollectionDefinition(nameof(TelemetryListeners), DisableParallelization = true)]
public sealed class TelemetryListeners;
