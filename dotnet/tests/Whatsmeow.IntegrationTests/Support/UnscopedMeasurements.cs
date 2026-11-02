using System.Collections.Concurrent;
using System.Diagnostics.Metrics;

namespace Whatsmeow.IntegrationTests.Support;

/// <summary>
/// Records one instrument of every meter named <see cref="WhatsmeowTelemetry.MeterName"/> that no factory made: one per
/// sidecar started without an <see cref="IMeterFactory"/>. A <c>MetricCollector</c> would follow only the first such
/// instrument, which may be another test's sidecar's.
/// </summary>
internal sealed class UnscopedMeasurements : IDisposable
{
	private readonly ConcurrentQueue<(long Value, Dictionary<string, object?> Tags)> _measurements = new();
	private readonly MeterListener _listener = new();

	public UnscopedMeasurements(string instrument)
	{
		_listener.InstrumentPublished = (published, listener) =>
		{
			if (published is { Meter: { Name: WhatsmeowTelemetry.MeterName, Scope: null } } && published.Name == instrument)
				listener.EnableMeasurementEvents(published);
		};
		_listener.SetMeasurementEventCallback<long>((_, value, tags, _) => _measurements.Enqueue((value, Tags.Of(tags.ToArray()))));
		_listener.Start();
	}

	/// <summary>The measurements tagged with <paramref name="session"/> so far.</summary>
	public IReadOnlyList<(long Value, Dictionary<string, object?> Tags)> Of(string session) =>
		[.. _measurements.Where(measurement => Equals(measurement.Tags.GetValueOrDefault("whatsmeow.session.id"), session))];

	public void Dispose() => _listener.Dispose();
}
