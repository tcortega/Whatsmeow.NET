namespace Whatsmeow.Events;

/// <summary>
/// A message that failed to decrypt. whatsmeow asks the sender and the account's phone for it again; a copy that
/// decrypts comes later as a <see cref="MessageReceived"/> with the same <see cref="Id"/>, so this is the moment to
/// show "waiting for this message".
/// </summary>
public sealed record UndecryptableMessageReceived : WhatsAppEvent
{
	/// <summary>The WhatsApp message id.</summary>
	public required MessageId Id { get; init; }

	/// <summary>Who and where.</summary>
	public required MessageSource Source { get; init; }

	/// <summary>The sender's push name; null when WhatsApp sent none.</summary>
	public required string? PushName { get; init; }

	/// <summary>The message's WhatsApp timestamp.</summary>
	public required DateTimeOffset Timestamp { get; init; }

	/// <summary>True when the sender asked not to show a "waiting for this message" placeholder.</summary>
	public required bool HidePlaceholder { get; init; }
}
