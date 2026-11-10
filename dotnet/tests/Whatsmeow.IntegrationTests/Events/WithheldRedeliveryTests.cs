using Google.Protobuf.WellKnownTypes;
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
		var client = await SessionSetup.LinkedAsync(fixture, Session, ct);
		using var received = new Received<MessageReceived>(client);
		// From the outbox, so every state comes, in order; the state watch keeps only the latest (IWhatsAppClient).
		using var states = new Received<SessionStateChanged>(client);
		await client.ConnectAsync(ct);
		for (var state = (await states.NextAsync(ct)).State; state != new SessionState.Connected(); state = (await states.NextAsync(ct)).State)
			Assert.True(state is SessionState.Disconnected or SessionState.Connecting, $"state {state} before Connected");

		var withheld = Deliveries.Message(Session, "M1");
		withheld.WithholdFirstAnswer = true;
		var start = s_wallClock.GetTimestamp();
		_ = await fixture.Fake.DeliverMessageAsync(withheld, cancellationToken: ct);
		await Deliveries.DeliverAsync(fixture.Fake, Session, ct, "M2");

		// M2 was recorded on the same connection; M1 only once the sidecar reset it and WhatsApp delivered it again.
		Assert.Equal(Deliveries.Received(Session, "M2"), await received.NextAsync(ct));
		Assert.Equal(new SessionState.Reconnecting(), (await states.NextAsync(ct).WaitAsync(s_bound, s_wallClock, ct)).State);
		Assert.Equal(new SessionState.Connected(), (await states.NextAsync(ct)).State);
		Assert.Equal(Deliveries.Received(Session, "M1"), await received.NextAsync(ct).WaitAsync(s_bound, s_wallClock, ct));
		Assert.True(s_wallClock.GetElapsedTime(start) < s_bound, $"M1 came again after {s_wallClock.GetElapsedTime(start)}");

		// Answered now: the fake's log has M2's receipt, the reset's close, then M1's receipt.
		var receipts = new List<string>();
		ulong after = 0;
		while (receipts.Count < 2)
		{
			var sent = (await fixture.Fake.WaitForSentAsync(new WaitForSentRequest { SessionId = Session, After = after }, cancellationToken: ct)).Sent;
			if (sent.Receipt is not null)
				receipts.Add(sent.Receipt.Id);
			after = sent.Index;
		}

		Assert.Equal(["M2", "M1"], receipts);
	}

	/// <summary>
	/// whatsmeow acks an undecryptable message only after the sidecar recorded it (ADR 0066), so a failed recording
	/// withholds its answer like a message's, and the sidecar's reset brings it again. That WhatsApp delivers it again
	/// is the fake's model, [inferred] for real WhatsApp (ADR 0066).
	/// </summary>
	[Fact(Timeout = Timeout)]
	public async Task FullCycle_AWithheldUndecryptableMessage_IsRecordedOnceAfterTheReset()
	{
		var ct = TestContext.Current.CancellationToken;
		const string Session = "events-withheld-undecryptable";
		var client = await SessionSetup.LinkedAsync(fixture, Session, ct);
		using var undecryptable = new Received<UndecryptableMessageReceived>(client);
		using var received = new Received<MessageReceived>(client);
		using var states = new Received<SessionStateChanged>(client);
		await client.ConnectAsync(ct);
		for (var state = (await states.NextAsync(ct)).State; state != new SessionState.Connected(); state = (await states.NextAsync(ct)).State)
			Assert.True(state is SessionState.Disconnected or SessionState.Connecting, $"state {state} before Connected");

		var start = s_wallClock.GetTimestamp();
		_ = await fixture.Fake.DeliverUndecryptableMessageAsync(Undecryptable(Session, "U1", withhold: true), cancellationToken: ct);
		await Deliveries.DeliverAsync(fixture.Fake, Session, ct, "M2");

		// M2 was recorded on the same connection; U1 only once the sidecar reset it and WhatsApp delivered it again.
		Assert.Equal(Deliveries.Received(Session, "M2"), await received.NextAsync(ct));
		Assert.Equal(new SessionState.Reconnecting(), (await states.NextAsync(ct).WaitAsync(s_bound, s_wallClock, ct)).State);
		Assert.Equal(new SessionState.Connected(), (await states.NextAsync(ct)).State);
		Assert.Equal("U1", (await undecryptable.NextAsync(ct).WaitAsync(s_bound, s_wallClock, ct)).Id.Value);
		Assert.True(s_wallClock.GetElapsedTime(start) < s_bound, $"U1 came again after {s_wallClock.GetElapsedTime(start)}");

		// Recorded once: the next undecryptable message the application sees is a new one, not U1 again.
		_ = await fixture.Fake.DeliverUndecryptableMessageAsync(Undecryptable(Session, "U2", withhold: false), cancellationToken: ct);
		Assert.Equal("U2", (await undecryptable.NextAsync(ct)).Id.Value);
	}

	private static DeliverUndecryptableMessageRequest Undecryptable(string session, string id, bool withhold) =>
		new()
		{
			SessionId = session,
			Id = id,
			Chat = Deliveries.Chat,
			Sender = Deliveries.Chat,
			MessageTime = Timestamp.FromDateTimeOffset(Deliveries.At),
			WithholdFirstAnswer = withhold,
		};
}
