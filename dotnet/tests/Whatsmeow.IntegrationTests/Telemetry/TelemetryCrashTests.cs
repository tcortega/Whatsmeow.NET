using System.Diagnostics;
using System.Security.Cryptography;
using Microsoft.Extensions.Diagnostics.Metrics.Testing;
using Whatsmeow.IntegrationTests.Fake;
using Whatsmeow.IntegrationTests.Sessions;
using Whatsmeow.IntegrationTests.Support;

namespace Whatsmeow.IntegrationTests.Telemetry;

/// <summary>
/// The telemetry across a crash of the shared sidecar; in the <see cref="SidecarCrashes"/> collection, which never runs
/// in parallel, so a span listener here hears no other test.
/// </summary>
[Collection(nameof(SidecarCrashes))]
public sealed class TelemetryCrashTests(FakeSidecar fixture)
{
	private const int Timeout = 120_000;

	[Fact(Timeout = Timeout)]
	public async Task SidecarRestart_IsCounted()
	{
		var ct = TestContext.Current.CancellationToken;
		using var restarts = new MetricCollector<long>(fixture.MeterFactory, WhatsmeowTelemetry.MeterName, "whatsmeow.sidecar.restarts");

		await fixture.CrashAsync(ct);

		// CrashAsync returns once the restart was logged, after it was counted.
		var measurement = Assert.Single(restarts.GetMeasurementSnapshot());
		Assert.Equal((1L, 0), (measurement.Value, measurement.Tags.Count));
	}

	[Fact(Timeout = Timeout)]
	public async Task Download_TheSidecarDiesMidway_ItsSpanIsFailed_AndTheErrorIsCounted()
	{
		var ct = TestContext.Current.CancellationToken;
		const string Session = "telemetry-crash-download";
		var client = await SessionSetup.ConnectedAsync(fixture, Session, ct);
		using var input = new MemoryStream(RandomNumberGenerator.GetBytes(100 * 1024));
		var media = await client.UploadMediaAsync(input, MediaType.Document, ct);
		var uploaded = (await fixture.Fake.WaitForSentAsync(new WaitForSentRequest { SessionId = Session }, cancellationToken: ct)).Sent;
		_ = await fixture.Fake.HoldNextDownloadAsync(new HoldNextDownloadRequest { SessionId = Session }, cancellationToken: ct);
		using var errors = new MetricCollector<long>(fixture.MeterFactory, WhatsmeowTelemetry.MeterName, "whatsmeow.errors");
		using var spans = new Spans();

		// The download is under way when the sidecar dies; the caller cancelled nothing.
		var download = client.DownloadMediaAsync(media, Stream.Null, ct);
		Assert.NotNull((await fixture.Fake.WaitForSentAsync(new WaitForSentRequest { SessionId = Session, After = uploaded.Index }, cancellationToken: ct)).Sent.MediaDownload);
		await fixture.CrashAsync(ct);
		var failure = await Assert.ThrowsAsync<SidecarUnavailableException>(() => download);

		// A call the sidecar cut off is a failure: the span says so, and it is counted by its error code.
		Assert.Equal(WhatsmeowErrorCode.Unknown, failure.ErrorCode);
		var type = nameof(WhatsmeowErrorCode.Unknown);
		var span = Assert.Single(spans.Of("whatsmeow.media.download", Session));
		Assert.Equal(
			Tags.Of(("whatsmeow.session.id", Session), ("whatsmeow.media.type", "document"), ("whatsmeow.media.size", 100L * 1024), ("error.type", type)),
			Tags.Of(span)
		);
		Assert.Equal(ActivityStatusCode.Error, span.Status);
		Assert.Contains(errors.GetMeasurementSnapshot(), measurement => Equals(measurement.Tags["error.type"], type));
	}
}
