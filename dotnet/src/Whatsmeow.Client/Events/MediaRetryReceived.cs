namespace Whatsmeow.Events;

/// <summary>
/// The account's phone answered <see cref="IWhatsAppClient.RequestMediaRetryAsync"/>: it uploaded the message's media
/// again, or could not. It is best effort: the phone may never answer, and nothing times out the request.
/// </summary>
public sealed record MediaRetryReceived : WhatsAppEvent
{
	/// <summary>The message the request named.</summary>
	public required MessageId Id { get; init; }

	/// <summary>The message's chat.</summary>
	public required Jid Chat { get; init; }

	/// <summary>In a group chat, the message's sender; null in a direct chat.</summary>
	public required Jid? Sender { get; init; }

	/// <summary>True when the account itself sent the message.</summary>
	public required bool IsFromMe { get; init; }

	/// <summary>When the phone answered.</summary>
	public required DateTimeOffset Timestamp { get; init; }

	/// <summary>The answer.</summary>
	public required MediaRetryResult Result { get; init; }
}
