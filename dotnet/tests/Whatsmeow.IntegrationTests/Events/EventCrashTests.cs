using Whatsmeow.Events;
using Whatsmeow.Groups;
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
		var all = await received.NextAsync(6, ct);
		Assert.Equal(Enumerable.Range(1, 6).Select(i => Deliveries.Received("events-crash", $"M{i}")), all);
		DeliveryChecks.ValidateEachOnce(Deliveries.Sent("M1", "M2", "M3", "M4", "M5", "M6"), all, SessionId.From("events-crash"));
	}

	[Fact(Timeout = Timeout)]
	public async Task GroupEvents_TheSidecarIsKilledMidStream_EveryEventReachesTheHandlersOnce()
	{
		var ct = TestContext.Current.CancellationToken;
		var client = await SessionSetup.LinkedAsync(fixture, "events-crash-groups", ct);
		var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		using var received = new Received<GroupInfoChanged>(
			client,
			async (e, token) =>
			{
				if (e.Changes is not [GroupChange.Renamed { Name: "N3" }])
					return;
				started.SetResult();
				await release.Task.WaitAsync(token).ConfigureAwait(false);
			}
		);
		await client.ConnectAsync(ct);

		// Durable before the kill: each delivery returns once the sidecar's handler committed it, and the fake records the
		// notification's ack only after that (GroupTests.GroupNotification_ByAnyoneForAGroupNeverListed_... reads the acks).
		await Deliveries.DeliverGroupAsync(fixture.Fake, "events-crash-groups", ct, "N1", "N2", "N3", "N4", "N5");
		await started.Task.WaitAsync(ct);

		await fixture.CrashAsync(ct);
		release.SetResult();
		await Deliveries.DeliverGroupAsync(fixture.Fake, "events-crash-groups", ct, "N6");

		// This process kept what it received, and the stream goes on after it: nothing lost, nothing repeated.
		Assert.Equal(
			Enumerable.Range(1, 6).Select(i => Deliveries.Renaming("events-crash-groups", $"N{i}")),
			await received.NextAsync(6, ct)
		);
	}
}
