using Microsoft.Extensions.Diagnostics.Metrics.Testing;
using Whatsmeow.IntegrationTests.Sessions;

namespace Whatsmeow.IntegrationTests.Sidecar;

/// <summary>
/// Calls made while the shared sidecar restarts (#85): from the next sidecar's first stderr line until the client
/// logs it ready, which spans its listen and its ready line. In the <see cref="SidecarCrashes"/> collection, which never
/// runs in parallel, so the error count, which no session id tells apart, hears no other test.
/// </summary>
[Collection(nameof(SidecarCrashes))]
public sealed class RestartTokenTests(FakeSidecar fixture)
{
	private const int Timeout = 120_000;
	private const int Callers = 8;
	private const int Crashes = 3;

	[Fact(Timeout = Timeout)]
	public async Task Calls_WhileTheNextSidecarStarts_NeverReachItWithTheLastToken()
	{
		var ct = TestContext.Current.CancellationToken;
		// A connected session, so its event streams and its ack loop retry across the restarts too.
		_ = await SessionSetup.ConnectedAsync(fixture, "restart-token", ct);
		using var errors = new MetricCollector<long>(fixture.MeterFactory, WhatsmeowTelemetry.MeterName, "whatsmeow.errors");
		var failures = new List<SidecarUnavailableException>();

		for (var i = 0; i < Crashes; i++)
		{
			var starting = Starting();
			var started = fixture.Logs.Named("SidecarStarted").Count;
			var crash = fixture.CrashAsync(ct);
			_ = await fixture.Logs.WaitForAsync(_ => Starting() > starting, ct);
			foreach (var caller in await Task.WhenAll(Enumerable.Range(0, Callers).Select(_ => Task.Run(() => CallUntilStartedAsync(started, ct), ct))))
				failures.AddRange(caller);
			await crash;
		}

		// Unreachable, never refused: a call before the next launch is ready never gets to it, nor do the acks and streams.
		Assert.All(failures, e => Assert.Equal(WhatsmeowErrorCode.Unknown, e.ErrorCode));
		Assert.DoesNotContain(errors.GetMeasurementSnapshot(), m => Equals(m.Tags["error.type"], nameof(WhatsmeowErrorCode.Unauthenticated)));
	}

	private async Task<List<SidecarUnavailableException>> CallUntilStartedAsync(int started, CancellationToken ct)
	{
		var failures = new List<SidecarUnavailableException>();
		while (fixture.Logs.Named("SidecarStarted").Count == started)
		{
			try
			{
				_ = await fixture.Sidecar.GetInfoAsync(ct).ConfigureAwait(false);
			}
			catch (SidecarUnavailableException e)
			{
				failures.Add(e);
			}
		}

		return failures;
	}

	private int Starting() => fixture.Logs.Collector.GetSnapshot().Count(r => r.Id.Name == "SidecarOutput" && r.Message.Contains("\"message\":\"starting\"", StringComparison.Ordinal));
}
