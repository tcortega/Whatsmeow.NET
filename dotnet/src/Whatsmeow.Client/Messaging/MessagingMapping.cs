using Google.Protobuf.WellKnownTypes;
using Whatsmeow.Protocol.V1;

namespace Whatsmeow.Messaging;

/// <summary>The message.proto requests and responses as the public types.</summary>
internal static class MessagingMapping
{
	public static SendResult ToResult(MessageId id, string chat, Timestamp messageTime) =>
		new()
		{
			Id = id,
			Chat = Jid.Parse(chat),
			Timestamp = messageTime.ToDateTimeOffset(),
		};

	public static MarkReadRequest ToMarkRead(SessionId sessionId, Jid chat, IReadOnlyCollection<MessageId> messageIds, Jid? sender)
	{
		var request = new MarkReadRequest
		{
			SessionId = sessionId.Value,
			Chat = chat.ToString(),
			Sender = sender?.ToString() ?? "",
		};
		request.MessageIds.Add(messageIds.Select(id => id.Value));
		return request;
	}
}
