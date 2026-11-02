using Whatsmeow.Events;

namespace Whatsmeow.Groups;

/// <summary>
/// The session joined a group, by an invite link, or was added to one. Like <see cref="GroupInfoChanged"/>, it is at
/// least once and can come again.
/// </summary>
public sealed record GroupJoined : WhatsAppEvent
{
	/// <summary>The group as WhatsApp sent it.</summary>
	public required GroupInfo Group { get; init; }

	/// <summary>Who added the session; null when WhatsApp did not say.</summary>
	public required Jid? Sender { get; init; }

	/// <summary>The sender's phone number when <see cref="Sender"/> is a LID and WhatsApp sent it.</summary>
	public required Jid? SenderAlt { get; init; }
}
