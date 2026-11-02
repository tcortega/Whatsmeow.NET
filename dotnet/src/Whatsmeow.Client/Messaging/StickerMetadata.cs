using System.Collections.Immutable;

namespace Whatsmeow.Messaging;

/// <summary>
/// What a sticker message says about its file, sent as given, leaving out what is null or empty. A sticker is uploaded
/// as <see cref="MediaType.Image"/>, usually as WebP; the library does not convert it. Its byte fields compare by
/// content.
/// </summary>
public sealed record StickerMetadata
{
	/// <summary>The file's MIME type, such as <c>image/webp</c>; null when unknown.</summary>
	public string? Mimetype { get; init; }

	/// <summary>The width in pixels; null when unknown.</summary>
	public int? Width { get; init; }

	/// <summary>The height in pixels; null when unknown.</summary>
	public int? Height { get; init; }

	/// <summary>A small PNG thumbnail, which stickers carry instead of a JPEG one; empty for none.</summary>
	public ImmutableArray<byte> PngThumbnail { get; init; } = [];

	/// <summary>Compares every field, the thumbnail by content.</summary>
	public bool Equals(StickerMetadata? other) =>
		other is not null
		&& string.Equals(Mimetype, other.Mimetype, StringComparison.Ordinal)
		&& Width == other.Width
		&& Height == other.Height
		&& PngThumbnail.AsSpan().SequenceEqual(other.PngThumbnail.AsSpan());

	/// <inheritdoc/>
	public override int GetHashCode() => HashCode.Combine(Mimetype, Width, Height);
}
