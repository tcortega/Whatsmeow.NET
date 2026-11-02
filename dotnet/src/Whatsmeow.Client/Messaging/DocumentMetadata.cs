using System.Collections.Immutable;

namespace Whatsmeow.Messaging;

/// <summary>
/// What a document message says about its file, sent as given, leaving out what is null or empty. Its byte fields
/// compare by content.
/// </summary>
public sealed record DocumentMetadata
{
	/// <summary>The file's MIME type, such as <c>application/pdf</c>; null when unknown.</summary>
	public string? Mimetype { get; init; }

	/// <summary>The file name the recipient sees; null for none.</summary>
	public string? FileName { get; init; }

	/// <summary>The caption; null for none. The recipient sees it with the file.</summary>
	public string? Caption { get; init; }

	/// <summary>A small JPEG thumbnail; empty for none, and the message goes out without one.</summary>
	public ImmutableArray<byte> JpegThumbnail { get; init; } = [];

	/// <summary>Compares every field, the thumbnail by content.</summary>
	public bool Equals(DocumentMetadata? other) =>
		other is not null
		&& string.Equals(Mimetype, other.Mimetype, StringComparison.Ordinal)
		&& string.Equals(FileName, other.FileName, StringComparison.Ordinal)
		&& string.Equals(Caption, other.Caption, StringComparison.Ordinal)
		&& JpegThumbnail.AsSpan().SequenceEqual(other.JpegThumbnail.AsSpan());

	/// <inheritdoc/>
	public override int GetHashCode() => HashCode.Combine(Mimetype, FileName, Caption);
}
