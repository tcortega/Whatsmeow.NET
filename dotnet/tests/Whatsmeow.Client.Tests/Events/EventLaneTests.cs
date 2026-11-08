using Whatsmeow.Events;
using Whatsmeow.Protocol.V1;
using E2E = Whatsmeow.Proto.WAWebProtobufsE2E;
using EphemeralPayload = Whatsmeow.Protocol.V1.StreamEphemeralEventsResponse.PayloadOneofCase;
using Payload = Whatsmeow.Protocol.V1.StreamEventsResponse.PayloadOneofCase;
using Timestamp = Google.Protobuf.WellKnownTypes.Timestamp;

namespace Whatsmeow.Client.Tests.Events;

/// <summary>
/// The dispatcher's lane of every event type (ARCHITECTURE §8.4, ADR 0036). The lane comes from the event's record, for
/// the real client and <c>FakeWhatsAppClient</c> alike, and must be the key the proto field gave before: the chat, the
/// contact, the group or the presence sender as the sidecar wrote it, or the session's lane. The JID strings are in
/// whatsmeow's own format (types.JID.String), which is what the sidecar sends.
/// </summary>
public sealed class EventLaneTests
{
	private static readonly SessionId s_session = SessionId.From("lanes");
	private static readonly Timestamp s_time = Timestamp.FromDateTimeOffset(new DateTimeOffset(2026, 11, 2, 12, 0, 0, TimeSpan.Zero));

	/// <summary>Each durable sample, and the lane the proto field named: the old proto-based rule, written out.</summary>
	private static readonly Dictionary<string, (StreamEventsResponse Response, string Lane)> s_durable = new(StringComparer.Ordinal)
	{
		["Message"] = Durable(
			new StreamEventsResponse
			{
				Message = new MessageEvent
				{
					Info = Info("120363025246125486@g.us", "5511999990001:12@s.whatsapp.net"),
					Message = new E2E.Message { Conversation = "hi" },
				},
			},
			static r => r.Message.Info.Source.Chat
		),
		["UndecryptableMessage"] = Durable(
			new StreamEventsResponse { UndecryptableMessage = new UndecryptableMessageEvent { Info = Info("5511999990001@s.whatsapp.net", "5511999990001@s.whatsapp.net") } },
			static r => r.UndecryptableMessage.Info.Source.Chat
		),
		["ConnectionState"] = Durable(
			new StreamEventsResponse { ConnectionState = new ConnectionStateEvent { State = new SessionState { Connected = new SessionState.Types.Connected() } } },
			static _ => ""
		),
		["Contact"] = Durable(
			new StreamEventsResponse
			{
				Contact = new ContactEvent { Jid = "5511999990002@s.whatsapp.net", PushName = new ContactEvent.Types.PushName { Name = "Ana" } },
			},
			static r => r.Contact.Jid
		),
		["MediaRetry"] = Durable(
			new StreamEventsResponse
			{
				MediaRetry = new MediaRetryEvent
				{
					MessageId = "3EB0AAAAAAAAAAAAAAAAAA",
					Chat = "236395184570386@lid",
					RetryTime = s_time,
					Failure = Protocol.V1.MediaRetryFailure.NotFound,
				},
			},
			static r => r.MediaRetry.Chat
		),
		["ChatState"] = Durable(
			new StreamEventsResponse
			{
				ChatState = new ChatStateEvent { Chat = "5511999990003@s.whatsapp.net", ChangeTime = s_time, Pin = new ChatPin { Pinned = true } },
			},
			static r => r.ChatState.Chat
		),
		["Receipt"] = Durable(
			new StreamEventsResponse
			{
				Receipt = new ReceiptEvent
				{
					Source = Source("5511999990004@s.whatsapp.net", "5511999990004:3@s.whatsapp.net"),
					MessageIds = { "3EB0AAAAAAAAAAAAAAAAAA" },
					ReceiptTime = s_time,
					Type = Protocol.V1.ReceiptType.Read,
				},
			},
			static r => r.Receipt.Source.Chat
		),
		["Group"] = Durable(
			new StreamEventsResponse
			{
				Group = new GroupEvent
				{
					Group = "120363025246125487@g.us",
					Time = s_time,
					Changed = new GroupEvent.Types.Changed { Changes = { new GroupChange { Renamed = new GroupChange.Types.Renamed { Name = "team" } } } },
				},
			},
			static r => r.Group.Group
		),
		["GroupJoined"] = Durable(
			new StreamEventsResponse
			{
				Group = new GroupEvent
				{
					Group = "120363025246125488@g.us",
					Time = s_time,
					Joined = new GroupEvent.Types.Joined { Group = new Group { Jid = "120363025246125488@g.us", Name = "team", CreateTime = s_time } },
				},
			},
			static r => r.Group.Group
		),

		// History has no lane field: every history event runs in the one history lane, never its messages' chat (ADR 0035).
		["HistoryMessages"] = Durable(
			new StreamEventsResponse
			{
				HistoryMessages = new HistoryMessagesEvent
				{
					SyncType = HistorySyncType.Recent,
					Messages =
					{
						new MessageEvent
						{
							Info = Info("120363025246125486@g.us", "5511999990001:12@s.whatsapp.net"),
							Message = new E2E.Message { Conversation = "old" },
						},
					},
				},
			},
			static _ => "@history"
		),
		["HistoryProgress"] = Durable(
			new StreamEventsResponse
			{
				HistoryProgress = new HistorySyncProgressEvent
				{
					SyncType = HistorySyncType.Recent,
					ChunkOrder = 1,
					Progress = 100,
					MessageCount = 1,
				},
			},
			static _ => "@history"
		),
	};

