using System.Collections.Immutable;
using System.Security.Cryptography;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Google.Rpc;
using Grpc.Core;
using Whatsmeow.Events;
using Whatsmeow.IntegrationTests.Events;
using Whatsmeow.IntegrationTests.Fake;
using Whatsmeow.IntegrationTests.Media;
using Whatsmeow.IntegrationTests.Sessions;
using Whatsmeow.IntegrationTests.Support;
using Whatsmeow.Messaging;
using Whatsmeow.Sessions;
using E2E = Whatsmeow.Proto.WAWebProtobufsE2E;
using MessageSource = Whatsmeow.Events.MessageSource;

namespace Whatsmeow.IntegrationTests.Messaging;

/// <summary>
/// Media messages through the public client, on the shared fake-mode sidecar: sends of each kind from a
/// <see cref="MediaRef"/> and the caller's metadata, and received media as typed content whose <see cref="MediaRef"/>
/// downloads. What WhatsApp got is read from the fake's log by condition; "nothing was uploaded" and "nothing was sent"
/// show by order.
/// </summary>
public sealed class MediaMessageTests(FakeSidecar fixture)
{
	private const int Timeout = 120_000;

	/// <summary>The other user every test sends to and receives from, by LID, which needs no lookup.</summary>
	private static readonly Jid s_peer = Jid.Parse("900000201@lid");

	private static readonly ImmutableArray<byte> s_jpeg = [0xFF, 0xD8, 0xFF, 0xE0, 1, 2, 3];
	private static readonly ImmutableArray<byte> s_png = [0x89, (byte)'P', (byte)'N', (byte)'G', 4, 5, 6];
	private static readonly ImmutableArray<byte> s_waveform = [0, 25, 50, 100, 50, 25, 0];
	private static readonly ImmutableArray<byte> s_sidecar = [1, 2, 3, 4, 5, 6, 7, 8, 9, 10];

