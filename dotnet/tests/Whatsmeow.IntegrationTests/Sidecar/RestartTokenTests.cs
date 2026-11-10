using Microsoft.Extensions.Diagnostics.Metrics.Testing;
using Microsoft.Extensions.Logging.Testing;
using Whatsmeow.IntegrationTests.Sessions;

namespace Whatsmeow.IntegrationTests.Sidecar;

/// <summary>
/// Calls made while the shared sidecar restarts (#85): from the crash until the client counts the next launch, which
/// spans the next sidecar's listen and its ready line. In the <see cref="SidecarCrashes"/> collection, which never
/// runs in parallel, so the error count, which no session id tells apart, hears no other test.
/// In embedded mode the next launch listens elsewhere with a token of its own (ADR 0045), and a call in the gap fails
/// unreachable, so some call must fail in each restart. In External mode the fixture itself restarts the standalone
/// sidecar inside <see cref="FakeSidecar.CrashAsync"/>, at the same port and with the same token file (ADR 0046). On
/// Windows a connect to a closed loopback port is refused after about 2 s [observed: #172], and the sidecar is back in
/// about 1 s, so a call in the gap may wait out the refusal and connect to it instead [inferred: no call failed in 6 of
/// 6 runs, #172]; there some call must start in the gap instead.
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
			var callers = Task.WhenAll(Enumerable.Range(0, Callers).Select(_ => Task.Run(() => CallUntilLaunchedAsync(launches, () => lost.IsCompletedSuccessfully, ct), ct)));
			await fixture.CrashAsync(ct);
			var calls = await callers;
			var crashFailures = calls.Sum(c => c.Failures.Count);
			var inGap = calls.Sum(c => c.InGap);

			// Not vacuous: calls ran while no sidecar was ready.
			var vacuous = FakeSidecar.IsExternal ? inGap == 0 : crashFailures == 0;
			Assert.False(vacuous, $"Sidecar {i + 1} restarted with {inGap} calls started after the client saw it go and {crashFailures} failed.");
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
	/// started once <paramref name="lost"/> held: it is read before the launch count, so such a call started after the
	/// client saw the sidecar go and before it counted the next launch.
	/// </summary>
	private async Task<(List<SidecarUnavailableException> Failures, int InGap)> CallUntilLaunchedAsync(int launches, Func<bool> lost, CancellationToken ct)
	{
		var failures = new List<SidecarUnavailableException>();
		var inGap = 0;
		while (true)
		{
			var afterLost = lost();
			if (fixture.Sidecar.Launches != launches)
				break;
			if (afterLost)
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
