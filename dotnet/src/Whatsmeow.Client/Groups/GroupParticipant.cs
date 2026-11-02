namespace Whatsmeow.Groups;

/// <summary>One participant of a group.</summary>
public sealed record GroupParticipant
{
	/// <summary>The participant: their LID in a LID-addressed group, else their phone number.</summary>
	public required Jid Jid { get; init; }

	/// <summary>The participant's other address form, when WhatsApp sent it.</summary>
	public required Jid? JidAlt { get; init; }

	/// <summary>The participant's role.</summary>
	public required GroupRole Role { get; init; }
}