	[Fact(Timeout = Timeout)]
	public async Task FullCycle_SendEachMediaKind_WhatsAppGetsThatMessage_AndTheResultIsAsForText()
	{
		var ct = TestContext.Current.CancellationToken;
		const string Session = "media-messages-send";
		var client = await SessionSetup.ConnectedAsync(fixture, Session, ct);
		var image = await UploadAsync(client, MediaType.Image, 20 * 1024, ct);
		var video = await UploadAsync(client, MediaType.Video, 150 * 1024, ct);
		var audio = await UploadAsync(client, MediaType.Audio, 10 * 1024, ct);
		var voice = await UploadAsync(client, MediaType.Audio, 8 * 1024, ct);
		var document = await UploadAsync(client, MediaType.Document, 30 * 1024, ct);
		var sticker = await UploadAsync(client, MediaType.Image, 5 * 1024, ct);
		var uploads = await NextAsync(Session, 0, 6, ct);

		// whatsmeow makes a video's streaming sidecar, 10 bytes per 64 KiB of what it uploads (upload.go:141-181).
		Assert.Equal(30, video.StreamingSidecar.Length);

		SendResult[] results =
		[
			await client.SendImageAsync(
				s_peer,
				image,
				new ImageMetadata { MimeType = "image/jpeg", Caption = "look", Width = 640, Height = 480, JpegThumbnail = s_jpeg },
				cancellationToken: ct
			),
			await client.SendVideoAsync(
				s_peer,
				video,
				new VideoMetadata
				{
					MimeType = "video/mp4",
					Caption = "watch",
					Width = 1280,
					Height = 720,
					Duration = TimeSpan.FromSeconds(12.9),
					JpegThumbnail = s_jpeg,
				},
				cancellationToken: ct
			),
			await client.SendAudioAsync(s_peer, audio, new AudioMetadata { MimeType = "audio/mpeg", Duration = TimeSpan.FromSeconds(200) }, cancellationToken: ct),
			await client.SendAudioAsync(
				s_peer,
				voice,
				new AudioMetadata
				{
					MimeType = "audio/ogg; codecs=opus",
					Duration = TimeSpan.FromSeconds(3),
					IsVoiceNote = true,
					Waveform = s_waveform,
				},
				cancellationToken: ct
			),
			await client.SendDocumentAsync(
				s_peer,
				document,
				new DocumentMetadata
				{
					MimeType = "application/pdf",
					FileName = "report.pdf",
					Caption = "the numbers",
					JpegThumbnail = s_jpeg,
				},
				cancellationToken: ct
			),
			await client.SendStickerAsync(
				s_peer,
				sticker,
				new StickerMetadata { MimeType = "image/webp", Width = 512, Height = 512, PngThumbnail = s_png },
				cancellationToken: ct
			),
		];

		E2E.Message[] expected =
		[
			new()
			{
				ImageMessage = new E2E.ImageMessage(ImageOf(image))
				{
					Mimetype = "image/jpeg",
					Caption = "look",
					Width = 640,
					Height = 480,
					JPEGThumbnail = Bytes(s_jpeg),
				},
			},
			// With the upload's streaming sidecar, and the length in whole seconds.
			new()
			{
				VideoMessage = new E2E.VideoMessage(VideoOf(video))
				{
					Mimetype = "video/mp4",
					Caption = "watch",
					Width = 1280,
					Height = 720,
					Seconds = 12,
					JPEGThumbnail = Bytes(s_jpeg),
				},
			},
			// An audio file says so with PTT false, as mautrix sends it.
			new()
			{
				AudioMessage = new E2E.AudioMessage(AudioOf(audio))
				{
					Mimetype = "audio/mpeg",
					Seconds = 200,
					PTT = false,
				},
			},
			new()
			{
				AudioMessage = new E2E.AudioMessage(AudioOf(voice))
				{
					Mimetype = "audio/ogg; codecs=opus",
					Seconds = 3,
					PTT = true,
					Waveform = Bytes(s_waveform),
				},
			},
			// A caption shows with the file only on a wrapped document.
			new()
			{
				DocumentWithCaptionMessage = new E2E.FutureProofMessage
				{
					Message = new E2E.Message
					{
						DocumentMessage = new E2E.DocumentMessage(DocumentOf(document))
						{
							Mimetype = "application/pdf",
							FileName = "report.pdf",
							Caption = "the numbers",
							JPEGThumbnail = Bytes(s_jpeg),
						},
					},
				},
			},
			// A PNG thumbnail: a sticker has no JPEG one.
			new()
			{
				StickerMessage = new E2E.StickerMessage(StickerOf(sticker))
				{
					Mimetype = "image/webp",
					Width = 512,
					Height = 512,
					PngThumbnail = Bytes(s_png),
				},
			},
		];

		var sent = await NextAsync(Session, uploads[^1].Index, results.Length, ct);
		for (var i = 0; i < results.Length; i++)
		{
			Assert.Equal(
				new SentMessage
				{
					Id = results[i].Id.Value,
					Chat = s_peer.ToString(),
					Message = expected[i],
					MessageTime = sent[i].Message.MessageTime,
				},
				sent[i].Message
			);
			Assert.Equal(new SendResult { Id = results[i].Id, Chat = s_peer, Timestamp = sent[i].Message.MessageTime.ToDateTimeOffset() }, results[i]);
		}
	}

