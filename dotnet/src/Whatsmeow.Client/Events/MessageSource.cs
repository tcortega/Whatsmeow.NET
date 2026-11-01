namespace Whatsmeow.Events;

/// <summary>Who a message is from and in which chat.</summary>
public sealed record MessageSource
{
	/// <summary>The chat: the other user in a direct chat, or the group.</summary>
	public required Jid Chat { get; init; }

	/// <summary>The sender.</summary>
	public required Jid Sender { get; init; }

	/// <summary>The sender's other address form (phone number or LID), when WhatsApp sent it.</summary>
	public required Jid? SenderAlt { get; init; }

	/// <summary>In a direct chat, the recipient's other address form, when WhatsApp sent it.</summary>
	public required Jid? RecipientAlt { get; init; }

	/// <summary>True when the account itself sent it, from another of its devices.</summary>
	public required bool IsFromMe { get; init; }
}
