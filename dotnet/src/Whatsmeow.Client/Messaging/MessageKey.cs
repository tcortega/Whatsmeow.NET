namespace Whatsmeow.Messaging;

/// <summary>
/// A message another one acts on: the one a reaction, an edit or a revoke targets. The library keeps no messages, so
/// it never checks that the message exists.
/// </summary>
public sealed record MessageKey
{
	/// <summary>The message's WhatsApp id.</summary>
	public required MessageId Id { get; init; }

	/// <summary>
	/// Who sent the message (<see cref="Events.MessageSource.Sender"/>), without a device; null for the account's own
	/// message. In a send, the account's own JID names its message too, and in a group a message from someone else needs
	/// its sender. In a received reaction, edit or revoke it is never the account's own JID: the account's message is
	/// always null, as a received quote's <see cref="Events.QuotedMessage.Sender"/> is.
	/// </summary>
	public Jid? Sender { get; init; }
}
