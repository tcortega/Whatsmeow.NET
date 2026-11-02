namespace Whatsmeow.Events;

/// <summary>
/// Ephemeral events (<see cref="PresenceChanged"/>, <see cref="ChatPresenceChanged"/>) this client did not get. The
/// sidecar keeps each session's newest 256 in memory, also while this client is not connected; when more arrive
/// before the client gets them, because its handlers fell behind or it was not connected, the oldest are dropped, and
/// this says how many.
/// </summary>
public sealed record EphemeralEventsDropped : WhatsAppEvent
{
	/// <summary>How many were dropped since the last ephemeral event.</summary>
	public required long Count { get; init; }
}
