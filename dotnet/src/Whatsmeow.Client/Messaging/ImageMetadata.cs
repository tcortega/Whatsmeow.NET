using System.Collections.Immutable;

namespace Whatsmeow.Messaging;

/// <summary>
/// What an image message says about its file, sent as given: the library never decodes the image to check it, and
/// leaves out what is null or empty. Its byte fields compare by content.
/// </summary>
public sealed record ImageMetadata
{
	/// <summary>The file's MIME type, such as <c>image/jpeg</c>; null when unknown.</summary>
	public string? Mimetype { get; init; }

	/// <summary>The caption; null for none.</summary>
	public string? Caption { get; init; }

	/// <summary>The width in pixels; null when unknown.</summary>
	public int? Width { get; init; }

	/// <summary>The height in pixels; null when unknown.</summary>
	public int? Height { get; init; }

	/// <summary>A small JPEG thumbnail; empty for none, and the message goes out without one.</summary>
	public ImmutableArray<byte> JpegThumbnail { get; init; } = [];

	/// <summary>Compares every field, the thumbnail by content.</summary>
	public bool Equals(ImageMetadata? other) =>
		other is not null
		&& string.Equals(Mimetype, other.Mimetype, StringComparison.Ordinal)
		&& string.Equals(Caption, other.Caption, StringComparison.Ordinal)
		&& Width == other.Width
		&& Height == other.Height
		&& JpegThumbnail.AsSpan().SequenceEqual(other.JpegThumbnail.AsSpan());

	/// <inheritdoc/>
	public override int GetHashCode() => HashCode.Combine(Mimetype, Caption, Width, Height);
}
