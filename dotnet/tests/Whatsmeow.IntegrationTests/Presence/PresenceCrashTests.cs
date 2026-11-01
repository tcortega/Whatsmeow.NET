using Whatsmeow.Events;
using Whatsmeow.IntegrationTests.Events;
using Whatsmeow.IntegrationTests.Sessions;
using Whatsmeow.IntegrationTests.Support;

namespace Whatsmeow.IntegrationTests.Presence;

/// <summary>Ephemeral events across a crash of the shared sidecar (ARCHITECTURE §8.5).</summary>
[Collection(nameof(SidecarCrashes))]
public sealed class PresenceCrashTests(FakeSidecar fixture)
{
	private const int Timeout = 120_000;

	[Fact(Timeout = Timeout)]
	public async Task Restart_EphemeralEventsFromBeforeAreGone_DurableOnesResumeAndNewOnesFlow()
	{
		var ct = TestContext.Current.CancellationToken;
		const string Session = "presence-crash";
		var client = await SessionSetup.LinkedAsync(fixture, Session, ct);
		using var presences = new Received<PresenceChanged>(client);
		using var messages = new Received<MessageReceived>(client);

		// Not connected in this process yet: the presence waits in the sidecar's memory, the message in its outbox.
		await Presences.DeliverAsync(fixture.Fake, ct, Presences.Of(Session, 1));
		await Deliveries.DeliverAsync(fixture.Fake, Session, ct, "M1");
		await fixture.CrashAsync(ct);

		await client.ConnectAsync(ct);
		await Presences.DeliverAsync(fixture.Fake, ct, Presences.Of(Session, 2));
		await Deliveries.DeliverAsync(fixture.Fake, Session, ct, "M2");

		Assert.Equal([Deliveries.Received(Session, "M1"), Deliveries.Received(Session, "M2")], await messages.NextAsync(2, ct));
		Assert.Equal(Presences.Changed(Session, 2), await presences.NextAsync(ct));

		// Connected: the ephemeral stream opens again on the restarted sidecar.
		await fixture.CrashAsync(ct);
		await Presences.DeliverAsync(fixture.Fake, ct, Presences.Of(Session, 3));

		Assert.Equal(Presences.Changed(Session, 3), await presences.NextAsync(ct));
	}
}
