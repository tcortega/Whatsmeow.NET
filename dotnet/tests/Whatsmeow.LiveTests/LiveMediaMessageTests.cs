using System.Collections.Immutable;
using System.Security.Cryptography;
using Whatsmeow.Events;
using Whatsmeow.IntegrationTests.Media;
using Whatsmeow.Messaging;

namespace Whatsmeow.LiveTests;

/// <summary>
/// Media messages (#29) between the live account and the peer, through real WhatsApp, compared whole with the fake-mode
/// tests' records (<see cref="LiveChecks.Received"/>), each ref downloaded by the peer (<see cref="MediaChecks"/>).
/// </summary>
[Collection(nameof(Live))]
public sealed class LiveMediaMessageTests(LiveSidecar live, LivePeer peer)
{
	private static readonly ImmutableArray<byte> s_jpeg = [0xFF, 0xD8, 0xFF, 0xE0, 1, 2, 3];
	private static readonly ImmutableArray<byte> s_png = [0x89, (byte)'P', (byte)'N', (byte)'G', 4, 5, 6];

	/// <summary>64 samples, as WhatsApp's own voice notes carry: a rise to the middle, then a fall.</summary>
	private static readonly ImmutableArray<byte> s_waveform = [.. Enumerable.Range(0, 64).Select(i => (byte)(100 - (Math.Abs(31.5 - i) * 100 / 31.5)))];

	/// <summary>
	/// An image, a video, an audio file, a voice note, a captioned document and a sticker, each sent to the peer with the
	/// metadata of the fake-mode send test: the peer gets each as its content case, with the ref the live session
	/// uploaded and the metadata as WhatsApp carries it (a video's length in whole seconds), and downloads each ref.
	/// </summary>
	[LiveFact(NeedsPeer = true)]
	public async Task Live_SendEachMediaKindToThePeer_ItGetsEachContentCase_AndEachRefDownloads()
	{
		var ct = TestContext.Current.CancellationToken;
		var client = await live.LiveAsync(ct);
		var other = await peer.ConnectedAsync(live, ct);
		var sender = await live.AccountAsync(ct);
		var (to, _) = await peer.AccountAsync(ct);
		var contents = new Dictionary<MediaRef, byte[]>();
		var image = await UploadAsync(client, MediaType.Image, 20 * 1024, contents, ct);
		var video = await UploadAsync(client, MediaType.Video, 150 * 1024, contents, ct);
		var audio = await UploadAsync(client, MediaType.Audio, 10 * 1024, contents, ct);
		var voice = await UploadAsync(client, MediaType.Audio, 8 * 1024, contents, ct);
		var document = await UploadAsync(client, MediaType.Document, 30 * 1024, contents, ct);
		var sticker = await UploadAsync(client, MediaType.Image, 5 * 1024, contents, ct);
		var imageMetadata = new ImageMetadata { Mimetype = "image/jpeg", Caption = "look", Width = 640, Height = 480, JpegThumbnail = s_jpeg };
		var videoMetadata = new VideoMetadata { Mimetype = "video/mp4", Caption = "watch", Width = 1280, Height = 720, Duration = TimeSpan.FromSeconds(12.9), JpegThumbnail = s_jpeg };
		var audioMetadata = new AudioMetadata { Mimetype = "audio/mpeg", Duration = TimeSpan.FromSeconds(200) };
		var voiceMetadata = new AudioMetadata { Mimetype = "audio/ogg; codecs=opus", Duration = TimeSpan.FromSeconds(3), IsVoiceNote = true, Waveform = s_waveform };
		var documentMetadata = new DocumentMetadata { Mimetype = "application/pdf", FileName = "report.pdf", Caption = "the numbers", JpegThumbnail = s_jpeg };
		var stickerMetadata = new StickerMetadata { Mimetype = "image/webp", Width = 512, Height = 512, PngThumbnail = s_png };

		(Func<MessageId, Task<SendResult>> Send, MessageContent Content)[] kinds =
		[
			(id => client.SendImageAsync(to, image, imageMetadata, id, cancellationToken: ct), new ImageContent { Media = image, Metadata = imageMetadata, IsViewOnce = false }),
			(
				id => client.SendVideoAsync(to, video, videoMetadata, id, cancellationToken: ct),
				new VideoContent { Media = video, Metadata = videoMetadata with { Duration = TimeSpan.FromSeconds(12) }, IsViewOnce = false }
			),
			(id => client.SendAudioAsync(to, audio, audioMetadata, id, cancellationToken: ct), new AudioContent { Media = audio, Metadata = audioMetadata, IsViewOnce = false }),
			(id => client.SendAudioAsync(to, voice, voiceMetadata, id, cancellationToken: ct), new AudioContent { Media = voice, Metadata = voiceMetadata, IsViewOnce = false }),
			(id => client.SendDocumentAsync(to, document, documentMetadata, id, cancellationToken: ct), new DocumentContent { Media = document, Metadata = documentMetadata }),
			(id => client.SendStickerAsync(to, sticker, stickerMetadata, id, cancellationToken: ct), new StickerContent { Media = sticker, Metadata = stickerMetadata }),
		];

		foreach (var (send, content) in kinds)
		{
			var id = MessageId.New();
			var arrived = LiveChecks.ArrivalAsync(other, id, ct);
			var sent = await send(id);
			var got = await LiveChecks.WithinAsync(arrived, $"the {content.GetType().Name}");
			live.Write($"live: the peer got the {content.GetType().Name} in a {LiveChecks.Form(got.Source.Chat)} chat, stamped {(got.Timestamp - sent.Timestamp).TotalSeconds:F0} s after the send's result");
			LiveChecks.Equal(LiveChecks.Received(other, got, sender, id, content), got);
		}

		foreach (var (media, content) in contents)
			await MediaChecks.ValidateDownloadsAsync(other, media, content, ct);
	}

