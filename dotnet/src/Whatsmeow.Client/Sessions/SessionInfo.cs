namespace Whatsmeow.Sessions;

/// <summary>A session and its state, as <see cref="WhatsmeowSessions.ListAsync"/> reports it.</summary>
public sealed record SessionInfo
{
	/// <summary>The session's id.</summary>
	public required SessionId Id { get; init; }

	/// <summary>The session's state.</summary>
	public required SessionState State { get; init; }

	/// <summary>
	/// This device's JID, <c>user:device@s.whatsapp.net</c>, as WhatsApp assigned it at pairing; null while the session
	/// is not paired. The account's own chat is this JID with <see cref="Jid.Device"/> 0.
	/// </summary>
	public Jid? Jid { get; init; }

	/// <summary>This device's LID, <c>user:device@lid</c>; null while the session is not paired, or when WhatsApp sent none.</summary>
	public Jid? Lid { get; init; }
}
