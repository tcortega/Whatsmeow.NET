using Whatsmeow.Messaging;

namespace Whatsmeow.Events;

/// <summary>A video message.</summary>
public sealed record VideoContent : MessageContent
{
	/// <summary>The file on WhatsApp's servers; download it with <see cref="IWhatsAppClient.DownloadMediaAsync"/>, or send it again without uploading.</summary>
	public required MediaRef Media { get; init; }

	/// <summary>What the message says about the file.</summary>
	public required VideoMetadata Metadata { get; init; }

	/// <summary>True for a view-once message, which the application should show once.</summary>
	public required bool IsViewOnce { get; init; }
}