	[Fact(Timeout = Timeout)]
	public async Task FullCycle_ReceiveEachMediaKind_IsItsContentCase_AndItsMediaRefDownloads()
	{
		var ct = TestContext.Current.CancellationToken;
		const string Session = "media-messages-receive";
		var client = await SessionSetup.LinkedAsync(fixture, Session, ct);
		using var received = new Received<MessageReceived>(client);
		await ConnectAsync(client, ct);
		var contents = new Dictionary<MediaType, byte[]>();
		var image = await UploadAsync(client, MediaType.Image, 20 * 1024, ct, contents);
		var video = await UploadAsync(client, MediaType.Video, 150 * 1024, ct, contents);
		var voice = await UploadAsync(client, MediaType.Audio, 8 * 1024, ct, contents);
		var document = await UploadAsync(client, MediaType.Document, 30 * 1024, ct, contents);

		(E2E.Message Message, MessageContent Content)[] cases =
		[
			(
				new() { ImageMessage = new E2E.ImageMessage(ImageOf(image)) { Mimetype = "image/jpeg", Caption = "look", Width = 640, Height = 480, JPEGThumbnail = Bytes(s_jpeg) } },
				new ImageContent
				{
					Media = image,
					Metadata = new ImageMetadata { MimeType = "image/jpeg", Caption = "look", Width = 640, Height = 480, JpegThumbnail = s_jpeg },
					IsViewOnce = false,
				}
			),
			(
				new() { VideoMessage = new E2E.VideoMessage(VideoOf(video)) { Mimetype = "video/mp4", Seconds = 12, Width = 1280, Height = 720 } },
				new VideoContent
				{
					Media = video,
					Metadata = new VideoMetadata { MimeType = "video/mp4", Duration = TimeSpan.FromSeconds(12), Width = 1280, Height = 720 },
					IsViewOnce = false,
				}
			),
			(
				new()
				{
					AudioMessage = new E2E.AudioMessage(AudioOf(voice)) { Mimetype = "audio/ogg; codecs=opus", Seconds = 3, PTT = true, Waveform = Bytes(s_waveform) },
				},
				new AudioContent
				{
					Media = voice,
					Metadata = new AudioMetadata { MimeType = "audio/ogg; codecs=opus", Duration = TimeSpan.FromSeconds(3), IsVoiceNote = true, Waveform = s_waveform },
					IsViewOnce = false,
				}
			),
			// An audio can carry a streaming sidecar, as a forwarded one does: it stays in the MediaRef, to be sent on.
			(
				new() { AudioMessage = new E2E.AudioMessage(AudioOf(voice)) { Mimetype = "audio/mpeg", StreamingSidecar = Bytes(s_sidecar) } },
				new AudioContent
				{
					Media = voice with { StreamingSidecar = s_sidecar },
					Metadata = new AudioMetadata { MimeType = "audio/mpeg" },
					IsViewOnce = false,
				}
			),
			// whatsmeow unwraps a captioned document before the sidecar sees it (types/events/events.go:413-416).
			(
				new()
				{
					DocumentWithCaptionMessage = new E2E.FutureProofMessage
					{
						Message = new E2E.Message
						{
							DocumentMessage = new E2E.DocumentMessage(DocumentOf(document)) { Mimetype = "application/pdf", FileName = "report.pdf", Caption = "the numbers" },
						},
					},
				},
				new DocumentContent
				{
					Media = document,
					Metadata = new DocumentMetadata { MimeType = "application/pdf", FileName = "report.pdf", Caption = "the numbers" },
				}
			),
			(
				new() { StickerMessage = new E2E.StickerMessage(StickerOf(image)) { Mimetype = "image/webp", Width = 512, Height = 512, PngThumbnail = Bytes(s_png) } },
				new StickerContent
				{
					Media = image,
					Metadata = new StickerMetadata { MimeType = "image/webp", Width = 512, Height = 512, PngThumbnail = s_png },
				}
			),
			// Nothing in the message but the file: every metadata field is unknown.
			(new() { ImageMessage = ImageOf(image) }, new ImageContent { Media = image, Metadata = new ImageMetadata(), IsViewOnce = false }),
		];

		for (var i = 0; i < cases.Length; i++)
		{
			await DeliverAsync(Session, $"M{i}", cases[i].Message, ct);
			Assert.Equal(Received(Session, $"M{i}", cases[i].Content), await received.NextAsync(ct));
		}

		// Each MediaRef downloads what was uploaded, lazily, from the message alone.
		foreach (var media in new[] { image, video, voice, document })
			await MediaChecks.ValidateDownloadsAsync(client, media, contents[media.Type], ct);
	}

