namespace Whatsmeow.Sessions;

/// <summary>A session and its state, as <see cref="WhatsmeowSessions.ListAsync"/> reports it.</summary>
public sealed record SessionInfo
{
	/// <summary>The session's id.</summary>
	public required SessionId Id { get; init; }

	/// <summary>The session's state.</summary>
	public required SessionState State { get; init; }
}
