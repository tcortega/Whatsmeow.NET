namespace Whatsmeow.Contacts;

/// <summary>A JID and its other address form, from <see cref="IWhatsAppClient.ResolveJidsAsync"/>.</summary>
public sealed record ResolvedJid
{
	/// <summary>The JID as it was asked for.</summary>
	public required Jid Jid { get; init; }

	/// <summary>
	/// The other form, the LID of a phone-number JID or the phone-number JID of a LID, with the same device; null when
	/// it is not known. Some LIDs never resolve to a phone number.
	/// </summary>
	public required Jid? Alt { get; init; }
}
