using System.Diagnostics.Metrics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.Metrics.Testing;
using Whatsmeow.Events;
using Whatsmeow.IntegrationTests.Events;
using Whatsmeow.IntegrationTests.Fake;
using Whatsmeow.IntegrationTests.Sessions;
using Whatsmeow.IntegrationTests.Support;
using Whatsmeow.Sessions;

namespace Whatsmeow.IntegrationTests.Telemetry;

/// <summary>
/// The library's meter and its counts (ARCHITECTURE §8.8). Counts on the shared sidecar are told apart by session id;
/// where the meter comes from is decided at the start, so those tests start a sidecar of their own on a database of its own.
/// </summary>
public sealed class MeterTests(FakeSidecar fixture, SidecarBinaries binaries)
{
	private const int Timeout = 120_000;
	private const string Peer = "900000402@lid";

	[Fact(Timeout = Timeout)]
	public async Task MessagesSent_CountsEachSendWhatsAppAccepted_BySession()
	{
		var ct = TestContext.Current.CancellationToken;
		const string Session = "telemetry-sent";
		var client = await SessionSetup.ConnectedAsync(fixture, Session, ct);
		var unpaired = await fixture.Sidecar.Sessions.GetOrCreateAsync(SessionId.From("telemetry-sent-unpaired"), ct);
		using var sent = new MetricCollector<long>(fixture.MeterFactory, WhatsmeowTelemetry.MeterName, "whatsmeow.messages.sent");

		_ = await client.SendTextAsync(Jid.Parse(Peer), "one", cancellationToken: ct);
		_ = await client.SendTextAsync(Jid.Parse(Peer), "two", cancellationToken: ct);
		_ = await Assert.ThrowsAsync<NotLoggedInException>(() => unpaired.SendTextAsync(Jid.Parse(Peer), "never", cancellationToken: ct));

		// Counted before each send returned; the failed one is not.
		var expected = Tags.Of(("whatsmeow.session.id", Session));
		var measurements = Measured.Of(sent, Session);
		Assert.Equal([1L, 1L], measurements.Select(measurement => measurement.Value));
		Assert.Equal([expected, expected], measurements.Select(Tags.Of));
		Assert.Empty(Measured.Of(sent, "telemetry-sent-unpaired"));
	}

	[Fact(Timeout = Timeout)]
	public async Task MessagesReceived_CountsEachMessageEvent_BySession()
	{
		var ct = TestContext.Current.CancellationToken;
		const string Session = "telemetry-received";
		var client = await SessionSetup.ConnectedAsync(fixture, Session, ct);
		using var messages = new Received<MessageReceived>(client);
		using var received = new MetricCollector<long>(fixture.MeterFactory, WhatsmeowTelemetry.MeterName, "whatsmeow.messages.received");

		await Deliveries.DeliverAsync(fixture.Fake, Session, ct, "M1", "M2");
		_ = await messages.NextAsync(2, ct);

		// Counted as each arrived, before its handlers ran.
		var measurements = Measured.Of(received, Session);
		Assert.Equal([1L, 1L], measurements.Select(measurement => measurement.Value));
		Assert.Equal([Tags.Of(("whatsmeow.session.id", Session)), Tags.Of(("whatsmeow.session.id", Session))], measurements.Select(Tags.Of));
	}

	[Fact(Timeout = Timeout)]
	public async Task HandlerDuration_IsRecordedForEachHandlerOfTheEvent_OneThatThrewIncluded()
	{
		var ct = TestContext.Current.CancellationToken;
		const string Session = "telemetry-duration";
		var client = await SessionSetup.ConnectedAsync(fixture, Session, ct);
		using var durations = new MetricCollector<double>(fixture.MeterFactory, WhatsmeowTelemetry.MeterName, "whatsmeow.handler.duration");
		var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

		// The first handler holds M2 in its lane, so once M2 reached it every handler of M1 has run and none of M2's has ended.
		using var first = new Received<MessageReceived>(
			client,
			async (e, token) =>
			{
				if (e.Id.Value != "M2")
					return;
				reached.SetResult();
				await release.Task.WaitAsync(token).ConfigureAwait(false);
			}
		);
		using var fine = client.On<MessageReceived>((_, _) => ValueTask.CompletedTask);
		using var failing = client.On<MessageReceived>((_, _) => throw new InvalidOperationException("a handler bug"));
		using var receipts = client.On<ReceiptReceived>((_, _) => ValueTask.CompletedTask);

		await Deliveries.DeliverAsync(fixture.Fake, Session, ct, "M1", "M2");
		await reached.Task.WaitAsync(ct);

		// One per handler of M1, in order, the one that threw included; the receipt handler did not run.
		var measurements = Measured.Of(durations, Session);
		release.SetResult();
		var message = Tags.Of(("whatsmeow.session.id", Session), ("whatsmeow.event.type", nameof(MessageReceived)));
		Assert.Equal(
			[message, message, Tags.Of(("whatsmeow.session.id", Session), ("whatsmeow.event.type", nameof(MessageReceived)), ("error.type", "System.InvalidOperationException"))],
			measurements.Select(Tags.Of)
		);
		Assert.All(measurements, measurement => Assert.True(measurement.Value >= 0));
	}