	[Fact(Timeout = Timeout)]
	public async Task Forward_AReceivedMediaRef_SendsWithoutUploading()
	{
		var ct = TestContext.Current.CancellationToken;
		const string Session = "media-messages-forward";
		var client = await SessionSetup.LinkedAsync(fixture, Session, ct);
		using var received = new Received<MessageReceived>(client);
		await ConnectAsync(client, ct);
		var image = await UploadAsync(client, MediaType.Image, 20 * 1024, ct);
		var upload = await NextAsync(Session, 0, 1, ct);
		await DeliverAsync(Session, "M1", new E2E.Message { ImageMessage = new E2E.ImageMessage(ImageOf(image)) { Caption = "pass it on" } }, ct);
		var content = Assert.IsType<ImageContent>((await received.NextAsync(ct)).Content);
		var receipt = (await NextAsync(Session, upload[0].Index, 1, ct))[0];
		Assert.Equal("M1", receipt.Receipt.Id);
		var other = Jid.Parse("900000202@lid");

		var result = await client.SendImageAsync(other, content.Media, content.Metadata, cancellationToken: ct);

		// The next thing WhatsApp got is the message: no upload came first.
		var sent = (await NextAsync(Session, receipt.Index, 1, ct))[0];
		Assert.Equal(
			new SentMessage
			{
				Id = result.Id.Value,
				Chat = other.ToString(),
				Message = new E2E.Message { ImageMessage = new E2E.ImageMessage(ImageOf(image)) { Caption = "pass it on" } },
				MessageTime = sent.Message.MessageTime,
			},
			sent.Message
		);
	}

	[Fact(Timeout = Timeout)]
	public async Task Receive_AViewOnceImageVideoOrVoiceNote_SaysItIsViewOnce()
	{
		var ct = TestContext.Current.CancellationToken;
		const string Session = "media-messages-view-once";
		var client = await SessionSetup.LinkedAsync(fixture, Session, ct);
		using var received = new Received<MessageReceived>(client);
		await ConnectAsync(client, ct);
		var image = await UploadAsync(client, MediaType.Image, 1024, ct);
		var video = await UploadAsync(client, MediaType.Video, 1024, ct);
		var voice = await UploadAsync(client, MediaType.Audio, 1024, ct);

		(E2E.Message Message, MessageContent Content)[] cases =
		[
			// In each of whatsmeow's three wrappers (types/events/events.go:394-408), and by the inner flag alone, which
			// mautrix reads too (pkg/msgconv/wa-media.go:52-53).
			(
				new() { ViewOnceMessageV2 = new E2E.FutureProofMessage { Message = new E2E.Message { ImageMessage = ImageOf(image) } } },
				new ImageContent { Media = image, Metadata = new ImageMetadata(), IsViewOnce = true }
			),
			(
				new() { ViewOnceMessage = new E2E.FutureProofMessage { Message = new E2E.Message { VideoMessage = VideoOf(video) } } },
				new VideoContent { Media = video, Metadata = new VideoMetadata(), IsViewOnce = true }
			),
			(
				new() { ViewOnceMessageV2Extension = new E2E.FutureProofMessage { Message = new E2E.Message { AudioMessage = new E2E.AudioMessage(AudioOf(voice)) { PTT = true } } } },
				new AudioContent { Media = voice, Metadata = new AudioMetadata { IsVoiceNote = true }, IsViewOnce = true }
			),
			(
				new() { ImageMessage = new E2E.ImageMessage(ImageOf(image)) { ViewOnce = true } },
				new ImageContent { Media = image, Metadata = new ImageMetadata(), IsViewOnce = true }
			),
		];

		for (var i = 0; i < cases.Length; i++)
		{
			await DeliverAsync(Session, $"V{i}", cases[i].Message, ct);
			Assert.Equal(Received(Session, $"V{i}", cases[i].Content), await received.NextAsync(ct));
		}
	}

