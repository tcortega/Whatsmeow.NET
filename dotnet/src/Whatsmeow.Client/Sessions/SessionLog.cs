namespace Whatsmeow.Sessions;

internal static partial class SessionLog
{
	[LoggerMessage(8, LogLevel.Warning, "Connecting session {SessionId} again after a sidecar restart failed")]
	public static partial void ReconnectFailed(ILogger logger, Exception exception, string sessionId);
}
