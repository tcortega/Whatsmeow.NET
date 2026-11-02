using Microsoft.Extensions.Diagnostics.Metrics.Testing;

namespace Whatsmeow.IntegrationTests.Telemetry;

/// <summary>The restart count, across a crash of the shared sidecar; in the <see cref="SidecarCrashes"/> collection.</summary>
[Collection(nameof(SidecarCrashes))]
public sealed class TelemetryCrashTests(FakeSidecar fixture)
{
	private const int Timeout = 120_000;

	[Fact(Timeout = Timeout)]
	public async Task SidecarRestart_IsCounted()
	{
		var ct = TestContext.Current.CancellationToken;
		using var restarts = new MetricCollector<long>(fixture.MeterFactory, WhatsmeowTelemetry.MeterName, "whatsmeow.sidecar.restarts");

		await fixture.CrashAsync(ct);

		// CrashAsync returns once the restart was logged, after it was counted.
		var measurement = Assert.Single(restarts.GetMeasurementSnapshot());
		Assert.Equal((1L, 0), (measurement.Value, measurement.Tags.Count));
	}
}
