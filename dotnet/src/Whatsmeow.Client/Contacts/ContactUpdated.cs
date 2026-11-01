using Whatsmeow.Events;

namespace Whatsmeow.Contacts;

/// <summary>
/// A change to a user's profile, or to a group's picture, that WhatsApp told the session about. These are best effort:
/// whatsmeow acknowledges them to WhatsApp whatever the handlers return, so one the sidecar failed to record is lost
/// (ARCHITECTURE §1). A repeat carries the same value, so applying it again is harmless.
/// </summary>
public sealed record ContactUpdated : WhatsAppEvent
{
	/// <summary>The user, or for a picture also a group, without a device.</summary>
	public required Jid Jid { get; init; }

	/// <summary>The user's other address form, when known.</summary>
	public required Jid? JidAlt { get; init; }

	/// <summary>What changed.</summary>
	public required ContactChange Change { get; init; }
}
