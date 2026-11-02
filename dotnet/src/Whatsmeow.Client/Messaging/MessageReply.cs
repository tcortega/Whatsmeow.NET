namespace Whatsmeow.Messaging;

/// <summary>The message a reply quotes. The library keeps no messages, so it never checks that the message exists.</summary>
public sealed record MessageReply
{
	/// <summary>The quoted message's WhatsApp id.</summary>
	public required MessageId Id { get; init; }

	/// <summary>
	/// Who sent the quoted message (<see cref="Events.MessageSource.Sender"/>); null for the account's own message, which
	/// the sidecar names by the account's JID. Whether WhatsApp needs the sender is unknown.
	/// </summary>
	public Jid? Sender { get; init; }

	/// <summary>The text the quote shows; null for none. The recipient's phone may show its own copy of the message instead.</summary>
	public string? QuotedText { get; init; }
}
