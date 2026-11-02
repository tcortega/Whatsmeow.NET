using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.Logging.Abstractions;
using Whatsmeow.ChatState;
using Whatsmeow.Contacts;
using Whatsmeow.Presence;
using Whatsmeow.Media;
using Whatsmeow.Groups;
using Whatsmeow.History;
using Whatsmeow.Protocol.V1;
using Whatsmeow.Sessions;
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
	/// <param name="sessionId">The session the event is of.</param>
	/// <param name="response">The event.</param>
	/// <param name="log">Where a history message that cannot be used is logged, as it is skipped; nowhere when null.</param>
	/// <exception cref="WhatsmeowValidationException">The id, the chat or the sender is missing or malformed: the event is unusable.</exception>
	public static WhatsAppEvent? ToEvent(SessionId sessionId, StreamEventsResponse response, ILogger? log = null) =>
		response.PayloadCase switch
		{
			Payload.Message => ToMessage(sessionId, response.Message),
			Payload.UndecryptableMessage => new UndecryptableMessageReceived
			{
				SessionId = sessionId,
				Id = MessageId.From(response.UndecryptableMessage.Info.Id),
				Source = ToSource(response.UndecryptableMessage.Info.Source),
				PushName = NullIfEmpty(response.UndecryptableMessage.Info.PushName),
				Timestamp = ToTime(response.UndecryptableMessage.Info.MessageTime),
				HidePlaceholder = response.UndecryptableMessage.HidePlaceholder,
				Unavailable = response.UndecryptableMessage.Unavailable ? ToUnavailable(response.UndecryptableMessage.UnavailableType) : null,
			},
			Payload.ConnectionState => new ConnectionStateChanged { SessionId = sessionId, State = SessionMapping.ToState(response.ConnectionState.State) },
			Payload.Contact => ContactMapping.ToEvent(sessionId, response.Contact),
			Payload.MediaRetry => new MediaRetryReceived
			{
				SessionId = sessionId,
				Id = MessageId.From(response.MediaRetry.MessageId),
				Chat = Jid.Parse(response.MediaRetry.Chat),
				Sender = ToJid(response.MediaRetry.Sender),
				IsFromMe = response.MediaRetry.IsFromMe,
				Timestamp = ToTime(response.MediaRetry.RetryTime),
				Result = response.MediaRetry.ResultCase switch
				{
					MediaRetryEvent.ResultOneofCase.Media => new MediaRetryResult.Reuploaded { Media = MediaMapping.ToRef(response.MediaRetry.Media) },
					MediaRetryEvent.ResultOneofCase.Failure or MediaRetryEvent.ResultOneofCase.None => new MediaRetryResult.Failed
					{
						Reason = MediaMapping.ToFailure(response.MediaRetry.Failure),
					},
					_ => new MediaRetryResult.Failed { Reason = MediaRetryFailure.Unknown },
				},
			},
			Payload.ChatState => ChatStateMapping.ToEvent(sessionId, response.ChatState),
			Payload.Receipt => new ReceiptReceived
			{
				SessionId = sessionId,
				Source = ToSource(response.Receipt.Source),
				MessageIds = [.. response.Receipt.MessageIds.Select(MessageId.From)],
				Timestamp = ToTime(response.Receipt.ReceiptTime),
				Type = ToType(response.Receipt.Type),
				MessageSender = ToJid(response.Receipt.MessageSender),
			},
			Payload.Group => GroupMapping.ToEvent(sessionId, response.Group),
			Payload.HistoryMessages => HistoryMapping.ToEvent(sessionId, response.HistoryMessages, log ?? NullLogger.Instance),
			Payload.HistoryProgress => HistoryMapping.ToEvent(sessionId, response.HistoryProgress),
			Payload.None => null,
			_ => null,
		};

	/// <summary>
	/// The lane an event runs in, for the client's dispatcher and <c>FakeWhatsAppClient</c>'s alike (ADR 0036): its chat, so
	/// one chat's events run in order; a contact's changes in the contact's lane, which is its direct chat when it uses the
	/// same form; a group's changes in the group's, with its messages; presence in its user's; history in a lane of its
	/// own, never a chat's, so a live message never waits for it (ADR 0035); the session's lane without one. The key is
	/// the JID as whatsmeow writes it, which is what the sidecar sends, so it is the proto field's own text
	/// (EventLaneTests). An event this client cannot map has no handlers to order, and runs in the session's lane.
	/// </summary>
	public static string Lane(WhatsAppEvent? e) =>
		e switch
		{
			MessageReceived message => message.Source.Chat.ToString(),
			UndecryptableMessageReceived message => message.Source.Chat.ToString(),
			ContactUpdated contact => contact.Jid.ToString(),
			MediaRetryReceived retry => retry.Chat.ToString(),
			ChatStateChanged chatState => chatState.Chat.ToString(),
			ReceiptReceived receipt => receipt.Source.Chat.ToString(),
			GroupInfoChanged group => group.Group.ToString(),
			GroupJoined group => group.Group.Jid.ToString(),
			PresenceChanged presence => presence.From.ToString(),
			ChatPresenceChanged chatPresence => chatPresence.Source.Chat.ToString(),
			HistoryReceived or HistorySyncProgressed => HistoryMapping.Lane,
			ConnectionStateChanged or EphemeralEventsDropped or null => SessionLane,
			_ => SessionLane,
		};

	/// <summary>
	/// Whether the event is one of <c>StreamEphemeralEvents</c>: best effort, in a dispatcher of its own that never acks, so
	/// it never holds back the durable events (ADR 0027).
	/// </summary>
	public static bool IsEphemeral(WhatsAppEvent e) => e is PresenceChanged or ChatPresenceChanged or EphemeralEventsDropped;

	/// <summary>
	/// The record of an ephemeral event, or null for a payload case or a state this client does not know. The time and
	/// the alt JIDs map as for <see cref="ToEvent(SessionId, StreamEventsResponse, ILogger?)"/>.
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

	private static MessageReceived ToMessage(SessionId sessionId, MessageEvent message)
	{
		var source = ToSource(message.Info.Source);
		return new MessageReceived
		{
			SessionId = sessionId,
			Id = MessageId.From(message.Info.Id),
			Source = source,
			PushName = NullIfEmpty(message.Info.PushName),
			Timestamp = ToTime(message.Info.MessageTime),
			Content = ContentMapping.ToContent(message.Message, message.ViewOnce, message.Target),
			ReplyTo = ContentMapping.ToReplyTo(message.Message, message.ReplyTo),
			Mentions = ContentMapping.ToMentions(message.Message),
			IsEphemeral = ContentMapping.IsEphemeral(message.Message, message.Ephemeral),
			DestinationChat = ToJid(message.DeviceSentDestination),
#pragma warning disable WMN1001 // Justification: the library fills its own experimental member; only its readers opt in
			Raw = message.RawMessage ?? message.Message,
#pragma warning restore WMN1001
		};
	}

	internal static MessageSource ToSource(Protocol.V1.MessageSource source) =>
		new()
		{
			Chat = Jid.Parse(source.Chat),
			Sender = Jid.Parse(source.Sender),
			SenderAlt = ToJid(source.SenderAlt),
			RecipientAlt = ToJid(source.RecipientAlt),
			IsFromMe = source.IsFromMe,
		};

	private static UnavailableType ToUnavailable(Protocol.V1.UnavailableType type) =>
		type switch
		{
			Protocol.V1.UnavailableType.ViewOnce => UnavailableType.ViewOnce,
			Protocol.V1.UnavailableType.Unspecified => UnavailableType.Unknown,
			_ => UnavailableType.Unknown,
		};

	private static ReceiptType ToType(Protocol.V1.ReceiptType type) =>
		type switch
		{
			Protocol.V1.ReceiptType.Delivered => ReceiptType.Delivered,
			Protocol.V1.ReceiptType.Sender => ReceiptType.Sender,
			Protocol.V1.ReceiptType.Retry => ReceiptType.Retry,
			Protocol.V1.ReceiptType.Read => ReceiptType.Read,
			Protocol.V1.ReceiptType.ReadSelf => ReceiptType.ReadSelf,
			Protocol.V1.ReceiptType.Played => ReceiptType.Played,
			Protocol.V1.ReceiptType.PlayedSelf => ReceiptType.PlayedSelf,
			Protocol.V1.ReceiptType.ServerError => ReceiptType.ServerError,
			Protocol.V1.ReceiptType.Inactive => ReceiptType.Inactive,
			Protocol.V1.ReceiptType.PeerMsg => ReceiptType.PeerMessage,
			Protocol.V1.ReceiptType.HistorySync => ReceiptType.HistorySync,
			Protocol.V1.ReceiptType.Unspecified => ReceiptType.Unknown,
			_ => ReceiptType.Unknown,
		};

	/// <summary>An alt JID that is missing or malformed is unknown; the message still arrives.</summary>
	private static Jid? ToJid(string value) => Jid.TryParse(value, out var jid) ? jid : null;

	internal static string? NullIfEmpty(string value) => value.Length == 0 ? null : value;

	/// <summary>
	/// The timestamp, clamped to what <see cref="DateTimeOffset"/> holds, so a time out of its range never costs the
	/// message (Google.Protobuf's conversion throws for one).
	/// </summary>
	internal static DateTimeOffset ToTime(Timestamp? timestamp) =>
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
