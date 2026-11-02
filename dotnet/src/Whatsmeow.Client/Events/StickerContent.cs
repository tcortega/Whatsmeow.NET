using Whatsmeow.Messaging;

namespace Whatsmeow.Events;

/// <summary>A sticker message.</summary>
public sealed record StickerContent : MessageContent
{
	/// <summary>The file on WhatsApp's servers; download it with <see cref="IWhatsAppClient.DownloadMediaAsync"/>, or send it again without uploading.</summary>
	public required MediaRef Media { get; init; }

	/// <summary>What the message says about the file.</summary>
	public required StickerMetadata Metadata { get; init; }
}
