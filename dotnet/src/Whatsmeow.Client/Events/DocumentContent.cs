using Whatsmeow.Messaging;

namespace Whatsmeow.Events;

/// <summary>A document message.</summary>
public sealed record DocumentContent : MessageContent
{
	/// <summary>The file on WhatsApp's servers; download it with <see cref="IWhatsAppClient.DownloadMediaAsync"/>, or send it again without uploading.</summary>
	public required MediaRef Media { get; init; }

	/// <summary>What the message says about the file.</summary>
	public required DocumentMetadata Metadata { get; init; }
}
