namespace Whatsmeow.Contacts;

/// <summary>
/// One of the session's contacts as WhatsApp synced it, from <see cref="IWhatsAppClient.GetContactsAsync"/>. A null name
/// is one WhatsApp did not sync.
/// </summary>
public sealed record Contact
{
	/// <summary>The contact's JID; one person can be listed under both address forms.</summary>
	public required Jid Jid { get; init; }

	/// <summary>The name the account saved for the contact (the address book's full name).</summary>
	public required string? SavedName { get; init; }

	/// <summary>The name the contact set for themselves.</summary>
	public required string? PushName { get; init; }

	/// <summary>The contact's verified business name.</summary>
	public required string? BusinessName { get; init; }
}
