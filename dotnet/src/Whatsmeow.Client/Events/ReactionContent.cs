using Whatsmeow.Messaging;

namespace Whatsmeow.Events;

/// <summary>A reaction to a message, or the removal of one.</summary>
public sealed record ReactionContent : MessageContent
{
	/// <summary>The message reacted to; it may be one the application never saw.</summary>
	public required MessageKey Target { get; init; }

	/// <summary>The emoji; empty when the sender removed their reaction.</summary>
	public required string Emoji { get; init; }
}