	[Fact(Timeout = Timeout)]
	public async Task Receive_AViewOnceArrivingUnavailable_IsUndecryptableMarkedUnavailableViewOnce()
	{
		var ct = TestContext.Current.CancellationToken;
		const string Session = "media-messages-unavailable";
		var client = await SessionSetup.LinkedAsync(fixture, Session, ct);
		using var received = new Received<UndecryptableMessageReceived>(client);
		await ConnectAsync(client, ct);

		foreach (var (id, type) in new[] { ("U1", "view_once"), ("U2", ""), ("U3", "a type whatsmeow does not name") })
		{
			_ = await fixture.Fake.DeliverUndecryptableMessageAsync(
				new DeliverUndecryptableMessageRequest
				{
					SessionId = Session,
					Id = id,
					Chat = s_peer.ToString(),
					Sender = s_peer.ToString(),
					MessageTime = Timestamp.FromDateTimeOffset(Deliveries.At),
					Unavailable = true,
					UnavailableType = type,
				},
				cancellationToken: ct
			);
		}

		var expected = new UndecryptableMessageReceived
		{
			SessionId = SessionId.From(Session),
			Id = MessageId.From("U1"),
			Source = Source,
			PushName = null,
			Timestamp = Deliveries.At,
			HidePlaceholder = false,
			Unavailable = UnavailableType.ViewOnce,
		};
		Assert.Equal(
			[
				expected,
				expected with { Id = MessageId.From("U2"), Unavailable = UnavailableType.Unknown },
				expected with { Id = MessageId.From("U3"), Unavailable = UnavailableType.Unknown },
			],
			await received.NextAsync(3, ct)
		);
	}

	[Fact(Timeout = Timeout)]
	public async Task Send_AMediaIdAgainOrAsText_WhatsAppGetsOneMessage()
	{
		var ct = TestContext.Current.CancellationToken;
		const string Session = "media-messages-same-id";
		var client = await SessionSetup.ConnectedAsync(fixture, Session, ct);
		var image = await UploadAsync(client, MediaType.Image, 1024, ct);
		var upload = await NextAsync(Session, 0, 1, ct);
		var id = MessageId.New();

		var first = await client.SendImageAsync(s_peer, image, new ImageMetadata { Caption = "once" }, id, cancellationToken: ct);
		var again = await client.SendImageAsync(s_peer, image, new ImageMetadata { Caption = "twice" }, id, cancellationToken: ct);
		var asText = await client.SendTextAsync(s_peer, "thrice", id, cancellationToken: ct);
		var other = await client.SendTextAsync(s_peer, "other", cancellationToken: ct);

		Assert.Equal(first, again);
		Assert.Equal(first, asText);
		var sent = await NextAsync(Session, upload[0].Index, 2, ct);
		Assert.Equal((id.Value, "once"), (sent[0].Message.Id, sent[0].Message.Message.ImageMessage.Caption));
		Assert.Equal(other.Id.Value, sent[1].Message.Id);
	}

	[Fact(Timeout = Timeout)]
	public async Task SendMedia_AnIncompleteRefOrOneOfTheWrongType_ThrowsValidation_AndNothingIsSent()
	{
		var ct = TestContext.Current.CancellationToken;
		const string Session = "media-messages-invalid";
		var client = await SessionSetup.ConnectedAsync(fixture, Session, ct);
		var document = await UploadAsync(client, MediaType.Document, 1024, ct);
		var video = await UploadAsync(client, MediaType.Video, 1024, ct);
		var audio = await UploadAsync(client, MediaType.Audio, 1024, ct);
		var uploads = await NextAsync(Session, 0, 3, ct);

		// A sticker is encrypted with the image keys (download.go:95-106): a recipient could not decrypt a document's.
		var wrongType = await Assert.ThrowsAsync<WhatsmeowValidationException>(() => client.SendStickerAsync(s_peer, document, new StickerMetadata(), cancellationToken: ct));
		var incomplete = await Assert.ThrowsAsync<WhatsmeowValidationException>(() =>
			client.SendDocumentAsync(s_peer, document with { MediaKey = [1, 2, 3] }, new DocumentMetadata(), cancellationToken: ct)
		);
		// WhatsApp carries a duration as unsigned 32-bit seconds.
		var negative = await Assert.ThrowsAsync<WhatsmeowValidationException>(() =>
			client.SendVideoAsync(s_peer, video, new VideoMetadata { Duration = TimeSpan.FromSeconds(-1) }, cancellationToken: ct)
		);
		var tooLong = await Assert.ThrowsAsync<WhatsmeowValidationException>(() =>
			client.SendAudioAsync(s_peer, audio, new AudioMetadata { Duration = TimeSpan.FromSeconds(1L << 32) }, cancellationToken: ct)
		);

		Assert.Equal(
			[WhatsmeowErrorCode.InvalidArgument, WhatsmeowErrorCode.InvalidArgument, WhatsmeowErrorCode.InvalidArgument, WhatsmeowErrorCode.InvalidArgument],
			[wrongType.ErrorCode, incomplete.ErrorCode, negative.ErrorCode, tooLong.ErrorCode]
		);
		Assert.Equal(["video.duration", "audio.duration"], [FieldOf(negative), FieldOf(tooLong)]);
		var next = await client.SendDocumentAsync(s_peer, document, new DocumentMetadata { FileName = "a.txt" }, cancellationToken: ct);
		Assert.Equal(next.Id.Value, (await NextAsync(Session, uploads[^1].Index, 1, ct))[0].Message.Id);
	}

