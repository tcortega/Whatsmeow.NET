using Whatsmeow.Events;

namespace Whatsmeow.ChatState;

/// <summary>
/// A change to a chat's state that the app state sync brought: made on the phone or another device, or the echo of this
/// session's own write. These are best effort: whatsmeow acknowledges app state to WhatsApp whatever the handlers
/// return, so one the sidecar failed to record is lost (ARCHITECTURE §1). A repeat carries the same value, so applying
/// it again is harmless.
/// </summary>
public sealed record ChatStateChanged : WhatsAppEvent
{
	/// <summary>The chat, without a device.</summary>
	public required Jid Chat { get; init; }

	/// <summary>When the change was made.</summary>
	public required DateTimeOffset Timestamp { get; init; }

	/// <summary>
	/// True when the event comes from a full sync of the app state rather than from a change: once after pairing, with
	/// the state of every chat, and again when the sidecar recovers a collection that got out of step.
	/// </summary>
	public required bool FromFullSync { get; init; }

	/// <summary>What changed.</summary>
	public required ChatStateChange Change { get; init; }
}
