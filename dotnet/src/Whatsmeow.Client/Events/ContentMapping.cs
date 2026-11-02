using Google.Protobuf;
using Whatsmeow.Messaging;
using E2E = Whatsmeow.Proto.WAWebProtobufsE2E;

namespace Whatsmeow.Events;

/// <summary>
/// A received message as its <see cref="MessageContent"/> case. It maps field by field and never throws: a field the
/// message lacks is unknown (null, or empty bytes), so the content still arrives.
/// </summary>
internal static class ContentMapping
{
	/// <param name="message">The message as whatsmeow unwrapped it.</param>
	/// <param name="viewOnce">True when whatsmeow unwrapped it from a view-once wrapper.</param>
	public static MessageContent ToContent(E2E.Message? message, bool viewOnce) =>
		message switch
		{
			{ HasConversation: true } => new TextContent { Text = message.Conversation },
			{ ExtendedTextMessage.HasText: true } => new TextContent { Text = message.ExtendedTextMessage.Text },
			{ ImageMessage: { } image } => ToImage(image, viewOnce),
			{ VideoMessage: { } video } => ToVideo(video, viewOnce),
			{ AudioMessage: { } audio } => ToAudio(audio, viewOnce),
			{ DocumentMessage: { } document } => ToDocument(document),
			{ StickerMessage: { } sticker } => ToSticker(sticker),
			_ => new UnknownContent(),
		};

	// A view-once message says so in its wrapper, which whatsmeow removed, or in its own flag; mautrix reads both
	// (pkg/connector/events.go:131-132, pkg/msgconv/wa-media.go:52-53).
	private static ImageContent ToImage(E2E.ImageMessage image, bool viewOnce) =>
		new()
		{
			Media = ToRef(MediaType.Image, image.URL, image.DirectPath, image.MediaKey, image.FileSHA256, image.FileEncSHA256, image.FileLength, ByteString.Empty),
			Metadata = new ImageMetadata
			{
				Mimetype = NullIfEmpty(image.Mimetype),
				Caption = NullIfEmpty(image.Caption),
				Width = image.HasWidth ? ToPixels(image.Width) : null,
				Height = image.HasHeight ? ToPixels(image.Height) : null,
				JpegThumbnail = [.. image.JPEGThumbnail.Span],
			},
			IsViewOnce = viewOnce || image.ViewOnce,
		};

	private static VideoContent ToVideo(E2E.VideoMessage video, bool viewOnce) =>
		new()
		{
			Media = ToRef(MediaType.Video, video.URL, video.DirectPath, video.MediaKey, video.FileSHA256, video.FileEncSHA256, video.FileLength, video.StreamingSidecar),
			Metadata = new VideoMetadata
			{
				Mimetype = NullIfEmpty(video.Mimetype),
				Caption = NullIfEmpty(video.Caption),
				Width = video.HasWidth ? ToPixels(video.Width) : null,
				Height = video.HasHeight ? ToPixels(video.Height) : null,
				Duration = video.HasSeconds ? TimeSpan.FromSeconds(video.Seconds) : null,
				JpegThumbnail = [.. video.JPEGThumbnail.Span],
			},
			IsViewOnce = viewOnce || video.ViewOnce,
		};

	private static AudioContent ToAudio(E2E.AudioMessage audio, bool viewOnce) =>
		new()
		{
			Media = ToRef(MediaType.Audio, audio.URL, audio.DirectPath, audio.MediaKey, audio.FileSHA256, audio.FileEncSHA256, audio.FileLength, audio.StreamingSidecar),
			Metadata = new AudioMetadata
			{
				Mimetype = NullIfEmpty(audio.Mimetype),
				Duration = audio.HasSeconds ? TimeSpan.FromSeconds(audio.Seconds) : null,
				IsVoiceNote = audio.PTT,
				Waveform = [.. audio.Waveform.Span],
			},
			IsViewOnce = viewOnce || audio.ViewOnce,
		};

	private static DocumentContent ToDocument(E2E.DocumentMessage document) =>
		new()
		{
			Media = ToRef(
				MediaType.Document,
				document.URL,
				document.DirectPath,
				document.MediaKey,
				document.FileSHA256,
				document.FileEncSHA256,
				document.FileLength,
				ByteString.Empty
			),
			Metadata = new DocumentMetadata
			{
				Mimetype = NullIfEmpty(document.Mimetype),
				FileName = NullIfEmpty(document.FileName),
				Caption = NullIfEmpty(document.Caption),
				JpegThumbnail = [.. document.JPEGThumbnail.Span],
			},
		};

	// A sticker is encrypted with the image keys (download.go:95-106).
	private static StickerContent ToSticker(E2E.StickerMessage sticker) =>
		new()
		{
			Media = ToRef(MediaType.Image, sticker.URL, sticker.DirectPath, sticker.MediaKey, sticker.FileSHA256, sticker.FileEncSHA256, sticker.FileLength, ByteString.Empty),
			Metadata = new StickerMetadata
			{
				Mimetype = NullIfEmpty(sticker.Mimetype),
				Width = sticker.HasWidth ? ToPixels(sticker.Width) : null,
				Height = sticker.HasHeight ? ToPixels(sticker.Height) : null,
				PngThumbnail = [.. sticker.PngThumbnail.Span],
			},
		};

	private static MediaRef ToRef(
		MediaType type,
		string url,
		string directPath,
		ByteString mediaKey,
		ByteString fileSha256,
		ByteString fileEncSha256,
		ulong fileLength,
		ByteString streamingSidecar
	) =>
		new()
		{
			Type = type,
			DirectPath = directPath,
			Url = Uri.TryCreate(url, UriKind.Absolute, out var parsed) ? parsed : null,
			MediaKey = [.. mediaKey.Span],
			FileSha256 = [.. fileSha256.Span],
			FileEncSha256 = [.. fileEncSha256.Span],
			FileLength = (long)Math.Min(fileLength, long.MaxValue),
			StreamingSidecar = [.. streamingSidecar.Span],
		};

	/// <summary>Pixels as an <see cref="int"/>, clamped: WhatsApp's are unsigned.</summary>
	private static int ToPixels(uint pixels) => (int)Math.Min(pixels, int.MaxValue);

	private static string? NullIfEmpty(string value) => value.Length == 0 ? null : value;
}
