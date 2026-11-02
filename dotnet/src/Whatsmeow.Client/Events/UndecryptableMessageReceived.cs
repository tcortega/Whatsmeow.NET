namespace Whatsmeow.Events;

/// <summary>
/// A message that failed to decrypt. whatsmeow asks the sender and the account's phone for it again; a copy that
/// decrypts comes later as a <see cref="MessageReceived"/> with the same <see cref="Id"/>, so this is the moment to
/// show "waiting for this message". A view-once message can come as content with <see cref="ImageContent.IsViewOnce"/>,
/// or as one that is <see cref="Unavailable"/> with <see cref="UnavailableType.ViewOnce"/>. whatsmeow asks the phone for
/// an unavailable one too, but whether the phone answers for a view-once is not known yet (#36 checks it live), so a
/// copy may or may not come; showing that it can be opened on the phone fits either.
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

	/// <summary>
	/// Null when a ciphertext came for this device and did not decrypt. Otherwise WhatsApp sent this device none, and
	/// this is why. Such a message is best effort: whatsmeow acknowledges it whatever the handler does.
	/// </summary>
	public UnavailableType? Unavailable { get; init; }
}
