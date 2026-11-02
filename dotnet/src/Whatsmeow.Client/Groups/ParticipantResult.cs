namespace Whatsmeow.Groups;

/// <summary>What WhatsApp answered for one participant of a group change, from <see cref="IWhatsAppClient.CreateGroupAsync"/> or <see cref="IWhatsAppClient.AddParticipantsAsync"/> and its siblings.</summary>
public sealed record ParticipantResult
{
	/// <summary>The participant, as WhatsApp answered.</summary>
	public required Jid Jid { get; init; }

	/// <summary>The participant's other address form, when WhatsApp sent it.</summary>
	public required Jid? JidAlt { get; init; }

	/// <summary>Whether the change was made, and if not, why.</summary>
	public required ParticipantOutcome Outcome { get; init; }
}
