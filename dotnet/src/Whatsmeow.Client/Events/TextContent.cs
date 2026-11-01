namespace Whatsmeow.Events;

/// <summary>A text message.</summary>
public sealed record TextContent : MessageContent
{
	/// <summary>The text.</summary>
	public required string Text { get; init; }
}
