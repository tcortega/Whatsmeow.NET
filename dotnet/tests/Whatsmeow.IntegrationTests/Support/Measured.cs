using Microsoft.Extensions.Diagnostics.Metrics.Testing;

namespace Whatsmeow.IntegrationTests.Support;

/// <summary>Reads a <see cref="MetricCollector{T}"/> by condition, for an instrument other tests on the shared sidecar record too.</summary>
internal static class Measured
{
	/// <summary>The measurements tagged with <paramref name="session"/> so far.</summary>
	public static IReadOnlyList<CollectedMeasurement<T>> Of<T>(MetricCollector<T> collector, string session)
		where T : struct =>
		[.. collector.GetMeasurementSnapshot().Where(measurement => Equals(measurement.Tags.GetValueOrDefault("whatsmeow.session.id"), session))];
}
