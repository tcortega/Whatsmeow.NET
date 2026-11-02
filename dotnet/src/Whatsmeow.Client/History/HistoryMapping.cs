using System.Collections.Immutable;
using Google.Protobuf.WellKnownTypes;
using Whatsmeow.Events;
using Whatsmeow.Protocol.V1;
using ProtoFailure = Whatsmeow.Protocol.V1.HistorySyncFailure;
using ProtoType = Whatsmeow.Protocol.V1.HistorySyncType;

namespace Whatsmeow.History;

/// <summary>The history events as the public records, and the request the client sends.</summary>
internal static class HistoryMapping
{
	/// <summary>
	/// The lane every history event runs in: one of its own, never a chat's. A chat's lane is its JID as whatsmeow writes
	/// it, which never starts with '@': with a user, the user comes first, and without one it is the server alone
	/// (whatsmeow types/jid.go JID.String).
	/// </summary>
	public const string Lane = "@history";

	/// <summary>
	/// The batch, its messages mapped as a live message is (<see cref="ContentMapping.ToContent"/>), or null when none is
	/// usable. A message without a usable id, chat or sender is skipped and logged, and the rest still arrive.
	/// </summary>
	public static HistoryReceived? ToEvent(SessionId sessionId, HistoryMessagesEvent batch, ILogger log)
	{
		var messages = ImmutableArray.CreateBuilder<HistoryMessage>(batch.Messages.Count);
		foreach (var message in batch.Messages)
		{
			try
			{
				var source = EventMapping.ToSource(message.Info.Source);
				messages.Add(
					new HistoryMessage
					{
						Id = MessageId.From(message.Info.Id),
						Source = source,
						PushName = EventMapping.NullIfEmpty(message.Info.PushName),
						Timestamp = EventMapping.ToTime(message.Info.MessageTime),
						Content = ContentMapping.ToContent(message.Message, message.ViewOnce, message.Target),
					}
				);
			}
#pragma warning disable CA1031 // Justification: one unusable history message is skipped, never its batch
			catch (Exception exception) when (exception is not OperationCanceledException)
#pragma warning restore CA1031
			{
				HistoryLog.MessageUnreadable(log, exception, sessionId.Value);
			}
		}

		return messages.Count == 0
			? null
			: new HistoryReceived
			{
				SessionId = sessionId,
				SyncType = ToType(batch.SyncType),
				Messages = messages.DrainToImmutable(),
			};
	}

	public static HistorySyncProgressed ToEvent(SessionId sessionId, HistorySyncProgressEvent progress) =>
		new()
		{
			SessionId = sessionId,
			SyncType = ToType(progress.SyncType),
			ChunkOrder = progress.ChunkOrder,
			Progress = progress.Progress,
			MessageCount = progress.MessageCount,
			SkippedCount = progress.SkippedCount,
			Failure = ToFailure(progress.Failure),
		};

	public static RequestHistoryRequest ToRequest(SessionId sessionId, MessageId oldest, Events.MessageSource source, DateTimeOffset oldestTimestamp, int count) =>
		new()
		{
			SessionId = sessionId.Value,
			Chat = source.Chat.ToString(),
			OldestMessageId = oldest.Value,
			OldestFromMe = source.IsFromMe,
			OldestMessageTime = Timestamp.FromDateTimeOffset(oldestTimestamp),
			Count = checked((uint)count),
		};

	private static HistorySyncType ToType(ProtoType type) =>
		type switch
		{
			ProtoType.InitialBootstrap => HistorySyncType.InitialBootstrap,
			ProtoType.InitialStatusV3 => HistorySyncType.InitialStatus,
			ProtoType.Full => HistorySyncType.Full,
			ProtoType.Recent => HistorySyncType.Recent,
			ProtoType.PushName => HistorySyncType.PushName,
			ProtoType.NonBlockingData => HistorySyncType.NonBlockingData,
			ProtoType.OnDemand => HistorySyncType.OnDemand,
			ProtoType.NoHistory => HistorySyncType.NoHistory,
			ProtoType.MessageAccessStatus => HistorySyncType.MessageAccessStatus,
			ProtoType.Unspecified => HistorySyncType.Unknown,
			_ => HistorySyncType.Unknown,
		};

	private static HistorySyncFailure? ToFailure(ProtoFailure failure) =>
		failure switch
		{
			ProtoFailure.Unspecified => null,
			ProtoFailure.Expired => HistorySyncFailure.Expired,
			ProtoFailure.Invalid => HistorySyncFailure.Invalid,
			ProtoFailure.Unavailable => HistorySyncFailure.Unavailable,
			_ => HistorySyncFailure.Unknown,
		};
}
