namespace Whatsmeow.Events;

/// <summary>The message a reply quotes, as the reply names it.</summary>
public sealed record QuotedMessage
{
	/// <summary>The quoted message's WhatsApp id.</summary>
	public required MessageId Id { get; init; }

	/// <summary>Who sent it; null when the reply does not say.</summary>
	public required Jid? Sender { get; init; }

	/// <summary>Its content as the reply carries it; null when WhatsApp sent none.</summary>
	public required MessageContent? Content { get; init; }
}
