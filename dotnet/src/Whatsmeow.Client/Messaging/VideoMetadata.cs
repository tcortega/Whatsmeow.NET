using System.Collections.Immutable;

namespace Whatsmeow.Messaging;

/// <summary>
/// What a video message says about its file, sent as given: the library never decodes the video to check it, and
/// leaves out what is null or empty. Its byte fields compare by content.
/// </summary>
public sealed record VideoMetadata
{
	/// <summary>The file's MIME type, such as <c>video/mp4</c>; null when unknown.</summary>
	public string? MimeType { get; init; }

	/// <summary>The caption; null for none.</summary>
	public string? Caption { get; init; }

	/// <summary>The width in pixels; null when unknown.</summary>
	public int? Width { get; init; }

	/// <summary>The height in pixels; null when unknown.</summary>
	public int? Height { get; init; }

	/// <summary>The length; null when unknown. WhatsApp carries whole seconds, so a fraction is dropped.</summary>
	public TimeSpan? Duration { get; init; }

	/// <summary>A small JPEG thumbnail; empty for none, and the message goes out without one.</summary>
	public ImmutableArray<byte> JpegThumbnail { get; init; } = [];

	/// <summary>Compares every field, the thumbnail by content.</summary>
	public bool Equals(VideoMetadata? other) =>
		other is not null
		&& string.Equals(MimeType, other.MimeType, StringComparison.Ordinal)
		&& string.Equals(Caption, other.Caption, StringComparison.Ordinal)
		&& Width == other.Width
		&& Height == other.Height
		&& Duration == other.Duration
		&& JpegThumbnail.AsSpan().SequenceEqual(other.JpegThumbnail.AsSpan());

	/// <inheritdoc/>
	public override int GetHashCode() => HashCode.Combine(MimeType, Caption, Width, Height, Duration);
}
