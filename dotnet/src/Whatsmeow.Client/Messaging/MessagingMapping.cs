using Google.Protobuf.WellKnownTypes;
using Whatsmeow.Core.Errors;
using Whatsmeow.Protocol.V1;

namespace Whatsmeow.Messaging;

/// <summary>The message.proto requests and responses as the public types.</summary>
internal static class MessagingMapping
{
	/// <summary>The send's result; <paramref name="field"/> is the response's chat field, which an unreadable chat names.</summary>
	public static SendResult ToResult(MessageId id, string chat, Timestamp messageTime, string field) =>
		new()
		{
			Id = id,
			Chat = SidecarValues.Jid(chat, field),
			Timestamp = messageTime.ToDateTimeOffset(),
		};

	public static Protocol.V1.MessageReply? ToProto(MessageReply? reply) =>
		reply is null
			? null
			: new()
			{
				Id = reply.Id.Value,
				Sender = reply.Sender?.ToString() ?? "",
				QuotedText = reply.QuotedText ?? "",
			};

	/// <summary>The users <paramref name="options"/> mentions, as JIDs' text: none for no options, and for mentions left default.</summary>
	public static string[] ToMentions(SendOptions? options)
	{
		var mentions = options is null ? [] : options.Mentions.AsSpan();
		var texts = new string[mentions.Length];
		for (var i = 0; i < mentions.Length; i++)
			texts[i] = mentions[i].ToString();
		return texts;
	}

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
