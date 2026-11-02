namespace Whatsmeow.Messaging;

/// <summary>A message WhatsApp accepted.</summary>
public sealed record SendResult
{
	/// <summary>The message's id: the one given, or the one the client made.</summary>
	public required MessageId Id { get; init; }

	/// <summary>
	/// The chat it went to. A message to a phone number goes to that account's LID (<c>user@lid</c>), as whatsmeow sends
	/// it; a LID or a group is the chat given.
	/// </summary>
	public required Jid Chat { get; init; }

	/// <summary>The message's WhatsApp timestamp, in whole seconds.</summary>
	public required DateTimeOffset Timestamp { get; init; }
}
