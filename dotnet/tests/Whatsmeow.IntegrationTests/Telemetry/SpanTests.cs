using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Security.Cryptography;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.Metrics.Testing;
using Whatsmeow.Events;
using Whatsmeow.IntegrationTests.Events;
using Whatsmeow.IntegrationTests.Fake;
using Whatsmeow.IntegrationTests.Media;
using Whatsmeow.IntegrationTests.Sessions;
using Whatsmeow.IntegrationTests.Support;
using Whatsmeow.Messaging;
using Whatsmeow.Sessions;
using E2E = Whatsmeow.Proto.WAWebProtobufsE2E;

namespace Whatsmeow.IntegrationTests.Telemetry;

/// <summary>
/// The library's spans through the public client, on the shared fake-mode sidecar (ARCHITECTURE §8.8). A call's span
/// stops before the call returns, so it is read at once, with no wait; an event's is waited for. These tests, and the
/// cancellation tests' error counts, which carry no session id, run alone in the <see cref="TelemetryListeners"/> collection.
/// </summary>
[Collection(nameof(TelemetryListeners))]
public sealed class SpanTests(FakeSidecar fixture, SidecarBinaries binaries)
{
	private const int Timeout = 120_000;
	private const string Peer = "900000401@lid";

	[Theory(Timeout = Timeout)]
	[InlineData("text")]
	[InlineData("image")]
	[InlineData("video")]
	[InlineData("audio")]
	[InlineData("document")]
	[InlineData("sticker")]
	[InlineData("raw")]
	public async Task Send_WithAListener_ASpanCoversEachOfTheSevenSends_AndEachIsCounted(string type)
	{
		var ct = TestContext.Current.CancellationToken;
		var session = $"telemetry-send-{type}";
		var client = await SessionSetup.ConnectedAsync(fixture, session, ct);
		var media = type is "text" or "raw" ? null : await UploadAsync(client, type, ct);
		using var sent = new MetricCollector<long>(fixture.MeterFactory, WhatsmeowTelemetry.MeterName, "whatsmeow.messages.sent");
		using var spans = new Spans();
		var id = MessageId.New();

		_ = await SendAsync(client, type, media, id, ct);

		// The span stopped before the send returned, and has no error; the send was counted before it returned too.
		var span = Assert.Single(spans.Of("whatsmeow.send", session));
		Assert.Equal(Tags.Of(("whatsmeow.session.id", session), ("whatsmeow.message.id", id.Value), ("whatsmeow.message.type", type)), Tags.Of(span));
		Assert.Equal((ActivityKind.Client, ActivityStatusCode.Unset), (span.Kind, span.Status));
		var counted = Assert.Single(Measured.Of(sent, session));
		Assert.Equal(1L, counted.Value);
		Assert.Equal(Tags.Of(("whatsmeow.session.id", session)), Tags.Of(counted));
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
		Assert.Equal((ActivityStatusCode.Error, null), (span.Status, span.StatusDescription));
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
		Assert.Equal((ActivityStatusCode.Error, null), (span.Status, span.StatusDescription));
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
		Assert.Equal((ActivityStatusCode.Error, null), (process.Status, process.StatusDescription));
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

	[Fact(Timeout = Timeout)]
	public async Task Event_WithOnlyTheApplicationListening_AHandlerRunsOutsideTheSpanItsSessionConnectedIn()
	{
		var ct = TestContext.Current.CancellationToken;
		const string Session = "telemetry-process-app-only";
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
		using var spans = Spans.OfTheTests();

		// The application connects inside a span of its own, and listens to its own spans only, not to the library's.
		Activity? application;
		using (application = Spans.Test.StartActivity("application"))
			await client.ConnectAsync(ct);
		await using (var watch = StateWatch.Start(client, ct))
			await watch.ReachesAsync(new SessionState.Connected(), new SessionState.Connecting());
		await Deliveries.DeliverAsync(fixture.Fake, Session, ct, "M1");

		// The event is not part of the connect: the handler's span is a root of a trace of its own.
		var handled = await spans.WaitForAsync(span => span.OperationName == "handled", ct);
		Assert.NotNull(application);
		Assert.Null(await current.Task);
		Assert.Equal(default, handled.ParentSpanId);
		Assert.NotEqual(application.TraceId, handled.TraceId);
	}

	[Fact(Timeout = Timeout)]
	public async Task Event_NoHandlerAcceptsIt_HasNoProcessSpan()
	{
		var ct = TestContext.Current.CancellationToken;
		const string Session = "telemetry-process-unhandled";
		var client = await SessionSetup.ConnectedAsync(fixture, Session, ct);
		var receipts = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		using var handler = client.On<ReceiptReceived>(
			(_, _) =>
			{
				_ = receipts.TrySetResult();
				return ValueTask.CompletedTask;
			}
		);
		using var spans = new Spans();

		// A message no handler takes, then a receipt in the same chat, so its lane ran the message first.
		await Deliveries.DeliverAsync(fixture.Fake, Session, ct, "M1");
		_ = await fixture.Fake.DeliverReceiptAsync(
			new DeliverReceiptRequest
			{
				SessionId = Session,
				Chat = Deliveries.Chat,
				Sender = Deliveries.Chat,
				MessageIds = { "M0" },
			},
			cancellationToken: ct
		);
		await receipts.Task.WaitAsync(ct);

		_ = await spans.WaitForAsync(span => span.OperationName == "whatsmeow.process" && Equals(span.GetTagItem("whatsmeow.session.id"), Session), ct);
		Assert.Equal([nameof(ReceiptReceived)], spans.Of("whatsmeow.process", Session).Select(span => span.GetTagItem("whatsmeow.event.type")));
	}

	[Fact(Timeout = Timeout)]
	public async Task Send_Cancelled_ItsSpanHasNoError_AndNoErrorIsCounted()
	{
		var ct = TestContext.Current.CancellationToken;
		const string Session = "telemetry-send-cancel";
		var client = await SessionSetup.ConnectedAsync(fixture, Session, ct);
		using var errors = new MetricCollector<long>(fixture.MeterFactory, WhatsmeowTelemetry.MeterName, "whatsmeow.errors");
		using var sent = new MetricCollector<long>(fixture.MeterFactory, WhatsmeowTelemetry.MeterName, "whatsmeow.messages.sent");
		_ = await fixture.Fake.HoldNextSendAsync(new HoldNextSendRequest { SessionId = Session }, cancellationToken: ct);
		using var spans = new Spans();
		using var cancel = CancellationTokenSource.CreateLinkedTokenSource(ct);
		var id = MessageId.New();

		// Cancelled once WhatsApp holds it.
		var held = client.SendTextAsync(Jid.Parse(Peer), "maybe", id, cancellationToken: cancel.Token);
		_ = await fixture.Fake.WaitForSentAsync(new WaitForSentRequest { SessionId = Session }, cancellationToken: ct);
		await cancel.CancelAsync();
		_ = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => held);

		var span = Assert.Single(spans.Of("whatsmeow.send", Session));
		Assert.Equal(Tags.Of(("whatsmeow.session.id", Session), ("whatsmeow.message.id", id.Value), ("whatsmeow.message.type", "text")), Tags.Of(span));
		Assert.Equal(ActivityStatusCode.Unset, span.Status);
		Assert.Empty(errors.GetMeasurementSnapshot());
		Assert.Empty(Measured.Of(sent, Session));
	}