	/// <summary>
	/// A voice note with a 64-sample waveform, a rise then a fall, to the peer: the peer gets it as sent, and the owner
	/// says how the peer's phone draws it (RESEARCH item 13). The bytes are not audio, so the phone cannot play it; the
	/// waveform is what is asked about.
	/// </summary>
	[LiveFact(NeedsPeer = true, NeedsOwner = true)]
	public async Task Live_AVoiceNoteWithAWaveform_TheOwnerSaysHowThePeersPhoneDrawsIt()
	{
		var ct = TestContext.Current.CancellationToken;
		var client = await live.LiveAsync(ct);
		var other = await peer.ConnectedAsync(live, ct);
		var sender = await live.AccountAsync(ct);
		var (to, _) = await peer.AccountAsync(ct);
		var voice = await UploadAsync(client, MediaType.Audio, 8 * 1024, contents: null, ct);
		var metadata = new AudioMetadata { Mimetype = "audio/ogg; codecs=opus", Duration = TimeSpan.FromSeconds(5), IsVoiceNote = true, Waveform = s_waveform };
		var id = MessageId.New();

		var arrived = LiveChecks.ArrivalAsync(other, id, ct);
		_ = await client.SendAudioAsync(to, voice, metadata, id, cancellationToken: ct);
		var got = await LiveChecks.WithinAsync(arrived, "the voice note");
		LiveChecks.Equal(LiveChecks.Received(other, got, sender, id, new AudioContent { Media = voice, Metadata = metadata, IsViewOnce = false }), got);

		_ = await live.AskOwnerAsync(
			"On the peer's phone, in the chat with the live account: the last voice note, 0:05 long. Is its waveform drawn as one rise to the middle and one fall, and how many bars does it show?",
			ct
		);
	}

	/// <summary>Uploads <paramref name="length"/> random bytes as <paramref name="type"/>, keeping them in <paramref name="contents"/>.</summary>
	private static async Task<MediaRef> UploadAsync(IWhatsAppClient client, MediaType type, int length, Dictionary<MediaRef, byte[]>? contents, CancellationToken ct)
	{
		var content = RandomNumberGenerator.GetBytes(length);
		using var input = new MemoryStream(content);
		var media = await client.UploadMediaAsync(input, type, ct).ConfigureAwait(false);
		contents?.Add(media, content);
		return media;
	}
}
