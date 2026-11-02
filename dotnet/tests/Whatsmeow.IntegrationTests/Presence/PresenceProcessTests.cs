using Microsoft.Extensions.Time.Testing;
using Whatsmeow.Events;
using Whatsmeow.IntegrationTests.Events;
using Whatsmeow.IntegrationTests.Fake;
using Whatsmeow.IntegrationTests.Sessions;
using Whatsmeow.IntegrationTests.Support;

namespace Whatsmeow.IntegrationTests.Presence;

/// <summary>
/// Ephemeral events and the ack watermark across a graceful stop of the .NET side. The test starts fake-mode sidecars
/// of its own on a database of its own, because stopping the process is the behaviour under test.
/// </summary>
public sealed class PresenceProcessTests(FakeSidecar fixture, SidecarBinaries binaries)
{
	private const int Timeout = 120_000;

	[Fact(Timeout = Timeout)]
	public async Task Dispose_WhileAPresenceHandlerIsStuck_TheHandledMessagesAreStillAcked()
	{
		var ct = TestContext.Current.CancellationToken;
		const string Session = "presence-watermark";
		using var logs = new LogCapture();
		var options = new WhatsmeowSidecarOptions { SidecarPath = fixture.Binary, DatabasePath = binaries.NewDatabase() };

		// The clock never moves, so the timer never acks: only the stop can, and it acks the watermark.
		await using (var first = await WhatsmeowSidecar.StartFakeWhatsAppAsync(options, logs.Factory, new FakeTimeProvider(), ct))
		{
			var fake = new FakeWhatsAppService.FakeWhatsAppServiceClient(first.CallInvoker);
			var client = await first.Sessions.GetOrCreateAsync(SessionId.From(Session), ct);
			_ = await fake.LinkDeviceAsync(new LinkDeviceRequest { SessionId = Session, Jid = SessionSetup.Jid(Session) }, cancellationToken: ct);
			var held = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
			using var stuck = client.On<PresenceChanged>(
				async (_, token) =>
				{
					held.SetResult();
					await new TaskCompletionSource().Task.WaitAsync(token).ConfigureAwait(false);
				}
			);
			using var received = new Received<MessageReceived>(client);
			await client.ConnectAsync(ct);

			// The presence comes first and never finishes; the messages after it do.
			await Presences.DeliverAsync(fake, ct, Presences.Of(Session, 1));
			await held.Task.WaitAsync(ct);
			await Deliveries.DeliverAsync(fake, Session, ct, "M1", "M2");
			_ = await received.NextAsync(2, ct);
		}

		await using var second = await WhatsmeowSidecar.StartFakeWhatsAppAsync(options, logs.Factory, ct);
		var again = await second.Sessions.GetOrCreateAsync(SessionId.From(Session), ct);
		using var next = new Received<MessageReceived>(again);
		await again.ConnectAsync(ct);
		await Deliveries.DeliverAsync(new FakeWhatsAppService.FakeWhatsAppServiceClient(second.CallInvoker), Session, ct, "M3");

		Assert.Equal(Deliveries.Received(Session, "M3"), await next.NextAsync(ct));
	}
}
