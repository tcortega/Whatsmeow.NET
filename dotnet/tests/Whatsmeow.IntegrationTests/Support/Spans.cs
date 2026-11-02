using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading.Channels;

namespace Whatsmeow.IntegrationTests.Support;

/// <summary>
/// Records every span that stopped while it is alive, of the sources it listens to: by default the library's
/// <see cref="ActivitySource"/> and the tests' own <see cref="Test"/>. A listener is process-wide, so only the tests of
/// the <see cref="TelemetryListeners"/> collection use one: none runs beside them.
/// </summary>
internal sealed class Spans : IDisposable
{
	private const string TestSourceName = "Whatsmeow.IntegrationTests";

	private readonly ConcurrentQueue<Activity> _stopped = new();
	private readonly Channel<Activity> _arrived = Channel.CreateUnbounded<Activity>();
	private readonly ActivityListener _listener;

	/// <summary>The library's spans and the tests' own.</summary>
	public Spans()
		: this(static source => source.Name is WhatsmeowTelemetry.ActivitySourceName or TestSourceName) { }

	// By name: reading Test here would create that source inside the listener's own registration, which could miss it.
	private Spans(Func<ActivitySource, bool> listensTo)
	{
		_listener = new ActivityListener
		{
			ShouldListenTo = listensTo,
			Sample = static (ref _) => ActivitySamplingResult.AllDataAndRecorded,
			ActivityStopped = span =>
			{
				_stopped.Enqueue(span);
				_ = _arrived.Writer.TryWrite(span);
			},
		};
		ActivitySource.AddActivityListener(_listener);
	}

	/// <summary>The tests' own spans only, as an application that does not listen to the library.</summary>
	public static Spans OfTheTests() => new(static source => source.Name is TestSourceName);

	/// <summary>Every source's spans but the library's, as an application that listens to everything else.</summary>
	public static Spans OfAllButTheLibrary() => new(static source => source.Name is not WhatsmeowTelemetry.ActivitySourceName);

	/// <summary>The tests' own spans: an application's around a library call, or a handler's own work.</summary>
	public static ActivitySource Test { get; } = new(TestSourceName);

	/// <summary>Every span that stopped so far, in order.</summary>
	public IReadOnlyList<Activity> All => [.. _stopped];

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

/// <summary>The tests that listen to spans, or count what no session id tells apart: never in parallel with another test.</summary>
[CollectionDefinition(nameof(TelemetryListeners), DisableParallelization = true)]
public sealed class TelemetryListeners;
