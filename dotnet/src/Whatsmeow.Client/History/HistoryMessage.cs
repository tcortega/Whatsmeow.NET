using Whatsmeow.Events;

namespace Whatsmeow.History;

/// <summary>
/// One message of the account's history, as a <see cref="HistoryReceived"/> batch carries it: an old message, never a
/// live one, with what a <see cref="MessageReceived"/> has. Its <see cref="Id"/> and the event's
/// <see cref="WhatsAppEvent.SessionId"/> are the dedupe key, against a batch that comes again and against the same
/// message received live.
/// </summary>
public sealed record HistoryMessage
{
	/// <summary>The WhatsApp message id.</summary>
	public required MessageId Id { get; init; }

	/// <summary>Who and where.</summary>
	public required MessageSource Source { get; init; }

	/// <summary>The sender's push name; null when WhatsApp sent none.</summary>
	public required string? PushName { get; init; }

	/// <summary>The message's WhatsApp timestamp.</summary>
	public required DateTimeOffset Timestamp { get; init; }

	/// <summary>What it says, mapped as a live message's content is.</summary>
	public required MessageContent Content { get; init; }
}
