using Whatsmeow.Presence;

namespace Whatsmeow.Events;

/// <summary>
/// A user's presence, which WhatsApp sends after <see cref="IWhatsAppClient.SubscribePresenceAsync"/>. Ephemeral: best
/// effort, never repeated, and dropped when more arrive than the sidecar keeps (<see cref="EphemeralEventsDropped"/>).
/// </summary>
public sealed record PresenceChanged : WhatsAppEvent
{
	/// <summary>The user.</summary>
	public required Jid From { get; init; }

	/// <summary>Online or offline.</summary>
	public required PresenceState State { get; init; }

	/// <summary>When the user was last online; null when WhatsApp sent none, as for a user whose privacy settings hide it.</summary>
	public required DateTimeOffset? LastSeen { get; init; }
}
