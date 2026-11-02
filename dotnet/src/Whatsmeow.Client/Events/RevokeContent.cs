using Whatsmeow.Messaging;

namespace Whatsmeow.Events;

/// <summary>
/// A message deleted for everyone. Who deleted it is the event's <see cref="MessageReceived.Source"/>: the message's
/// own sender, or a group admin when the target's <see cref="MessageKey.Sender"/> is another user. The target's sender
/// has no device, while the source's sender may name one, so compare their <see cref="Jid.User"/> and
/// <see cref="Jid.Server"/>; a null target sender is the account's own message, whoever deleted it. Whether a key names
/// a sender by the same address form, phone number or LID, as the source does is unknown.
/// </summary>
public sealed record RevokeContent : MessageContent
{
	/// <summary>The message deleted; it may be one the application never saw.</summary>
	public required MessageKey Target { get; init; }
}