	[Fact(Timeout = Timeout)]
	public async Task QueueLength_RisesWhileAHandlerHoldsItsLane_AndFallsBackToZero()
	{
		var ct = TestContext.Current.CancellationToken;
		const string Session = "telemetry-queue";
		// From before the session's first event, so the sum of the changes is the length.
		using var queue = new MetricCollector<int>(fixture.MeterFactory, WhatsmeowTelemetry.MeterName, "whatsmeow.dispatcher.queue.length");
		var client = await SessionSetup.ConnectedAsync(fixture, Session, ct);
		var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		using var messages = new Received<MessageReceived>(client, async (_, token) => await release.Task.WaitAsync(token).ConfigureAwait(false));

		// Two messages of one chat: the first holds the lane, the second waits behind it.
		await Deliveries.DeliverAsync(fixture.Fake, Session, ct, "M1", "M2");
		_ = await Measured.WaitForAsync(queue, Session, measurements => measurements.Sum(measurement => measurement.Value) == 2, ct);

		release.SetResult();
		_ = await messages.NextAsync(2, ct);
		var all = await Measured.WaitForAsync(queue, Session, measurements => measurements.Sum(measurement => measurement.Value) == 0, ct);
		Assert.All(all, measurement => Assert.Equal(Tags.Of(("whatsmeow.session.id", Session)), Tags.Of(measurement)));
	}

	[Fact(Timeout = Timeout)]
	public async Task NoMeterFactory_TheSameMetricsArePublishedUnderTheSameName()
	{
		var ct = TestContext.Current.CancellationToken;
		const string Session = "telemetry-own-meter";
		using var logs = new LogCapture();
		// Meters of no factory: other tests' own sidecars publish there too, told apart by the session id.
		using var sent = new UnscopedMeasurements("whatsmeow.messages.sent");
		var options = new WhatsmeowSidecarOptions { SidecarPath = fixture.Binary, DatabasePath = binaries.NewDatabase() };

		await using var sidecar = await WhatsmeowSidecar.StartFakeWhatsAppAsync(options, logs.Factory, ct);
		var client = await sidecar.Sessions.GetOrCreateAsync(SessionId.From(Session), ct);
		_ = await new FakeWhatsAppService.FakeWhatsAppServiceClient(sidecar.CallInvoker).LinkDeviceAsync(
			new LinkDeviceRequest { SessionId = Session, Jid = SessionSetup.Jid(Session) },
			cancellationToken: ct
		);
		await client.ConnectAsync(ct);
		await using (var watch = StateWatch.Start(client, ct))
			await watch.ReachesAsync(new SessionState.Connected(), new SessionState.Connecting());
		_ = await client.SendTextAsync(Jid.Parse(Peer), "hello", cancellationToken: ct);

		var measurement = Assert.Single(sent.Of(Session));
		Assert.Equal(1L, measurement.Value);
		Assert.Equal(Tags.Of(("whatsmeow.session.id", Session)), measurement.Tags);
	}

	[Fact(Timeout = Timeout)]
	public async Task Start_WithAMeterFactory_TheMeterComesFromIt()
	{
		var ct = TestContext.Current.CancellationToken;
		using var logs = new LogCapture();
		await using var services = new ServiceCollection().AddMetrics().BuildServiceProvider();
		var meterFactory = services.GetRequiredService<IMeterFactory>();
		using var errors = new MetricCollector<long>(meterFactory, WhatsmeowTelemetry.MeterName, "whatsmeow.errors");
		var options = new WhatsmeowSidecarOptions { SidecarPath = binaries.Stage(binaries.Sidecar), DatabasePath = binaries.NewDatabase() };

		// The release build, through the public start.
		await using var sidecar = await WhatsmeowSidecar.StartAsync(options, logs.Factory, meterFactory, ct);
		var client = await sidecar.Sessions.GetOrCreateAsync(SessionId.From("telemetry-factory"), ct);
		_ = await Assert.ThrowsAsync<NotLoggedInException>(() => client.SendTextAsync(Jid.Parse(Peer), "hi", cancellationToken: ct));

		var measurement = Assert.Single(errors.GetMeasurementSnapshot());
		Assert.Equal(1L, measurement.Value);
		Assert.Equal(Tags.Of(("error.type", "NotLoggedIn")), Tags.Of(measurement));
	}
}
