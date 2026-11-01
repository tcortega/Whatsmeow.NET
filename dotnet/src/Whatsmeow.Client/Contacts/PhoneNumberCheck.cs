namespace Whatsmeow.Contacts;

/// <summary>Whether one phone number is on WhatsApp, from <see cref="IWhatsAppClient.IsOnWhatsAppAsync"/>.</summary>
public sealed record PhoneNumberCheck
{
	/// <summary>The number as it was asked for.</summary>
	public required string PhoneNumber { get; init; }

	/// <summary>True when the number is on WhatsApp.</summary>
	public required bool IsOnWhatsApp { get; init; }

	/// <summary>The number's phone-number JID; null when it is not on WhatsApp.</summary>
	public required Jid? Jid { get; init; }

	/// <summary>The user's LID; null when the number is not on WhatsApp or WhatsApp sent none.</summary>
	public required Jid? Lid { get; init; }
}
