using Google.Protobuf.WellKnownTypes;
using Whatsmeow.Contacts;
using Whatsmeow.Presence;
using Whatsmeow.Protocol.V1;
using Whatsmeow.Sessions;
using E2E = Whatsmeow.Proto.WAWebProtobufsE2E;
using EphemeralPayload = Whatsmeow.Protocol.V1.StreamEphemeralEventsResponse.PayloadOneofCase;
using Payload = Whatsmeow.Protocol.V1.StreamEventsResponse.PayloadOneofCase;

namespace Whatsmeow.Events;

/// <summary>The events.proto events as the public records.</summary>
internal static class EventMapping
{
	/// <summary>The lane of the session's own events, beside one lane per chat.</summary>
	public const string SessionLane = "";

	/// <summary>
	/// The record of <paramref name="response"/>, or null for a payload case this client does not know. It maps field by
	/// field: a time out of range is clamped and a malformed alt JID is unknown, so the content still arrives.
	/// </summary>
	/// <exception cref="WhatsmeowValidationException">The id, the chat or the sender is missing or malformed: the event is unusable.</exception>
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
			Payload.Contact => ContactMapping.ToEvent(sessionId, response.Contact),
			// Receipts come with #9: skipped, and acked.
			Payload.Receipt or Payload.None => null,
			_ => null,
		};

	/// <summary>The lane an event runs in: its chat, so one chat's events run in order; the session's lane without one.</summary>
	public static string Lane(StreamEventsResponse response) =>
		response.PayloadCase switch
		{
			Payload.Message => response.Message.Info?.Source?.Chat ?? SessionLane,
			Payload.UndecryptableMessage => response.UndecryptableMessage.Info?.Source?.Chat ?? SessionLane,
			// The contact's lane: a user's changes run in order with their direct chat when it uses the same form.
			Payload.Contact => response.Contact.Jid,
			Payload.ConnectionState or Payload.Receipt or Payload.None => SessionLane,
			_ => SessionLane,
		};

	/// <summary>
	/// The record of an ephemeral event, or null for a payload case or a state this client does not know. The time and
	/// the alt JIDs map as for <see cref="ToEvent(SessionId, StreamEventsResponse)"/>.
	/// </summary>
	/// <exception cref="WhatsmeowValidationException">The user, the chat or the sender is missing or malformed: the event is unusable.</exception>
	public static WhatsAppEvent? ToEvent(SessionId sessionId, StreamEphemeralEventsResponse response) =>
		response.PayloadCase switch
		{
			EphemeralPayload.Presence when PresenceMapping.ToState(response.Presence.State) is { } state => new PresenceChanged
			{
				SessionId = sessionId,
				From = Jid.Parse(response.Presence.From),
				State = state,
				LastSeen = response.Presence.LastSeen is null ? null : ToTime(response.Presence.LastSeen),
			},
			EphemeralPayload.ChatPresence when PresenceMapping.ToState(response.ChatPresence.State) is { } state => new ChatPresenceChanged
			{
				SessionId = sessionId,
				Source = ToSource(response.ChatPresence.Source),
				State = state,
			},
			EphemeralPayload.QueueOverflow => new EphemeralEventsDropped
			{
				SessionId = sessionId,
				Count = (long)Math.Min(response.QueueOverflow.Dropped, long.MaxValue),
			},
			EphemeralPayload.Presence or EphemeralPayload.ChatPresence or EphemeralPayload.None => null,
			_ => null,
		};

	/// <summary>The lane an ephemeral event runs in: its user or its chat; the session's lane without one.</summary>
	public static string Lane(StreamEphemeralEventsResponse response) =>
		response.PayloadCase switch
		{
			EphemeralPayload.Presence => response.Presence.From,
			EphemeralPayload.ChatPresence => response.ChatPresence.Source?.Chat ?? SessionLane,
			EphemeralPayload.QueueOverflow or EphemeralPayload.None => SessionLane,
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

	/// <summary>An alt JID that is missing or malformed is unknown; the message still arrives.</summary>
	private static Jid? ToJid(string value) => Jid.TryParse(value, out var jid) ? jid : null;

	private static string? NullIfEmpty(string value) => value.Length == 0 ? null : value;

	/// <summary>
	/// The timestamp, clamped to what <see cref="DateTimeOffset"/> holds, so a time out of its range never costs the
	/// message (Google.Protobuf's conversion throws for one).
	/// </summary>
	private static DateTimeOffset ToTime(Timestamp? timestamp) =>
		timestamp switch
		{
			null => DateTimeOffset.UnixEpoch,
			{ Seconds: > MaxSeconds } => DateTimeOffset.MaxValue,
			{ Seconds: < MinSeconds } => DateTimeOffset.MinValue,
			{ Nanos: < 0 or > 999_999_999 } => DateTimeOffset.FromUnixTimeSeconds(timestamp.Seconds),
			_ => timestamp.ToDateTimeOffset(),
		};

	private const long MinSeconds = -62_135_596_800;
	private const long MaxSeconds = 253_402_300_799;
}
