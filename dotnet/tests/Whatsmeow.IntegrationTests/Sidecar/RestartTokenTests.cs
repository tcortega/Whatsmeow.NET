using Microsoft.Extensions.Diagnostics.Metrics.Testing;
using Whatsmeow.IntegrationTests.Sessions;

namespace Whatsmeow.IntegrationTests.Sidecar;

/// <summary>
/// Calls made while the shared sidecar restarts (#85): from the crash until the client counts the next launch, which
/// spans the next sidecar's listen and its ready line. In the <see cref="SidecarCrashes"/> collection, which never
/// runs in parallel, so the error count, which no session id tells apart, hears no other test.
/// </summary>
[Collection(nameof(SidecarCrashes))]
public sealed class RestartTokenTests(FakeSidecar fixture)
{
	private const int Timeout = 120_000;
	private const int Callers = 8;
	private const int Crashes = 3;

	// A pause between a caller's calls, so the callers never starve the next sidecar's start.
	private static readonly TimeSpan s_pause = TimeSpan.FromMilliseconds(1);

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
			var launches = fixture.Sidecar.Launches;
			var callers = Task.WhenAll(Enumerable.Range(0, Callers).Select(_ => Task.Run(() => CallUntilLaunchedAsync(launches, ct), ct)));
			await fixture.CrashAsync(ct);
			var crashFailures = (await callers).SelectMany(f => f).ToList();

			// Not vacuous: calls ran while no sidecar was ready.
			Assert.True(crashFailures.Count > 0, $"No call failed while sidecar {i + 1} restarted.");
			failures.AddRange(crashFailures);
		}

		// Unreachable, never refused: a call before the next launch is ready never gets to it, nor do the acks and streams.
		Assert.All(failures, e => Assert.Equal(WhatsmeowErrorCode.Unknown, e.ErrorCode));
		Assert.DoesNotContain(errors.GetMeasurementSnapshot(), m => Equals(m.Tags["error.type"], nameof(WhatsmeowErrorCode.Unauthenticated)));
	}

	/// <summary>Calls until the next launch is counted, with a pause between calls, and returns the failures.</summary>
	private async Task<List<SidecarUnavailableException>> CallUntilLaunchedAsync(int launches, CancellationToken ct)
	{
		var failures = new List<SidecarUnavailableException>();
		while (fixture.Sidecar.Launches == launches)
		{
			try
			{
				_ = await fixture.Sidecar.GetInfoAsync(ct).ConfigureAwait(false);
			}
			catch (SidecarUnavailableException e)
			{
				failures.Add(e);
			}

			await Task.Delay(s_pause, fixture.Sidecar.TimeProvider, ct).ConfigureAwait(false);
		}

		return failures;
	}
}
