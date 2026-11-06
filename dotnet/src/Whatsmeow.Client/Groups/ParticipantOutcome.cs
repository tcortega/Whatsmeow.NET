namespace Whatsmeow.Groups;

/// <summary>Whether a change to one participant was made. Each case is a nested sealed record.</summary>
/// <remarks>
/// The library defines every case; a type derived from it elsewhere is unsupported, and the cases may become a closed
/// set (ADR 0055).
/// </remarks>
public abstract record ParticipantOutcome
{
	private protected ParticipantOutcome()
	{
	}

	/// <summary>The change was made.</summary>
	public sealed record Done : ParticipantOutcome;

	/// <summary>WhatsApp refused the change for this participant.</summary>
	public sealed record Refused : ParticipantOutcome
	{
		/// <summary>WhatsApp's code for why, HTTP-like.</summary>
		public required int Code { get; init; }

		/// <summary>
		/// The invite WhatsApp offers instead of adding the participant, as their privacy settings ask; null when it
		/// offers none.
		/// </summary>
		public required GroupAddInvite? Invite { get; init; }
	}
}
