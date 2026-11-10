using Microsoft.Extensions.Diagnostics.Metrics.Testing;
using Microsoft.Extensions.Logging.Testing;
using Whatsmeow.IntegrationTests.Sessions;

namespace Whatsmeow.IntegrationTests.Sidecar;

/// <summary>
/// Calls made while the shared sidecar restarts (#85): from the crash until the client counts the next launch, which
/// spans the next sidecar's listen and its ready line. In the <see cref="SidecarCrashes"/> collection, which never
/// runs in parallel, so the error count, which no session id tells apart, hears no other test.
/// A call in that gap fails unreachable where a refused connect is instant. On Windows a connect to a closed loopback
/// port is refused after about 2 s [observed: #172], so in External mode, whose next sidecar listens at the same port
/// after about 1 s, such a call may connect to it instead, with the same token from the same file (ADR 0046); in
/// embedded mode the next launch listens elsewhere with a token of its own (ADR 0045).
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
			var lost = fixture.Logs.WaitForAsync(IsLost, fixture.Logs.Collector.GetSnapshot().Count(IsLost) + 1, ct);
			var callers = Task.WhenAll(Enumerable.Range(0, Callers).Select(_ => Task.Run(() => CallUntilLaunchedAsync(launches, () => lost.IsCompleted, ct), ct)));
			await fixture.CrashAsync(ct);
			var calls = await callers;

			// Not vacuous: calls started while no sidecar was ready, after the client saw this one go.
			Assert.True(calls.Sum(c => c.InGap) > 0, $"No call started while sidecar {i + 1} restarted.");
			failures.AddRange(calls.SelectMany(c => c.Failures));
		}

		// Unreachable, never refused: a call before the next launch is ready never gets to it, nor do the acks and streams.
		Assert.All(failures, e => Assert.Equal(WhatsmeowErrorCode.Unknown, e.ErrorCode));
		Assert.DoesNotContain(errors.GetMeasurementSnapshot(), m => Equals(m.Tags["error.type"], nameof(WhatsmeowErrorCode.Unauthenticated)));
	}

	/// <summary>The supervisor's record that the sidecar went, in either mode.</summary>
	private static bool IsLost(FakeLogRecord record) => record.Id.Name is "SidecarExited" or "ExternalSidecarLost";

	/// <summary>
	/// Calls until the next launch is counted, with a pause between calls, and returns the failures and how many calls
	/// started once <paramref name="lost"/> held.
	/// </summary>
	private async Task<(List<SidecarUnavailableException> Failures, int InGap)> CallUntilLaunchedAsync(int launches, Func<bool> lost, CancellationToken ct)
	{
		var failures = new List<SidecarUnavailableException>();
		var inGap = 0;
		while (fixture.Sidecar.Launches == launches)
		{
			if (lost())
				inGap++;
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

		return (failures, inGap);
	}
}
