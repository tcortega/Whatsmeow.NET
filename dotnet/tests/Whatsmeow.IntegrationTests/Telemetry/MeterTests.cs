using System.Diagnostics.Metrics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.Metrics.Testing;
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