	[Fact(Timeout = Timeout)]
	public async Task SendMedia_ABadArgument_FaultsTheReturnedTask_AsSendTextAsyncDoes_AndNothingIsSent()
	{
		var ct = TestContext.Current.CancellationToken;
		const string Session = "media-messages-arguments";
		var client = await SessionSetup.ConnectedAsync(fixture, Session, ct);
		var image = await UploadAsync(client, MediaType.Image, 1024, ct);
		var upload = await NextAsync(Session, 0, 1, ct);

		(Func<Task> Send, System.Type Exception, string Parameter)[] cases =
		[
			(() => client.SendTextAsync(s_peer, null!, cancellationToken: ct), typeof(ArgumentNullException), "text"),
			(() => client.SendImageAsync(s_peer, null!, new ImageMetadata(), cancellationToken: ct), typeof(ArgumentNullException), "image"),
			(() => client.SendVideoAsync(s_peer, image with { Type = MediaType.Video }, null!, cancellationToken: ct), typeof(ArgumentNullException), "metadata"),
			(() => client.SendAudioAsync(s_peer, image with { Type = MediaType.Audio, FileLength = -1 }, new AudioMetadata(), cancellationToken: ct), typeof(ArgumentOutOfRangeException), "audio"),
			(() => client.SendDocumentAsync(s_peer, image with { Type = (MediaType)99 }, new DocumentMetadata(), cancellationToken: ct), typeof(ArgumentOutOfRangeException), "document"),
			(() => client.SendStickerAsync(s_peer, image, new StickerMetadata { Height = -1 }, cancellationToken: ct), typeof(ArgumentOutOfRangeException), "metadata"),
		];

		foreach (var (send, exception, parameter) in cases)
		{
			var fault = Assert.IsAssignableFrom<ArgumentException>(await FaultOfAsync(send));
			Assert.Equal((exception, parameter), (fault.GetType(), fault.ParamName));
		}

		var next = await client.SendTextAsync(s_peer, "after", cancellationToken: ct);
		Assert.Equal(next.Id.Value, (await NextAsync(Session, upload[0].Index, 1, ct))[0].Message.Id);
	}

	/// <summary>What the task <paramref name="send"/> returns holds; the call itself must not throw.</summary>
	private static async Task<Exception?> FaultOfAsync(Func<Task> send)
	{
		Task? task = null;
		Assert.Null(Record.Exception(() => { task = send(); }));
		return await Record.ExceptionAsync(() => task!).ConfigureAwait(false);
	}

	/// <summary>The request field the sidecar named as invalid, from the call's ErrorInfo.</summary>
	private static string FieldOf(WhatsmeowValidationException exception) =>
		Assert.IsType<RpcException>(exception.InnerException).GetRpcStatus()!.GetDetail<ErrorInfo>().Metadata["field"];

	private static MessageSource Source => new() { Chat = s_peer, Sender = s_peer, SenderAlt = null, RecipientAlt = null, IsFromMe = false };

	private static MessageReceived Received(string session, string id, MessageContent content) =>
		MessageChecks.Received(SessionId.From(session), MessageId.From(id), Source, pushName: null, Deliveries.At, content);

	/// <summary>Connects a linked session whose handlers are registered, so its events flow to them.</summary>
	private static async Task ConnectAsync(IWhatsAppClient client, CancellationToken ct)
	{
		await client.ConnectAsync(ct).ConfigureAwait(false);
		var watch = StateWatch.Start(client, ct);
		await using (watch.ConfigureAwait(false))
			await watch.ReachesAsync(new SessionState.Connected(), new SessionState.Connecting()).ConfigureAwait(false);
	}

