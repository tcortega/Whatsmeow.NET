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
	/// Who sent the message (<see cref="Events.MessageSource.Sender"/>); null for the account's own message. The
	/// account's own JID names its message too. In a group, a message from someone else needs its sender.
	/// </summary>
	public Jid? Sender { get; init; }
}
