namespace Whatsmeow.Contacts;

/// <summary>One user the account blocked, from <see cref="IWhatsAppClient.GetBlocklistAsync"/>.</summary>
public sealed record BlockedUser
{
	/// <summary>The user, as WhatsApp lists it (usually the LID).</summary>
	public required Jid Jid { get; init; }

	/// <summary>The user's other address form; null when WhatsApp sent none.</summary>
	public required Jid? JidAlt { get; init; }
}
