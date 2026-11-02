namespace Whatsmeow.Events;

/// <summary>The message a reply quotes, as the reply names it.</summary>
public sealed record QuotedMessage
{
	/// <summary>The quoted message's WhatsApp id.</summary>
	public required MessageId Id { get; init; }

	/// <summary>
	/// Who sent it (<see cref="MessageSource.Sender"/>), without a device; null for the account's own message, however the
	/// reply names the account, by phone number or LID and from any device, as for <see cref="Messaging.MessageKey.Sender"/>.
	/// A reply that does not say who sent the quoted message, or names no user, is no reply: <see cref="MessageReceived.ReplyTo"/>
	/// is null.
	/// </summary>
	public required Jid? Sender { get; init; }

	/// <summary>Its content as the reply carries it; null when WhatsApp sent none.</summary>
	public required MessageContent? Content { get; init; }
}
