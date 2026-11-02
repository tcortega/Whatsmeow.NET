using Whatsmeow.Messaging;

namespace Whatsmeow.Events;

/// <summary>An audio message: an audio file, or a voice note (<see cref="AudioMetadata.IsVoiceNote"/>).</summary>
public sealed record AudioContent : MessageContent
{
	/// <summary>The file on WhatsApp's servers; download it with <see cref="IWhatsAppClient.DownloadMediaAsync"/>, or send it again without uploading.</summary>
	public required MediaRef Media { get; init; }

	/// <summary>What the message says about the file.</summary>
	public required AudioMetadata Metadata { get; init; }

	/// <summary>True for a view-once message, which the application should show once.</summary>
	public required bool IsViewOnce { get; init; }
}
