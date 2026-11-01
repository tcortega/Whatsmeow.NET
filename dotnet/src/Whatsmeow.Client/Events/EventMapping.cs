using Google.Protobuf.WellKnownTypes;
using Whatsmeow.Protocol.V1;
using Whatsmeow.Sessions;
using E2E = Whatsmeow.Proto.WAWebProtobufsE2E;
using Payload = Whatsmeow.Protocol.V1.StreamEventsResponse.PayloadOneofCase;

namespace Whatsmeow.Events;

/// <summary>The events.proto events as the public records.</summary>
internal static class EventMapping
{
	/// <summary>The lane of the session's own events, beside one lane per chat.</summary>
	public const string SessionLane = "";

	/// <summary>The record of <paramref name="response"/>, or null for a payload case this client does not know.</summary>
	/// <exception cref="WhatsmeowValidationException">A JID or a message id in it is malformed.</exception>
	public static WhatsAppEvent? ToEvent(SessionId sessionId, StreamEventsResponse response) =>
		response.PayloadCase switch
		{
			Payload.Message => new MessageReceived
			{
				SessionId = sessionId,
				Id = MessageId.From(response.Message.Info.Id),
				Source = ToSource(response.Message.Info.Source),
				PushName = NullIfEmpty(response.Message.Info.PushName),
				Timestamp = ToTime(response.Message.Info.MessageTime),
				Content = ToContent(response.Message.Message),
			},
			Payload.UndecryptableMessage => new UndecryptableMessageReceived
			{
				SessionId = sessionId,
				Id = MessageId.From(response.UndecryptableMessage.Info.Id),
				Source = ToSource(response.UndecryptableMessage.Info.Source),
				PushName = NullIfEmpty(response.UndecryptableMessage.Info.PushName),
				Timestamp = ToTime(response.UndecryptableMessage.Info.MessageTime),
				HidePlaceholder = response.UndecryptableMessage.HidePlaceholder,
			},
			Payload.ConnectionState => new ConnectionStateChanged { SessionId = sessionId, State = SessionMapping.ToState(response.ConnectionState.State) },
			// Receipts come with #9: skipped, and acked.
			Payload.Receipt or Payload.None => null,
			_ => null,
		};

	/// <summary>The lane an event runs in: its chat, so one chat's events run in order.</summary>
	public static string Lane(StreamEventsResponse response) =>
		response.PayloadCase switch
		{
			Payload.Message => response.Message.Info.Source.Chat,
			Payload.UndecryptableMessage => response.UndecryptableMessage.Info.Source.Chat,
			Payload.ConnectionState or Payload.Receipt or Payload.None => SessionLane,
			_ => SessionLane,
		};

	private static MessageSource ToSource(Protocol.V1.MessageSource source) =>
		new()
		{
			Chat = Jid.Parse(source.Chat),
			Sender = Jid.Parse(source.Sender),
			SenderAlt = ToJid(source.SenderAlt),
			RecipientAlt = ToJid(source.RecipientAlt),
			IsFromMe = source.IsFromMe,
		};

	private static MessageContent ToContent(E2E.Message? message) =>
		message switch
		{
			{ HasConversation: true } => new TextContent { Text = message.Conversation },
			{ ExtendedTextMessage.HasText: true } => new TextContent { Text = message.ExtendedTextMessage.Text },
			_ => new UnknownContent(),
		};

	private static Jid? ToJid(string value) => value.Length == 0 ? null : Jid.Parse(value);

	private static string? NullIfEmpty(string value) => value.Length == 0 ? null : value;

	private static DateTimeOffset ToTime(Timestamp? timestamp) => timestamp?.ToDateTimeOffset() ?? DateTimeOffset.UnixEpoch;
}
