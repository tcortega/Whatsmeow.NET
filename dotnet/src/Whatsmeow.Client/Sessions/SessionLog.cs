namespace Whatsmeow.Sessions;

internal static partial class SessionLog
{
	[LoggerMessage(8, LogLevel.Warning, "Connecting session {SessionId} again after a sidecar restart failed")]
	public static partial void ReconnectFailed(ILogger logger, Exception exception, string sessionId);

	[LoggerMessage(15, LogLevel.Warning, "Connecting session {SessionId} again after a sidecar restart failed: {ErrorCode}; the sidecar's log says why")]
	public static partial void ReconnectRefused(ILogger logger, string sessionId, WhatsmeowErrorCode errorCode);

	[LoggerMessage(9, LogLevel.Error, "Stopping the events of session {SessionId} failed")]
	public static partial void StopEventsFailed(ILogger logger, Exception exception, string sessionId);
}
