namespace Whatsmeow.Sidecar;

/// <summary>The supervisor's log messages, in category <see cref="Category"/>; the sidecar's own stderr goes to <see cref="SidecarCategory"/>.</summary>
internal static partial class SidecarLog
{
	public const string Category = "Whatsmeow.Client";
	public const string SidecarCategory = "Whatsmeow.Sidecar";

	[LoggerMessage(1, LogLevel.Information, "Sidecar {ProcessId} version {Version} is ready at {Endpoint}")]
	public static partial void SidecarStarted(ILogger logger, int processId, string? version, string endpoint);

	[LoggerMessage(2, LogLevel.Warning, "Sidecar {ProcessId} exited unexpectedly with code {ExitCode}")]
	public static partial void SidecarExited(ILogger logger, int processId, int exitCode);

	[LoggerMessage(3, LogLevel.Information, "Sidecar restarted as {ProcessId} after {Delay} (attempt {Attempt})")]
	public static partial void SidecarRestarted(ILogger logger, int processId, TimeSpan delay, int attempt);

	[LoggerMessage(4, LogLevel.Error, "Sidecar restart attempt {Attempt} failed; the next one is in {Delay}")]
	public static partial void SidecarRestartFailed(ILogger logger, Exception exception, int attempt, TimeSpan delay);

	[LoggerMessage(5, LogLevel.Warning, "The sidecar at {Path} cannot run in place; running a copy at {Copy}")]
	public static partial void SidecarCopied(ILogger logger, string path, string copy);

	[LoggerMessage(6, LogLevel.Warning, "Deleting the socket directory {Directory} failed")]
	public static partial void SocketDirectoryNotDeleted(ILogger logger, Exception exception, string directory);

	[LoggerMessage(10, LogLevel.Information, "Connected to the sidecar at {Address}, version {Version}")]
	public static partial void ExternalSidecarConnected(ILogger logger, string address, string version);

	[LoggerMessage(11, LogLevel.Warning, "The sidecar at {Address} went away")]
	public static partial void ExternalSidecarLost(ILogger logger, string address);

	/// <summary>Named as an embedded restart is: either way the sidecar is back, and the client resumed (ADR 0046).</summary>
	[LoggerMessage(12, LogLevel.Information, "The sidecar at {Address} is back after {Delay} (attempt {Attempt})", EventName = "SidecarRestarted")]
	public static partial void ExternalSidecarBack(ILogger logger, string address, TimeSpan delay, int attempt);

	[LoggerMessage(13, LogLevel.Debug, "The health watch on the external sidecar ended")]
	public static partial void ExternalWatchEnded(ILogger logger, Exception exception);

	/// <summary>One sidecar stderr line, at the level it reports.</summary>
	[LoggerMessage(EventId = 7, Message = "{Line}")]
	public static partial void SidecarOutput(ILogger logger, LogLevel level, string line);
}
