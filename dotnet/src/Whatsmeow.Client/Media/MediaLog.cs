namespace Whatsmeow.Media;

internal static partial class MediaLog
{
	[LoggerMessage(42, LogLevel.Debug, "Uploading by path under the media root, for session {SessionId}")]
	public static partial void UploadingByPath(ILogger logger, string sessionId);

	[LoggerMessage(43, LogLevel.Debug, "Uploading in chunks, for session {SessionId}")]
	public static partial void UploadingInChunks(ILogger logger, string sessionId);

	[LoggerMessage(38, LogLevel.Debug, "Downloading by path under the media root, for session {SessionId}")]
	public static partial void DownloadingByPath(ILogger logger, string sessionId);

	[LoggerMessage(39, LogLevel.Debug, "Downloading in chunks, for session {SessionId}")]
	public static partial void DownloadingInChunks(ILogger logger, string sessionId);

	[LoggerMessage(40, LogLevel.Warning, "Removing the partial download {Path} failed")]
	public static partial void RemovePartialFailed(ILogger logger, Exception exception, string path);
}
