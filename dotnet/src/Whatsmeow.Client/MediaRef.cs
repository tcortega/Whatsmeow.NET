using System.Collections.Immutable;

namespace Whatsmeow;

/// <summary>
/// An uploaded file on WhatsApp's servers: enough to download it, or to send it again without uploading, also after a
/// sidecar restart and without the message it came in. Store it as it is; its byte fields compare by content.
/// </summary>
public sealed record MediaRef
{
	/// <summary>The key type it is encrypted with.</summary>
	public required MediaType Type { get; init; }

	/// <summary>The server path it downloads from.</summary>
	public required string DirectPath { get; init; }

	/// <summary>The URL the upload returned; null when unknown, as after a media retry.</summary>
	public required Uri? Url { get; init; }

	/// <summary>The 32-byte media key.</summary>
	public required ImmutableArray<byte> MediaKey { get; init; }

	/// <summary>The SHA-256 of the plaintext.</summary>
	public required ImmutableArray<byte> FileSha256 { get; init; }

	/// <summary>The SHA-256 of what was uploaded: the ciphertext and its MAC.</summary>
	public required ImmutableArray<byte> FileEncSha256 { get; init; }

	/// <summary>The plaintext's length in bytes.</summary>
	public required long FileLength { get; init; }

	/// <summary>
	/// The streaming sidecar (a MAC per 64 KiB of what was uploaded); empty when there is none. whatsmeow makes one for a
	/// video upload, and a received video or audio may carry one.
	/// </summary>
	public required ImmutableArray<byte> StreamingSidecar { get; init; }

	/// <summary>
	/// Compares every field, the byte ones by content: a reference stored and read back equals the one it came from
	/// (<see cref="ImmutableArray{T}"/> alone compares the arrays it wraps).
	/// </summary>
	public bool Equals(MediaRef? other) =>
		other is not null
		&& Type == other.Type
		&& string.Equals(DirectPath, other.DirectPath, StringComparison.Ordinal)
		&& Equals(Url, other.Url)
		&& MediaKey.AsSpan().SequenceEqual(other.MediaKey.AsSpan())
		&& FileSha256.AsSpan().SequenceEqual(other.FileSha256.AsSpan())
		&& FileEncSha256.AsSpan().SequenceEqual(other.FileEncSha256.AsSpan())
		&& FileLength == other.FileLength
		&& StreamingSidecar.AsSpan().SequenceEqual(other.StreamingSidecar.AsSpan());

	/// <inheritdoc/>
	public override int GetHashCode()
	{
		var hash = new HashCode();
		hash.Add(Type);
		hash.Add(DirectPath, StringComparer.Ordinal);
		hash.AddBytes(FileSha256.AsSpan());
		hash.Add(FileLength);
		return hash.ToHashCode();
	}
}
