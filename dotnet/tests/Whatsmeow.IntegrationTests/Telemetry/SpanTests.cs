using System.Diagnostics;
using System.Security.Cryptography;
using Microsoft.Extensions.Diagnostics.Metrics.Testing;
using Whatsmeow.Events;
using Whatsmeow.IntegrationTests.Events;
using Whatsmeow.IntegrationTests.Fake;
using Whatsmeow.IntegrationTests.Sessions;
using Whatsmeow.IntegrationTests.Support;
using Whatsmeow.Messaging;
using Whatsmeow.Sessions;

namespace Whatsmeow.IntegrationTests.Telemetry;

/// <summary>
/// The library's spans through the public client, on the shared fake-mode sidecar (ARCHITECTURE §8.8). A call's span
/// stops before the call returns, so it is read at once, with no wait; an event's is waited for. These tests and the error counts, which carry no
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
	[Fact(Timeout = Timeout)]
	public async Task Event_ItsHandlersRunInsideItsProcessSpan_WhichHasNoParent()
	{
		var ct = TestContext.Current.CancellationToken;
		const string Session = "telemetry-process";
		var client = await SessionSetup.LinkedAsync(fixture, Session, ct);
		var current = new TaskCompletionSource<Activity?>(TaskCreationOptions.RunContinuationsAsynchronously);
		using var handler = client.On<MessageReceived>(
			(e, token) =>
			{
				_ = current.TrySetResult(Activity.Current);
				using var own = Spans.Test.StartActivity("handled");
				return ValueTask.CompletedTask;
			}
		);
		using var spans = new Spans();

		// The application connects inside a span of its own; the events that come later are not part of it.
		using (Spans.Test.StartActivity("application"))
			await client.ConnectAsync(ct);
		await using (var watch = StateWatch.Start(client, ct))
			await watch.ReachesAsync(new SessionState.Connected(), new SessionState.Connecting());
		await Deliveries.DeliverAsync(fixture.Fake, Session, ct, "M1");

		var process = await spans.WaitForAsync(
			span => span.OperationName == "whatsmeow.process" && Equals(span.GetTagItem("whatsmeow.session.id"), Session) && Equals(span.GetTagItem("whatsmeow.event.type"), nameof(MessageReceived)),
			ct
		);
		var handled = await spans.WaitForAsync(span => span.OperationName == "handled", ct);
		Assert.Equal(Tags.Of(("whatsmeow.session.id", Session), ("whatsmeow.event.type", nameof(MessageReceived))), Tags.Of(process));
		Assert.Equal((ActivityKind.Consumer, ActivityStatusCode.Unset, default(ActivitySpanId)), (process.Kind, process.Status, process.ParentSpanId));
		Assert.Same(process, await current.Task);
		Assert.Equal(process.SpanId, handled.ParentSpanId);
	}

	[Fact(Timeout = Timeout)]
	public async Task Event_AHandlerThrows_ItsProcessSpanRecordsTheExceptionType()
	{
		var ct = TestContext.Current.CancellationToken;
		const string Session = "telemetry-process-throws";
		var client = await SessionSetup.ConnectedAsync(fixture, Session, ct);
		using var handler = client.On<MessageReceived>((_, _) => throw new InvalidOperationException("a handler bug with a secret"));
		using var spans = new Spans();

		await Deliveries.DeliverAsync(fixture.Fake, Session, ct, "M1");

		var process = await spans.WaitForAsync(
			span => span.OperationName == "whatsmeow.process" && Equals(span.GetTagItem("whatsmeow.session.id"), Session) && Equals(span.GetTagItem("whatsmeow.event.type"), nameof(MessageReceived)),
			ct
		);
		Assert.Equal(
			Tags.Of(("whatsmeow.session.id", Session), ("whatsmeow.event.type", nameof(MessageReceived)), ("error.type", "System.InvalidOperationException")),
			Tags.Of(process)
		);
		Assert.Equal((ActivityStatusCode.Error, "System.InvalidOperationException"), (process.Status, process.StatusDescription));
	}

	[Fact(Timeout = Timeout)]
	public async Task NoListener_AHandlerRunsWithoutAnActivity()
	{
		var ct = TestContext.Current.CancellationToken;
		const string Session = "telemetry-quiet";
		var client = await SessionSetup.ConnectedAsync(fixture, Session, ct);
		var current = new TaskCompletionSource<Activity?>(TaskCreationOptions.RunContinuationsAsynchronously);
		using var handler = client.On<MessageReceived>(
			(_, _) =>
			{
				_ = current.TrySetResult(Activity.Current);
				return ValueTask.CompletedTask;
			}
		);

		await Deliveries.DeliverAsync(fixture.Fake, Session, ct, "M1");

		Assert.Null(await current.Task);
	}

	[Fact(Timeout = Timeout)]
	public async Task NoSpanOrMeasurement_CarriesAJidOrAPhoneNumber()
	{
		var ct = TestContext.Current.CancellationToken;
		const string Session = "telemetry-pii";
		const string Phone = "5511900000404";
		_ = await fixture.Fake.RegisterUserAsync(new RegisterUserRequest { PhoneJid = $"{Phone}@s.whatsapp.net", Lid = "900000404@lid" }, cancellationToken: ct);
		using var sent = new MetricCollector<long>(fixture.MeterFactory, WhatsmeowTelemetry.MeterName, "whatsmeow.messages.sent");
		using var received = new MetricCollector<long>(fixture.MeterFactory, WhatsmeowTelemetry.MeterName, "whatsmeow.messages.received");
		using var errors = new MetricCollector<long>(fixture.MeterFactory, WhatsmeowTelemetry.MeterName, "whatsmeow.errors");
		using var durations = new MetricCollector<double>(fixture.MeterFactory, WhatsmeowTelemetry.MeterName, "whatsmeow.handler.duration");
		using var queue = new MetricCollector<int>(fixture.MeterFactory, WhatsmeowTelemetry.MeterName, "whatsmeow.dispatcher.queue.length");
		using var spans = new Spans();
		var client = await SessionSetup.ConnectedAsync(fixture, Session, ct);
		var unpaired = await fixture.Sidecar.Sessions.GetOrCreateAsync(SessionId.From("telemetry-pii-unpaired"), ct);
		using var handler = client.On<MessageReceived>((_, _) => ValueTask.CompletedTask);

		// A send to a phone number, a message from one, a transfer, and a failed send to one.
		_ = await client.SendTextAsync(Jid.Parse($"{Phone}@s.whatsapp.net"), "hello", cancellationToken: ct);
		await Deliveries.DeliverAsync(fixture.Fake, Session, ct, "M1");
		_ = await spans.WaitForAsync(
			span => span.OperationName == "whatsmeow.process" && Equals(span.GetTagItem("whatsmeow.session.id"), Session) && Equals(span.GetTagItem("whatsmeow.event.type"), nameof(MessageReceived)),
			ct
		);
		_ = await Measured.WaitForAsync(durations, Session, measurements => measurements.Count > 0, ct);
		using var input = new MemoryStream([1, 2, 3]);
		var media = await client.UploadMediaAsync(input, MediaType.Document, ct);
		await client.DownloadMediaAsync(media, Stream.Null, ct);
		_ = await Assert.ThrowsAsync<NotLoggedInException>(() => unpaired.SendTextAsync(Jid.Parse($"{Phone}@s.whatsapp.net"), "hi", cancellationToken: ct));

		IEnumerable<KeyValuePair<string, object?>> tags =
		[
			.. spans.Of("whatsmeow.send", Session).Concat(spans.Of("whatsmeow.process", Session)).Concat(spans.Of("whatsmeow.media.upload", Session)).Concat(spans.Of("whatsmeow.media.download", Session)).SelectMany(span => span.TagObjects),
			.. sent.GetMeasurementSnapshot().SelectMany(measurement => measurement.Tags),
			.. received.GetMeasurementSnapshot().SelectMany(measurement => measurement.Tags),
			.. errors.GetMeasurementSnapshot().SelectMany(measurement => measurement.Tags),
			.. durations.GetMeasurementSnapshot().SelectMany(measurement => measurement.Tags),
			.. queue.GetMeasurementSnapshot().SelectMany(measurement => measurement.Tags),
		];
		var values = tags.Select(tag => Convert.ToString(tag.Value, System.Globalization.CultureInfo.InvariantCulture) ?? "").ToList();

		// Every kind was seen, and no value is a JID (every JID has an @), the phone number, or the sender's.
		string[] kinds = ["whatsmeow.send", "whatsmeow.process", "whatsmeow.media.upload", "whatsmeow.media.download"];
		Assert.All(kinds, name => Assert.NotEmpty(spans.Of(name, Session)));
		int[] counts = [Measured.Of(sent, Session).Count, Measured.Of(received, Session).Count, Measured.Of(durations, Session).Count, Measured.Of(queue, Session).Count, errors.GetMeasurementSnapshot().Count];
		Assert.All(counts, count => Assert.NotEqual(0, count));
		Assert.DoesNotContain(values, value => value.Contains('@', StringComparison.Ordinal) || value.Contains(Phone, StringComparison.Ordinal) || value.Contains("5511999999999", StringComparison.Ordinal) || value.Contains("900000404", StringComparison.Ordinal));
	}
}
