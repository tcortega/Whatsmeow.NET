namespace Whatsmeow.Extensions.Hosting;

/// <summary>The hosting package's log messages, in the client's category (<c>Whatsmeow.Client</c>).</summary>
internal static partial class HostingLog
{
	[LoggerMessage(60, LogLevel.Warning, "Declared session {SessionId} is {State}, so the host start does not connect it")]
	public static partial void SessionNotConnected(ILogger logger, string sessionId, string state);
}
