using Whatsmeow.Sessions;

namespace Whatsmeow.Events;

/// <summary>A session state transition, in order with the session's other events. Every transition is one of these.</summary>
public sealed record ConnectionStateChanged : WhatsAppEvent
{
	/// <summary>The new state.</summary>
	public required SessionState State { get; init; }
}
