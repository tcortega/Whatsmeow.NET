using System.Collections.Immutable;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Whatsmeow.Media;
using Whatsmeow.Protocol.V1;

namespace Whatsmeow.Messaging;

/// <summary>The media sends as message.proto's <see cref="SendMediaRequest"/>.</summary>
internal static class MediaMessageMapping
{
	/// <param name="paramName">The send's name for <paramref name="media"/>, which an exception names.</param>
	/// <exception cref="ArgumentOutOfRangeException">The length is negative, or the type is not a <see cref="MediaType"/>.</exception>
	public static SendMediaRequest ToRequest(SessionId sessionId, Jid chat, MessageId id, MediaRef media, string paramName)
	{
		ArgumentOutOfRangeException.ThrowIfNegative(media.FileLength, paramName);
		if (media.Type is not (MediaType.Image or MediaType.Video or MediaType.Audio or MediaType.Document))
			throw new ArgumentOutOfRangeException(paramName, media.Type, "Not a media type.");
		return new()
		{
			SessionId = sessionId.Value,
			Chat = chat.ToString(),
			ClientMessageId = id.Value,
			Media = MediaMapping.ToProto(media),
		};
	}

	/// <exception cref="ArgumentOutOfRangeException">The width or the height is negative.</exception>
	public static Protocol.V1.ImageMetadata ToProto(ImageMetadata metadata)
	{
		var proto = new Protocol.V1.ImageMetadata
		{
			Mimetype = metadata.MimeType ?? "",
			Caption = metadata.Caption ?? "",
			JpegThumbnail = ToBytes(metadata.JpegThumbnail),
		};
		if (ToPixels(metadata.Width, nameof(metadata)) is { } width)
			proto.Width = width;
		if (ToPixels(metadata.Height, nameof(metadata)) is { } height)
			proto.Height = height;
		return proto;
	}

	/// <exception cref="ArgumentOutOfRangeException">The width or the height is negative.</exception>
	public static Protocol.V1.VideoMetadata ToProto(VideoMetadata metadata)
	{
		var proto = new Protocol.V1.VideoMetadata
		{
			Mimetype = metadata.MimeType ?? "",
			Caption = metadata.Caption ?? "",
			Duration = ToDuration(metadata.Duration),
			JpegThumbnail = ToBytes(metadata.JpegThumbnail),
		};
		if (ToPixels(metadata.Width, nameof(metadata)) is { } width)
			proto.Width = width;
		if (ToPixels(metadata.Height, nameof(metadata)) is { } height)
			proto.Height = height;
		return proto;
	}

	public static Protocol.V1.AudioMetadata ToProto(AudioMetadata metadata) =>
		new()
		{
			Mimetype = metadata.MimeType ?? "",
			Duration = ToDuration(metadata.Duration),
			VoiceNote = metadata.IsVoiceNote,
			Waveform = ToBytes(metadata.Waveform),
		};

	public static Protocol.V1.DocumentMetadata ToProto(DocumentMetadata metadata) =>
		new()
		{
			Mimetype = metadata.MimeType ?? "",
			FileName = metadata.FileName ?? "",
			Caption = metadata.Caption ?? "",
			JpegThumbnail = ToBytes(metadata.JpegThumbnail),
		};

	/// <exception cref="ArgumentOutOfRangeException">The width or the height is negative.</exception>
	public static Protocol.V1.StickerMetadata ToProto(StickerMetadata metadata)
	{
		var proto = new Protocol.V1.StickerMetadata
		{
			Mimetype = metadata.MimeType ?? "",
			PngThumbnail = ToBytes(metadata.PngThumbnail),
		};
		if (ToPixels(metadata.Width, nameof(metadata)) is { } width)
			proto.Width = width;
		if (ToPixels(metadata.Height, nameof(metadata)) is { } height)
			proto.Height = height;
		return proto;
	}

	/// <exception cref="ArgumentOutOfRangeException"><paramref name="pixels"/> is negative.</exception>
	private static uint? ToPixels(int? pixels, string paramName)
	{
		if (pixels is not { } value)
			return null;
		ArgumentOutOfRangeException.ThrowIfNegative(value, paramName);
		return (uint)value;
	}

	private static Duration? ToDuration(TimeSpan? duration) => duration is { } value ? Duration.FromTimeSpan(value) : null;

	private static ByteString ToBytes(ImmutableArray<byte> bytes) => ByteString.CopyFrom(bytes.AsSpan());
}
