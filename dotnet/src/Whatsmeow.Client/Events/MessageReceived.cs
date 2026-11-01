namespace Whatsmeow.Events;

/// <summary>A message received, or sent from another of the account's devices.</summary>
public sealed record MessageReceived : WhatsAppEvent
{
	/// <summary>The WhatsApp message id; with <see cref="WhatsAppEvent.SessionId"/>, the dedupe key.</summary>
	public required MessageId Id { get; init; }

	/// <summary>Who and where.</summary>
	public required MessageSource Source { get; init; }

	/// <summary>The sender's push name; null when WhatsApp sent none.</summary>
	public required string? PushName { get; init; }

	/// <summary>The message's WhatsApp timestamp.</summary>
	public required DateTimeOffset Timestamp { get; init; }

	/// <summary>What it says.</summary>
	public required MessageContent Content { get; init; }
}
