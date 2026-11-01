namespace Whatsmeow.Events;

internal static partial class EventLog
{
	[LoggerMessage(30, LogLevel.Error, "A handler of a {EventType} of session {SessionId} threw; the event counts as handled")]
	public static partial void HandlerFailed(ILogger logger, Exception exception, string eventType, string sessionId);

	[LoggerMessage(31, LogLevel.Error, "An OnHandlerError callback of session {SessionId} threw")]
	public static partial void ErrorCallbackFailed(ILogger logger, Exception exception, string sessionId);

	[LoggerMessage(32, LogLevel.Error, "Event {Seq} of session {SessionId} cannot be read; it is skipped and acked")]
	public static partial void EventUnreadable(ILogger logger, Exception exception, ulong seq, string sessionId);

	[LoggerMessage(33, LogLevel.Error, "The event stream of session {SessionId} ended for good")]
	public static partial void StreamEnded(ILogger logger, Exception exception, string sessionId);

	[LoggerMessage(34, LogLevel.Warning, "The event stream of session {SessionId} failed; it is opened again in {Delay}")]
	public static partial void StreamFailed(ILogger logger, Exception exception, string sessionId, TimeSpan delay);

	[LoggerMessage(35, LogLevel.Debug, "Acked the events of session {SessionId} up to {Seq}")]
	public static partial void Acked(ILogger logger, string sessionId, ulong seq);

	[LoggerMessage(36, LogLevel.Warning, "An ephemeral {PayloadCase} event of session {SessionId} this client cannot read is skipped")]
	public static partial void EphemeralEventSkipped(ILogger logger, string payloadCase, string sessionId);

	[LoggerMessage(37, LogLevel.Error, "An ephemeral event of session {SessionId} cannot be read; it is skipped")]
	public static partial void EphemeralEventUnreadable(ILogger logger, Exception exception, string sessionId);
}
