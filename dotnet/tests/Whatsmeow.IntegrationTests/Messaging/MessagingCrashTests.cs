using Google.Protobuf.WellKnownTypes;
using Whatsmeow.Events;
using Whatsmeow.IntegrationTests.Fake;
using Whatsmeow.IntegrationTests.Sessions;
using Whatsmeow.IntegrationTests.Support;
using Whatsmeow.Sessions;
using MessageSource = Whatsmeow.Events.MessageSource;

namespace Whatsmeow.IntegrationTests.Messaging;

/// <summary>Sends and receipts across a crash of the shared sidecar (ARCHITECTURE §13 invariants 2 and 10).</summary>
[Collection(nameof(SidecarCrashes))]
public sealed class MessagingCrashTests(FakeSidecar fixture)
{
	private const int Timeout = 120_000;

	private async Task<Sent> SentAsync(string session, CancellationToken ct, ulong after = 0) =>
		(await fixture.Fake.WaitForSentAsync(new WaitForSentRequest { SessionId = session, After = after }, cancellationToken: ct).ConfigureAwait(false)).Sent;

	[Fact(Timeout = Timeout)]
	public async Task Send_TheSidecarIsKilledAfterTheHandOff_ARepeatIsOutcomeUnknown_AndNothingIsReplayed()
	{
		var ct = TestContext.Current.CancellationToken;
		const string Session = "messaging-crash-send";
		var client = await SessionSetup.ConnectedAsync(fixture, Session, ct);
		var to = Jid.Parse("900000201@lid");
		var id = MessageId.New();
		_ = await fixture.Fake.HoldNextSendAsync(new HoldNextSendRequest { SessionId = Session }, cancellationToken: ct);

		var held = client.SendTextAsync(to, "maybe", id, cancellationToken: ct);
		Assert.Equal(id.Value, (await SentAsync(Session, ct)).Message.Id);
		await fixture.CrashAsync(ct);

		_ = await Assert.ThrowsAsync<SidecarUnavailableException>(() => held);

		// The client connects the session again after the restart.
		await using (var watch = StateWatch.Start(client, ct))
			await watch.ReachesAsync(new SessionState.Connected(), new SessionState.Disconnected(), new SessionState.Connecting());

		// It was recorded before the hand-off, so the restarted sidecar knows its outcome is unknown, and never sends it again.
		_ = await Assert.ThrowsAsync<OutcomeUnknownException>(() => client.SendTextAsync(to, "maybe", id, cancellationToken: ct));
		var next = await client.SendTextAsync(to, "next", cancellationToken: ct);
		Assert.Equal(next.Id.Value, (await SentAsync(Session, ct)).Message.Id);
	}

	[Fact(Timeout = Timeout)]
	public async Task Receipt_AckedThenTheSidecarIsKilled_StillReachesTheHandler()
	{
		var ct = TestContext.Current.CancellationToken;
		const string Session = "messaging-crash-receipt";
		var client = await SessionSetup.LinkedAsync(fixture, Session, ct);
		var at = DateTimeOffset.FromUnixTimeSeconds(1_700_000_400);

		// Delivered before this process reads the session's events: only the outbox has it once it is acked.
		_ = await fixture.Fake.DeliverReceiptAsync(
			new DeliverReceiptRequest
			{
				SessionId = Session,
				Chat = "900000202@lid",
				Sender = "900000202@lid",
				MessageIds = { "3EB0000000000000000202" },
				Type = "read",
				ReceiptTime = Timestamp.FromDateTimeOffset(at),
			},
			cancellationToken: ct
		);
		Assert.Equal(new Ack { Class = "receipt", Id = "3EB0000000000000000202", Type = "read" }, (await SentAsync(Session, ct)).Ack);
		await fixture.CrashAsync(ct);

		using var receipts = new Received<ReceiptReceived>(client);
		await client.ConnectAsync(ct);

		var expected = new ReceiptReceived
		{
			SessionId = SessionId.From(Session),
			Source = new MessageSource
			{
				Chat = Jid.Parse("900000202@lid"),
				Sender = Jid.Parse("900000202@lid"),
				SenderAlt = null,
				RecipientAlt = null,
				IsFromMe = false,
			},
			MessageIds = [MessageId.From("3EB0000000000000000202")],
			Timestamp = at,
			Type = ReceiptType.Read,
		};

		Assert.Equal(expected, await receipts.NextAsync(ct));
	}
}
