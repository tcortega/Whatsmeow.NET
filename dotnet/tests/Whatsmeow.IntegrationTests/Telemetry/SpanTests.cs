using System.Diagnostics;
using System.Security.Cryptography;
using Microsoft.Extensions.Diagnostics.Metrics.Testing;
using Whatsmeow.IntegrationTests.Fake;
using Whatsmeow.IntegrationTests.Sessions;
using Whatsmeow.IntegrationTests.Support;
using Whatsmeow.Messaging;

namespace Whatsmeow.IntegrationTests.Telemetry;

/// <summary>
/// The library's spans through the public client, on the shared fake-mode sidecar (ARCHITECTURE §8.8). A span stops
/// before the call returns, so each is read at once, with no wait. These tests and the error counts, which carry no
/// session id, run alone in the <see cref="TelemetryListeners"/> collection.
/// </summary>
[Collection(nameof(TelemetryListeners))]
public sealed class SpanTests(FakeSidecar fixture)
{
	private const int Timeout = 120_000;
	private const string Peer = "900000401@lid";

	[Fact(Timeout = Timeout)]
	public async Task Send_WithAListener_ASpanCoversEachSendWithItsSessionMessageIdAndType()
	{
		var ct = TestContext.Current.CancellationToken;
		const string Session = "telemetry-send";
		var client = await SessionSetup.ConnectedAsync(fixture, Session, ct);
		using var input = new MemoryStream(RandomNumberGenerator.GetBytes(1000));
		var image = await client.UploadMediaAsync(input, MediaType.Image, ct);
		using var spans = new Spans();
		var (textId, imageId) = (MessageId.New(), MessageId.New());

		_ = await client.SendTextAsync(Jid.Parse(Peer), "hello", textId, cancellationToken: ct);
		var afterText = spans.Of("whatsmeow.send", Session);
		_ = await client.SendImageAsync(Jid.Parse(Peer), image, new ImageMetadata { Caption = "look" }, imageId, cancellationToken: ct);

		// Each span stopped before its send returned, and has no error.
		var text = Assert.Single(afterText);
		var sends = spans.Of("whatsmeow.send", Session);
		Assert.Equal(2, sends.Count);
		Assert.Equal(
			[
				Tags.Of(("whatsmeow.session.id", Session), ("whatsmeow.message.id", textId.Value), ("whatsmeow.message.type", "text")),
				Tags.Of(("whatsmeow.session.id", Session), ("whatsmeow.message.id", imageId.Value), ("whatsmeow.message.type", "image")),
			],
			sends.Select(Tags.Of)
		);
		Assert.Same(text, sends[0]);
		Assert.All(sends, span => Assert.Equal((ActivityKind.Client, ActivityStatusCode.Unset), (span.Kind, span.Status)));
	}

	[Fact(Timeout = Timeout)]
	public async Task Send_Failing_ItsSpanRecordsTheErrorCode_NotTheMessage()
	{
		var ct = TestContext.Current.CancellationToken;
		const string Session = "telemetry-send-unpaired";
		var client = await fixture.Sidecar.Sessions.GetOrCreateAsync(SessionId.From(Session), ct);
		using var spans = new Spans();
		var id = MessageId.New();

		_ = await Assert.ThrowsAsync<NotLoggedInException>(() => client.SendTextAsync(Jid.Parse(Peer), "hi", id, cancellationToken: ct));

		var span = Assert.Single(spans.Of("whatsmeow.send", Session));
		Assert.Equal(
			Tags.Of(("whatsmeow.session.id", Session), ("whatsmeow.message.id", id.Value), ("whatsmeow.message.type", "text"), ("error.type", "NotLoggedIn")),
			Tags.Of(span)
		);
		Assert.Equal((ActivityStatusCode.Error, "NotLoggedIn"), (span.Status, span.StatusDescription));
	}

