using Whatsmeow.Events;
using Whatsmeow.IntegrationTests.Fake;
using Whatsmeow.IntegrationTests.Sessions;
using Whatsmeow.IntegrationTests.Support;
using Whatsmeow.Sessions;

namespace Whatsmeow.IntegrationTests.Events;

/// <summary>
/// A message whose answer the sidecar withheld comes again within a bound, without the application reconnecting: the
/// sidecar resets the connection, and WhatsApp delivers it again at the login (ADR 0044, #91).
/// </summary>
public sealed class WithheldRedeliveryTests(FakeSidecar fixture)
{
	private const int Timeout = 120_000;

	/// <summary>
	/// The settle delay before the reset, 5 s, plus the fake's login, with room for a loaded runner; far below the next
	/// login the application would otherwise wait for.
	/// </summary>
	private static readonly TimeSpan s_bound = TimeSpan.FromSeconds(30);

#pragma warning disable RS0030 // Justification: the sidecar waits its settle delay on its own clock, so the test bounds the redelivery with the real clock, not a fake one
	private static readonly TimeProvider s_wallClock = TimeProvider.System;
#pragma warning restore RS0030

	[Fact(Timeout = Timeout)]
	public async Task FullCycle_AWithheldAnswer_TheMessageReachesTheHandlersAgainWithoutTheApplicationReconnecting()
	{
		var ct = TestContext.Current.CancellationToken;
		const string Session = "events-withheld";
		var client = await SessionSetup.ConnectedAsync(fixture, Session, ct);
		using var received = new Received<MessageReceived>(client);
		await using var watch = StateWatch.Start(client, ct);
		Assert.Equal(new SessionState.Connected(), await watch.NextAsync());

		var withheld = Deliveries.Message(Session, "M1");
		withheld.WithholdFirstAnswer = true;
		var start = s_wallClock.GetTimestamp();
		_ = await fixture.Fake.DeliverMessageAsync(withheld, cancellationToken: ct);
		await Deliveries.DeliverAsync(fixture.Fake, Session, ct, "M2");

		// M2 was recorded on the same connection; M1 only once the sidecar reset it and WhatsApp delivered it again.
		Assert.Equal(Deliveries.Received(Session, "M2"), await received.NextAsync(ct));
		await watch.ReachesAsync(new SessionState.Reconnecting()).WaitAsync(s_bound, s_wallClock, ct);
		await watch.ReachesAsync(new SessionState.Connected());
		Assert.Equal(Deliveries.Received(Session, "M1"), await received.NextAsync(ct).WaitAsync(s_bound, s_wallClock, ct));
		Assert.True(s_wallClock.GetElapsedTime(start) < s_bound, $"M1 came again after {s_wallClock.GetElapsedTime(start)}");

		// Answered now: the fake's log has M2's receipt, the reset's close, then M1's receipt.
		var receipts = new List<string>();
		for (ulong after = 0; receipts.Count < 2; after++)
		{
			var sent = (await fixture.Fake.WaitForSentAsync(new WaitForSentRequest { SessionId = Session, After = after }, cancellationToken: ct)).Sent;
			if (sent.Receipt is not null)
				receipts.Add(sent.Receipt.Id);
		}

		Assert.Equal(["M2", "M1"], receipts);
	}
}
