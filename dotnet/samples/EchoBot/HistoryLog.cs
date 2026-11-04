using Microsoft.Extensions.Logging;
using Whatsmeow;
using Whatsmeow.Extensions.Hosting;
using Whatsmeow.History;

namespace EchoBot;

/// <summary>
/// Logs each batch of a session's history. History arrives apart from live messages, so the bot never answers an old
/// message as if it were new.
/// </summary>
public sealed partial class HistoryLog(ILogger<HistoryLog> logger) : IWhatsAppEventHandler<HistoryReceived>
{
	public ValueTask HandleAsync(HistoryReceived e, IWhatsAppClient client, CancellationToken cancellationToken)
	{
		if (e.Messages.IsEmpty)
			return ValueTask.CompletedTask;

		var times = e.Messages.Select(static message => message.Timestamp).ToArray();
		HistoryBatch(logger, e.SessionId.Value, e.Messages.Length, e.SyncType.ToString(), times.Min(), times.Max());
		return ValueTask.CompletedTask;
	}

	[LoggerMessage(30, LogLevel.Information, "History of session {SessionId}: {Count} {SyncType} messages from {Oldest:O} to {Newest:O}")]
	private static partial void HistoryBatch(ILogger logger, string sessionId, int count, string syncType, DateTimeOffset oldest, DateTimeOffset newest);
}