	[Fact(Timeout = Timeout)]
	public async Task Transfer_WithAListener_ASpanCoversEachUploadAndDownloadWithItsSize()
	{
		var ct = TestContext.Current.CancellationToken;
		const string Session = "telemetry-media";
		var client = await SessionSetup.ConnectedAsync(fixture, Session, ct);
		var directory = Directory.CreateDirectory(Path.Combine(fixture.MediaRoot, Session)).FullName;
		var photo = Path.Combine(directory, "photo.jpg");
		await File.WriteAllBytesAsync(photo, RandomNumberGenerator.GetBytes(2000), ct);
		using var spans = new Spans();

		// One upload by path and one in chunks, then a download of each kind.
		var byPath = await client.UploadMediaFileAsync(photo, MediaType.Image, ct);
		using var input = new MemoryStream(RandomNumberGenerator.GetBytes(100_000));
		var inChunks = await client.UploadMediaAsync(input, MediaType.Document, ct);
		await client.DownloadMediaFileAsync(byPath, Path.Combine(directory, "copy.jpg"), ct);
		await client.DownloadMediaAsync(inChunks, Stream.Null, ct);

		Assert.Equal(
			[
				Tags.Of(("whatsmeow.session.id", Session), ("whatsmeow.media.type", "image"), ("whatsmeow.media.size", 2000L)),
				Tags.Of(("whatsmeow.session.id", Session), ("whatsmeow.media.type", "document"), ("whatsmeow.media.size", 100_000L)),
			],
			spans.Of("whatsmeow.media.upload", Session).Select(Tags.Of)
		);
		Assert.Equal(
			[
				Tags.Of(("whatsmeow.session.id", Session), ("whatsmeow.media.type", "image"), ("whatsmeow.media.size", 2000L)),
				Tags.Of(("whatsmeow.session.id", Session), ("whatsmeow.media.type", "document"), ("whatsmeow.media.size", 100_000L)),
			],
			spans.Of("whatsmeow.media.download", Session).Select(Tags.Of)
		);
		Assert.All(
			spans.Of("whatsmeow.media.upload", Session).Concat(spans.Of("whatsmeow.media.download", Session)),
			span => Assert.Equal((ActivityKind.Client, ActivityStatusCode.Unset), (span.Kind, span.Status))
		);
	}

	[Fact(Timeout = Timeout)]
	public async Task Download_Failing_ItsSpanRecordsTheErrorCode()
	{
		var ct = TestContext.Current.CancellationToken;
		const string Session = "telemetry-media-expired";
		var client = await SessionSetup.ConnectedAsync(fixture, Session, ct);
		using var input = new MemoryStream([1, 2, 3]);
		var media = await client.UploadMediaAsync(input, MediaType.Audio, ct);
		_ = await fixture.Fake.ExpireMediaAsync(new ExpireMediaRequest { DirectPath = media.DirectPath, Status = 410 }, cancellationToken: ct);
		using var spans = new Spans();

		_ = await Assert.ThrowsAsync<MediaExpiredException>(() => client.DownloadMediaAsync(media, Stream.Null, ct));

		var span = Assert.Single(spans.Of("whatsmeow.media.download", Session));
		Assert.Equal(
			Tags.Of(("whatsmeow.session.id", Session), ("whatsmeow.media.type", "audio"), ("whatsmeow.media.size", 3L), ("error.type", "MediaExpired")),
			Tags.Of(span)
		);
		Assert.Equal((ActivityStatusCode.Error, "MediaExpired"), (span.Status, span.StatusDescription));
	}

	[Fact(Timeout = Timeout)]
	public async Task Errors_AreCountedByErrorCode_ForUnaryClientStreamAndServerStreamCalls()
	{
		var ct = TestContext.Current.CancellationToken;
		var unpaired = await fixture.Sidecar.Sessions.GetOrCreateAsync(SessionId.From("telemetry-errors-unpaired"), ct);
		var offline = await SessionSetup.LinkedAsync(fixture, "telemetry-errors-offline", ct);
		var connected = await SessionSetup.ConnectedAsync(fixture, "telemetry-errors-expired", ct);
		using var three = new MemoryStream([1, 2, 3]);
		var media = await connected.UploadMediaAsync(three, MediaType.Audio, ct);
		_ = await fixture.Fake.ExpireMediaAsync(new ExpireMediaRequest { DirectPath = media.DirectPath, Status = 410 }, cancellationToken: ct);
		using var errors = new MetricCollector<long>(fixture.MeterFactory, WhatsmeowTelemetry.MeterName, "whatsmeow.errors");

		// A unary call, a client stream and a server stream, each failing with its own code.
		_ = await Assert.ThrowsAsync<NotLoggedInException>(() => unpaired.SendTextAsync(Jid.Parse(Peer), "hi", cancellationToken: ct));
		using var one = new MemoryStream([1]);
		_ = await Assert.ThrowsAsync<NotConnectedException>(() => offline.UploadMediaAsync(one, MediaType.Image, ct));
		_ = await Assert.ThrowsAsync<MediaExpiredException>(() => connected.DownloadMediaAsync(media, Stream.Null, ct));

		// Counted before each call returned. Nothing else runs beside this test, and no background call fails with these codes.
		var counted = errors.GetMeasurementSnapshot().Where(measurement => measurement.Tags["error.type"] is "NotLoggedIn" or "NotConnected" or "MediaExpired").ToList();
		Assert.Equal([1L, 1L, 1L], counted.Select(measurement => measurement.Value));
		Assert.Equal([Tags.Of(("error.type", "NotLoggedIn")), Tags.Of(("error.type", "NotConnected")), Tags.Of(("error.type", "MediaExpired"))], counted.Select(Tags.Of));
	}
}