	[Fact(Timeout = Timeout)]
	public async Task Upload_Cancelled_ItsSpanHasNoError_AndNoErrorIsCounted()
	{
		var ct = TestContext.Current.CancellationToken;
		const string Session = "telemetry-media-cancel";
		var client = await SessionSetup.ConnectedAsync(fixture, Session, ct);
		using var errors = new MetricCollector<long>(fixture.MeterFactory, WhatsmeowTelemetry.MeterName, "whatsmeow.errors");
		await using var input = new StallingStream();
		using var spans = new Spans();
		using var cancel = CancellationTokenSource.CreateLinkedTokenSource(ct);

		// Cancelled once the first chunk went out.
		_ = await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
		{
			var upload = client.UploadMediaAsync(input, MediaType.Document, cancel.Token);
			await input.StalledAsync(ct).ConfigureAwait(false);
			await cancel.CancelAsync().ConfigureAwait(false);
			_ = await upload.ConfigureAwait(false);
		});

		var span = Assert.Single(spans.Of("whatsmeow.media.upload", Session));
		Assert.Equal(Tags.Of(("whatsmeow.session.id", Session), ("whatsmeow.media.type", "document")), Tags.Of(span));
		Assert.Equal(ActivityStatusCode.Unset, span.Status);
		Assert.Empty(errors.GetMeasurementSnapshot());
	}

	[Fact(Timeout = Timeout)]
	public async Task Event_AHandlersOwnCancellation_IsAFailure_OnItsProcessSpanAndItsDuration()
	{
		var ct = TestContext.Current.CancellationToken;
		const string Session = "telemetry-process-cancels";
		var client = await SessionSetup.ConnectedAsync(fixture, Session, ct);
		using var durations = new MetricCollector<double>(fixture.MeterFactory, WhatsmeowTelemetry.MeterName, "whatsmeow.handler.duration");
		using var handler = client.On<MessageReceived>((_, _) => throw new OperationCanceledException("the handler's own timeout"));
		using var spans = new Spans();

		await Deliveries.DeliverAsync(fixture.Fake, Session, ct, "M1");

		// Not the stop's: as an HTTP timeout in the handler would be. The span stops after the duration was recorded.
		var process = await spans.WaitForAsync(
			span => span.OperationName == "whatsmeow.process" && Equals(span.GetTagItem("whatsmeow.session.id"), Session) && Equals(span.GetTagItem("whatsmeow.event.type"), nameof(MessageReceived)),
			ct
		);
		var failed = Tags.Of(("whatsmeow.session.id", Session), ("whatsmeow.event.type", nameof(MessageReceived)), ("error.type", "System.OperationCanceledException"));
		Assert.Equal(failed, Tags.Of(process));
		Assert.Equal(ActivityStatusCode.Error, process.Status);
		var duration = Assert.Single(Measured.Of(durations, Session));
		Assert.Equal(failed, Tags.Of(duration));
	}

	[Fact(Timeout = Timeout)]
	public async Task Event_TheStopCancelsAHandler_ItsProcessSpanHasNoError_AndNoDurationIsRecorded()
	{
		var ct = TestContext.Current.CancellationToken;
		const string Session = "telemetry-process-stopped";
		using var logs = new LogCapture();
		// A sidecar of its own, which the test stops while its handler runs.
		await using var services = new ServiceCollection().AddMetrics().BuildServiceProvider();
		var meterFactory = services.GetRequiredService<IMeterFactory>();
		using var durations = new MetricCollector<double>(meterFactory, WhatsmeowTelemetry.MeterName, "whatsmeow.handler.duration");
		var options = new WhatsmeowSidecarOptions { SidecarPath = fixture.Binary, DatabasePath = binaries.NewDatabase() };
		await using var sidecar = await WhatsmeowSidecar.StartFakeWhatsAppWithMeterFactoryAsync(options, logs.Factory, meterFactory, ct);
		var fake = new FakeWhatsAppService.FakeWhatsAppServiceClient(sidecar.CallInvoker);
		var client = await sidecar.Sessions.GetOrCreateAsync(SessionId.From(Session), ct);
		_ = await fake.LinkDeviceAsync(new LinkDeviceRequest { SessionId = Session, Jid = SessionSetup.Jid(Session) }, cancellationToken: ct);
		var running = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var never = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		using var handler = client.On<MessageReceived>(
			async (_, token) =>
			{
				running.SetResult();
				await never.Task.WaitAsync(token).ConfigureAwait(false);
			}
		);
		using var spans = new Spans();
		await client.ConnectAsync(ct);
		await using (var watch = StateWatch.Start(client, ct))
			await watch.ReachesAsync(new SessionState.Connected(), new SessionState.Connecting());
		_ = await fake.DeliverMessageAsync(Deliveries.Message(Session, "M1"), cancellationToken: ct);
		await running.Task.WaitAsync(ct);

		await sidecar.DisposeAsync();

		// The stop waited for the handler, so its span has stopped.
		var process = Assert.Single(spans.Of("whatsmeow.process", Session), span => Equals(span.GetTagItem("whatsmeow.event.type"), nameof(MessageReceived)));
		Assert.Equal(Tags.Of(("whatsmeow.session.id", Session), ("whatsmeow.event.type", nameof(MessageReceived))), Tags.Of(process));
		Assert.Equal(ActivityStatusCode.Unset, process.Status);
		Assert.Empty(Measured.Of(durations, Session));
	}

	[Fact(Timeout = Timeout)]
	public async Task Event_OnlyAWaitForAsyncTakesIt_HasNoProcessSpan_AndNoDuration()
	{
		var ct = TestContext.Current.CancellationToken;
		const string Session = "telemetry-process-waited";
		var client = await SessionSetup.ConnectedAsync(fixture, Session, ct);
		using var durations = new MetricCollector<double>(fixture.MeterFactory, WhatsmeowTelemetry.MeterName, "whatsmeow.handler.duration");
		var receipts = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		using var handler = client.On<ReceiptReceived>(
			(_, _) =>
			{
				_ = receipts.TrySetResult();
				return ValueTask.CompletedTask;
			}
		);
		using var spans = new Spans();
		var waited = client.WaitForAsync<MessageReceived>(_ => true, TimeSpan.FromSeconds(60), ct);

		// The waited message, then a receipt in the same chat, so its lane ran the message first.
		await Deliveries.DeliverAsync(fixture.Fake, Session, ct, "M1");
		_ = await fixture.Fake.DeliverReceiptAsync(new DeliverReceiptRequest { SessionId = Session, Chat = Deliveries.Chat, Sender = Deliveries.Chat, MessageIds = { "M0" } }, cancellationToken: ct);
		await receipts.Task.WaitAsync(ct);

		// The wait is no handler of the application's: only the receipt's handler made a span and a duration.
		Assert.Equal("M1", (await waited).Id.Value);
		_ = await spans.WaitForAsync(span => span.OperationName == "whatsmeow.process" && Equals(span.GetTagItem("whatsmeow.session.id"), Session), ct);
		Assert.Equal([nameof(ReceiptReceived)], spans.Of("whatsmeow.process", Session).Select(span => span.GetTagItem("whatsmeow.event.type")));
		Assert.Equal([nameof(ReceiptReceived)], Measured.Of(durations, Session).Select(measurement => measurement.Tags["whatsmeow.event.type"]));
	}

	[Fact(Timeout = Timeout)]
	public async Task NoListener_ASendAndATransferStartNoActivity()
	{
		var ct = TestContext.Current.CancellationToken;
		const string Session = "telemetry-quiet-calls";
		var client = await SessionSetup.ConnectedAsync(fixture, Session, ct);
		using var spans = Spans.OfAllButTheLibrary();

		// The library's calls start spans of their own (gRPC's, HTTP's); none has a library activity above it.
		_ = await client.SendTextAsync(Jid.Parse(Peer), "hello", cancellationToken: ct);
		using var input = new MemoryStream([1, 2, 3]);
		var media = await client.UploadMediaAsync(input, MediaType.Document, ct);
		await client.DownloadMediaAsync(media, Stream.Null, ct);

		Assert.NotEmpty(spans.All);
		Assert.All(spans.All, span => Assert.DoesNotContain(Ancestors(span), above => above.OperationName.StartsWith("whatsmeow.", StringComparison.Ordinal)));
	}

	private static IEnumerable<Activity> Ancestors(Activity span)
	{
		for (var above = span.Parent; above is not null; above = above.Parent)
			yield return above;
	}

	private static async Task<MediaRef> UploadAsync(IWhatsAppClient client, string type, CancellationToken ct)
	{
		using var input = new MemoryStream(RandomNumberGenerator.GetBytes(1000));
		var mediaType = type switch
		{
			"video" => MediaType.Video,
			"audio" => MediaType.Audio,
			"document" => MediaType.Document,
			_ => MediaType.Image,
		};
		return await client.UploadMediaAsync(input, mediaType, ct).ConfigureAwait(false);
	}

	private static Task<SendResult> SendAsync(IWhatsAppClient client, string type, MediaRef? media, MessageId id, CancellationToken ct)
	{
		var to = Jid.Parse(Peer);
#pragma warning disable WMN1001 // Justification: the raw send is one of the seven sends under test
		return type switch
		{
			"text" => client.SendTextAsync(to, "hello", id, cancellationToken: ct),
			"image" => client.SendImageAsync(to, media!, new ImageMetadata(), id, cancellationToken: ct),
			"video" => client.SendVideoAsync(to, media!, new VideoMetadata(), id, cancellationToken: ct),
			"audio" => client.SendAudioAsync(to, media!, new AudioMetadata(), id, cancellationToken: ct),
			"document" => client.SendDocumentAsync(to, media!, new DocumentMetadata(), id, cancellationToken: ct),
			"sticker" => client.SendStickerAsync(to, media!, new StickerMetadata(), id, cancellationToken: ct),
			_ => client.SendRawAsync(to, new E2E.Message { Conversation = "hello" }, id, ct),
		};
#pragma warning restore WMN1001
	}
}
