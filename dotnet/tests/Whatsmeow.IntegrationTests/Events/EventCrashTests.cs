using Whatsmeow.Events;
using Whatsmeow.IntegrationTests.Sessions;
using Whatsmeow.IntegrationTests.Support;

namespace Whatsmeow.IntegrationTests.Events;

/// <summary>The event stream across a crash of the shared sidecar (ARCHITECTURE §8.5, invariants 2 and 3).</summary>
[Collection(nameof(SidecarCrashes))]
public sealed class EventCrashTests(FakeSidecar fixture)
{
	private const int Timeout = 120_000;

	[Fact(Timeout = Timeout)]
	public async Task Stream_TheSidecarIsKilledMidStream_EveryEventReachesTheHandlersOnce()
	{
		var ct = TestContext.Current.CancellationToken;
		var client = await SessionSetup.LinkedAsync(fixture, "events-crash", ct);
		var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		using var received = new Received<MessageReceived>(
			client,
			async (e, token) =>
			{
				if (e.Id != MessageId.From("M3"))
					return;
				started.SetResult();
				await release.Task.WaitAsync(token).ConfigureAwait(false);
			}
		);
		await client.ConnectAsync(ct);

		// Durable before the kill: each delivery returns once the sidecar's handler committed it.
		await Deliveries.DeliverAsync(fixture.Fake, "events-crash", ct, "M1", "M2", "M3", "M4", "M5");
		await started.Task.WaitAsync(ct);

		await fixture.CrashAsync(ct);
		release.SetResult();
		await Deliveries.DeliverAsync(fixture.Fake, "events-crash", ct, "M6");

		// This process kept what it received, and the stream goes on after it: nothing lost, nothing repeated.
		Assert.Equal(
			Enumerable.Range(1, 6).Select(i => Deliveries.Received("events-crash", $"M{i}")),
			await received.NextAsync(6, ct)
		);
	}
}
