namespace Whatsmeow.History;

internal static partial class HistoryLog
{
	[LoggerMessage(41, LogLevel.Error, "A history message of session {SessionId} cannot be read; it is skipped, and the rest of its batch arrives")]
	public static partial void MessageUnreadable(ILogger logger, Exception exception, string sessionId);
}
