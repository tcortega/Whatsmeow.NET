using Whatsmeow.Messaging;

namespace Whatsmeow.Events;

/// <summary>An edit: the sender replaced the content of a message they sent.</summary>
public sealed record EditContent : MessageContent
{
	/// <summary>The message edited; it may be one the application never saw.</summary>
	public required MessageKey Target { get; init; }

	/// <summary>Its new content.</summary>
	public required MessageContent Content { get; init; }
}