	private static readonly Dictionary<string, (StreamEphemeralEventsResponse Response, string Lane)> s_ephemeral = new(StringComparer.Ordinal)
	{
		["Presence"] = Ephemeral(
			new StreamEphemeralEventsResponse { Presence = new PresenceEvent { From = "5511999990005:7@s.whatsapp.net", State = PresenceState.Available } },
			static r => r.Presence.From
		),
		["ChatPresence"] = Ephemeral(
			new StreamEphemeralEventsResponse
			{
				ChatPresence = new ChatPresenceEvent
				{
					Source = Source("120363025246125486@g.us", "5511999990006@s.whatsapp.net"),
					State = ChatPresenceState.Typing,
				},
			},
			static r => r.ChatPresence.Source.Chat
		),
		["QueueOverflow"] = Ephemeral(new StreamEphemeralEventsResponse { QueueOverflow = new QueueOverflowEvent { Dropped = 3 } }, static _ => ""),
	};

	public static TheoryData<string> DurableSamples => [.. s_durable.Keys];

	public static TheoryData<string> EphemeralSamples => [.. s_ephemeral.Keys];

	[Theory]
	[MemberData(nameof(DurableSamples))]
	public void DurableEvent_RunsInTheLaneItsProtoFieldNamed(string sample)
	{
		var (response, lane) = s_durable[sample];
		var e = EventMapping.ToEvent(s_session, response);

		Assert.NotNull(e);
		Assert.False(EventMapping.IsEphemeral(e));
		Assert.Equal(lane, EventMapping.Lane(e));
	}

	[Theory]
	[MemberData(nameof(EphemeralSamples))]
	public void EphemeralEvent_RunsInTheLaneItsProtoFieldNamed(string sample)
	{
		var (response, lane) = s_ephemeral[sample];
		var e = EventMapping.ToEvent(s_session, response);

		Assert.NotNull(e);
		Assert.True(EventMapping.IsEphemeral(e));
		Assert.Equal(lane, EventMapping.Lane(e));
	}

	/// <summary>A new payload case needs a sample here, so its lane is checked too.</summary>
	[Fact]
	public void EveryPayloadCase_HasASample()
	{
		Payload[] sampled = [.. s_durable.Values.Select(static sample => sample.Response.PayloadCase).Distinct().Order()];
		EphemeralPayload[] ephemeral = [.. s_ephemeral.Values.Select(static sample => sample.Response.PayloadCase).Distinct().Order()];

		Assert.Equal([.. Enum.GetValues<Payload>().Where(static payload => payload is not Payload.None).Order()], sampled);
		Assert.Equal([.. Enum.GetValues<EphemeralPayload>().Where(static payload => payload is not EphemeralPayload.None).Order()], ephemeral);
	}

	/// <summary>An event this client cannot map has no handlers to order, so it runs in the session's lane.</summary>
	[Fact]
	public void UnmappedEvent_RunsInTheSessionLane() => Assert.Equal(EventMapping.SessionLane, EventMapping.Lane(e: null));

	private static (StreamEventsResponse, string) Durable(StreamEventsResponse response, Func<StreamEventsResponse, string> lane) => (response, lane(response));

	private static (StreamEphemeralEventsResponse, string) Ephemeral(StreamEphemeralEventsResponse response, Func<StreamEphemeralEventsResponse, string> lane) =>
		(response, lane(response));

	private static MessageInfo Info(string chat, string sender) =>
		new()
		{
			Source = Source(chat, sender),
			Id = "3EB0AAAAAAAAAAAAAAAAAA",
			MessageTime = s_time,
		};

	private static Protocol.V1.MessageSource Source(string chat, string sender) => new() { Chat = chat, Sender = sender };
}
