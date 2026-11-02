using Whatsmeow.Presence;

namespace Whatsmeow.Events;

/// <summary>
/// What a user is doing in a chat: typing, recording audio, or neither any more. Ephemeral: best effort, never
/// repeated, and dropped when more arrive than the sidecar keeps (<see cref="EphemeralEventsDropped"/>).
/// </summary>
public sealed record ChatPresenceChanged : WhatsAppEvent
{
	/// <summary>Who and where.</summary>
	public required MessageSource Source { get; init; }

	/// <summary>The state.</summary>
	public required ChatPresenceState State { get; init; }
}
