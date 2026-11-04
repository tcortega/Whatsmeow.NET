namespace Whatsmeow.Events;

/// <summary>
/// An event of one session, from <see cref="IWhatsAppClient.On{TEvent}"/> or <see cref="IWhatsAppClient.WaitForAsync{TEvent}"/>.
/// Delivery is at least once: after a crash of either process an event can come again, with the same content, so
/// handlers must be idempotent; <see cref="SessionId"/> plus the message id is the dedupe key (ARCHITECTURE §8.4).
/// </summary>
/// <remarks>
/// The library defines every case; a type derived from it elsewhere is unsupported, and the cases may become a closed
/// set (ADR 0055).
/// </remarks>
public abstract record WhatsAppEvent
{
	private protected WhatsAppEvent()
	{
	}

	/// <summary>The session the event belongs to.</summary>
	public required SessionId SessionId { get; init; }
}
