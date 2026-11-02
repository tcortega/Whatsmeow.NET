using Google.Protobuf;
using Whatsmeow.Events;
using ProtoMediaRef = Whatsmeow.Protocol.V1.MediaRef;
using ProtoMediaType = Whatsmeow.Protocol.V1.MediaType;
using ProtoRetryFailure = Whatsmeow.Protocol.V1.MediaRetryFailure;

namespace Whatsmeow.Media;

/// <summary>The media.proto messages as the public records.</summary>
internal static class MediaMapping
{
	public static MediaRef ToRef(ProtoMediaRef media) =>
		new()
		{
			Type = ToType(media.MediaType),
			DirectPath = media.DirectPath,
			Url = Uri.TryCreate(media.Url, UriKind.Absolute, out var url) ? url : null,
			MediaKey = [.. media.MediaKey.Span],
			FileSha256 = [.. media.FileSha256.Span],
			FileEncSha256 = [.. media.FileEncSha256.Span],
			FileLength = checked((long)media.FileLength),
			StreamingSidecar = [.. media.StreamingSidecar.Span],
		};

	public static ProtoMediaRef ToProto(MediaRef media) =>
		new()
		{
			MediaType = ToProto(media.Type),
			DirectPath = media.DirectPath,
			Url = media.Url?.OriginalString ?? "",
			MediaKey = ByteString.CopyFrom(media.MediaKey.AsSpan()),
			FileSha256 = ByteString.CopyFrom(media.FileSha256.AsSpan()),
			FileEncSha256 = ByteString.CopyFrom(media.FileEncSha256.AsSpan()),
			FileLength = checked((ulong)media.FileLength),
			StreamingSidecar = ByteString.CopyFrom(media.StreamingSidecar.AsSpan()),
		};

	/// <exception cref="ArgumentOutOfRangeException"><paramref name="type"/> is not a media type.</exception>
	public static ProtoMediaType ToProto(MediaType type) =>
		type switch
		{
			MediaType.Image => ProtoMediaType.Image,
			MediaType.Video => ProtoMediaType.Video,
			MediaType.Audio => ProtoMediaType.Audio,
			MediaType.Document => ProtoMediaType.Document,
			_ => throw new ArgumentOutOfRangeException(nameof(type), type, "Not a media type."),
		};

	private static MediaType ToType(ProtoMediaType type) =>
		type switch
		{
			ProtoMediaType.Image => MediaType.Image,
			ProtoMediaType.Video => MediaType.Video,
			ProtoMediaType.Audio => MediaType.Audio,
			ProtoMediaType.Document => MediaType.Document,
			ProtoMediaType.Unspecified => UnknownType(type),
			_ => UnknownType(type),
		};

	/// <exception cref="WhatsmeowValidationException">Always: the sidecar sent a type this client does not know.</exception>
	private static MediaType UnknownType(ProtoMediaType type)
	{
		WhatsmeowValidationException.ThrowInvalid($"The media type {type} is not known.");
		return default;
	}

	public static MediaRetryFailure ToFailure(ProtoRetryFailure failure) =>
		failure switch
		{
			ProtoRetryFailure.GeneralError => MediaRetryFailure.GeneralError,
			ProtoRetryFailure.NotFound => MediaRetryFailure.NotFound,
			ProtoRetryFailure.DecryptionError => MediaRetryFailure.DecryptionError,
			ProtoRetryFailure.NotOnPhone => MediaRetryFailure.NotOnPhone,
			ProtoRetryFailure.Unspecified => MediaRetryFailure.Unknown,
			_ => MediaRetryFailure.Unknown,
		};
}
