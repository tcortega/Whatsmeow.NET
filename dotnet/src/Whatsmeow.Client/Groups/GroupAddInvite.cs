namespace Whatsmeow.Groups;

/// <summary>The invite WhatsApp offers for a participant it would not add directly.</summary>
public sealed record GroupAddInvite
{
	/// <summary>The invite's code.</summary>
	public required string Code { get; init; }

	/// <summary>When the invite expires.</summary>
	public required DateTimeOffset ExpiresAt { get; init; }
}
