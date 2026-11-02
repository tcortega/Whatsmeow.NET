using Microsoft.Extensions.Diagnostics.Metrics.Testing;

namespace Whatsmeow.IntegrationTests.Support;

/// <summary>Reads a <see cref="MetricCollector{T}"/> by condition, for an instrument other tests on the shared sidecar record too.</summary>
internal static class Measured
{
	/// <summary>The measurements tagged with <paramref name="session"/> so far.</summary>
	public static IReadOnlyList<CollectedMeasurement<T>> Of<T>(MetricCollector<T> collector, string session)
		where T : struct =>
		[.. collector.GetMeasurementSnapshot().Where(measurement => Equals(measurement.Tags.GetValueOrDefault("whatsmeow.session.id"), session))];

	/// <summary>
	/// Waits until <paramref name="session"/>'s measurements satisfy <paramref name="done"/>, and returns them; the test's
	/// timeout ends the wait. Each turn waits for one more measurement than the snapshot it checked, so none is missed.
	/// </summary>
	public static async Task<IReadOnlyList<CollectedMeasurement<T>>> WaitForAsync<T>(
		MetricCollector<T> collector,
		string session,
		Func<IReadOnlyList<CollectedMeasurement<T>>, bool> done,
		CancellationToken cancellationToken
	)
		where T : struct
	{
		while (true)
		{
			var all = collector.GetMeasurementSnapshot();
			IReadOnlyList<CollectedMeasurement<T>> mine = [.. all.Where(measurement => Equals(measurement.Tags.GetValueOrDefault("whatsmeow.session.id"), session))];
			if (done(mine))
				return mine;
			await collector.WaitForMeasurementsAsync(all.Count + 1, cancellationToken).ConfigureAwait(false);
		}
	}
}
