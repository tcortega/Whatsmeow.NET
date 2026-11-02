namespace Whatsmeow;

/// <summary>The kind of key a file is encrypted with on WhatsApp's servers.</summary>
public enum MediaType
{
	/// <summary>An image, and a sticker.</summary>
	Image,

	/// <summary>A video.</summary>
	Video,

	/// <summary>Audio, voice notes included.</summary>
	Audio,

	/// <summary>A document.</summary>
	Document,
}
