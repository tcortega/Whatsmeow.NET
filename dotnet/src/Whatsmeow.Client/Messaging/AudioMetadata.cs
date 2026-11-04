using System.Collections.Immutable;

namespace Whatsmeow.Messaging;

/// <summary>
/// What an audio message says about its file, sent as given: the library never decodes the audio to check it, and
/// leaves out what is null or empty. Its byte fields compare by content.
/// </summary>
public sealed record AudioMetadata
{
	/// <summary>
	/// The file's MIME type; null when unknown. A voice note should be <c>audio/ogg; codecs=opus</c> (mono Opus): one that
	/// is not is reported to play on WhatsApp Web and stay silent on phones, which this library has not measured. The
	/// library does not check it.
	/// </summary>
	public string? MimeType { get; init; }

	/// <summary>The length; null when unknown. WhatsApp carries whole seconds, so a fraction is dropped.</summary>
	public TimeSpan? Duration { get; init; }

	/// <summary>True for a voice note (push to talk), false for an audio file.</summary>
	public bool IsVoiceNote { get; init; }

	/// <summary>
	/// A voice note's waveform, sent unchanged; empty for none. Which range phones expect (0-100, 0-127 or 0-255) is
	/// not known; 64 values peaking near 100 is the common choice.
	/// </summary>
	public ImmutableArray<byte> Waveform { get; init; } = [];

	/// <summary>Compares every field, the waveform by content.</summary>
	public bool Equals(AudioMetadata? other) =>
		other is not null
		&& string.Equals(MimeType, other.MimeType, StringComparison.Ordinal)
		&& Duration == other.Duration
		&& IsVoiceNote == other.IsVoiceNote
		&& Waveform.AsSpan().SequenceEqual(other.Waveform.AsSpan());

	/// <inheritdoc/>
	public override int GetHashCode() => HashCode.Combine(MimeType, Duration, IsVoiceNote);
}
