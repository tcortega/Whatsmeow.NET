using Whatsmeow.Messaging;

namespace Whatsmeow.Events;

/// <summary>
/// A message deleted for everyone. Who deleted it is the event's <see cref="MessageReceived.Source"/>: the message's
/// own sender, or a group admin when <see cref="MessageKey.Sender"/> names someone else.
/// </summary>
public sealed record RevokeContent : MessageContent
{
	/// <summary>The message deleted; it may be one the application never saw.</summary>
	public required MessageKey Target { get; init; }
}