	private async Task DeliverAsync(string session, string id, E2E.Message message, CancellationToken ct) =>
		_ = await fixture
			.Fake.DeliverMessageAsync(
				new DeliverMessageRequest
				{
					SessionId = session,
					Id = id,
					Chat = s_peer.ToString(),
					Sender = s_peer.ToString(),
					MessageTime = Timestamp.FromDateTimeOffset(Deliveries.At),
					Message = message,
				},
				cancellationToken: ct
			)
			.ConfigureAwait(false);

	/// <summary>Uploads <paramref name="length"/> random bytes as <paramref name="type"/>, keeping them in <paramref name="contents"/>.</summary>
	private static async Task<MediaRef> UploadAsync(IWhatsAppClient client, MediaType type, int length, CancellationToken ct, Dictionary<MediaType, byte[]>? contents = null)
	{
		var content = RandomNumberGenerator.GetBytes(length);
		contents?.Add(type, content);
		using var input = new MemoryStream(content);
		return await client.UploadMediaAsync(input, type, ct).ConfigureAwait(false);
	}

	/// <summary>The session's next <paramref name="count"/> entries after the entry <paramref name="after"/>.</summary>
	private async Task<IReadOnlyList<Sent>> NextAsync(string session, ulong after, int count, CancellationToken ct)
	{
		var entries = new List<Sent>(count);
		for (var i = 0; i < count; i++)
		{
			var next = (await fixture.Fake.WaitForSentAsync(new WaitForSentRequest { SessionId = session, After = after }, cancellationToken: ct).ConfigureAwait(false)).Sent;
			entries.Add(next);
			after = next.Index;
		}

		return entries;
	}

	private static ByteString Bytes(ImmutableArray<byte> bytes) => ByteString.CopyFrom(bytes.AsSpan());

	private static E2E.ImageMessage ImageOf(MediaRef m) =>
		new()
		{
			URL = m.Url!.OriginalString,
			DirectPath = m.DirectPath,
			MediaKey = Bytes(m.MediaKey),
			FileSHA256 = Bytes(m.FileSha256),
			FileEncSHA256 = Bytes(m.FileEncSha256),
			FileLength = (ulong)m.FileLength,
		};

	private static E2E.VideoMessage VideoOf(MediaRef m) =>
		new()
		{
			URL = m.Url!.OriginalString,
			DirectPath = m.DirectPath,
			MediaKey = Bytes(m.MediaKey),
			FileSHA256 = Bytes(m.FileSha256),
			FileEncSHA256 = Bytes(m.FileEncSha256),
			FileLength = (ulong)m.FileLength,
			StreamingSidecar = Bytes(m.StreamingSidecar),
		};

	private static E2E.AudioMessage AudioOf(MediaRef m) =>
		new()
		{
			URL = m.Url!.OriginalString,
			DirectPath = m.DirectPath,
			MediaKey = Bytes(m.MediaKey),
			FileSHA256 = Bytes(m.FileSha256),
			FileEncSHA256 = Bytes(m.FileEncSha256),
			FileLength = (ulong)m.FileLength,
		};

	private static E2E.DocumentMessage DocumentOf(MediaRef m) =>
		new()
		{
			URL = m.Url!.OriginalString,
			DirectPath = m.DirectPath,
			MediaKey = Bytes(m.MediaKey),
			FileSHA256 = Bytes(m.FileSha256),
			FileEncSHA256 = Bytes(m.FileEncSha256),
			FileLength = (ulong)m.FileLength,
		};

	private static E2E.StickerMessage StickerOf(MediaRef m) =>
		new()
		{
			URL = m.Url!.OriginalString,
			DirectPath = m.DirectPath,
			MediaKey = Bytes(m.MediaKey),
			FileSHA256 = Bytes(m.FileSha256),
			FileEncSHA256 = Bytes(m.FileEncSha256),
			FileLength = (ulong)m.FileLength,
		};
}
