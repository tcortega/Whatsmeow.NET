namespace Whatsmeow.Events;

/// <summary>A handler threw while handling an event; the event still counts as handled.</summary>
public sealed record HandlerError
{
	/// <summary>The event.</summary>
	public required WhatsAppEvent Event { get; init; }

	/// <summary>What the handler threw.</summary>
	public required Exception Exception { get; init; }
}
