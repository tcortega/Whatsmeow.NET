using Whatsmeow.Events;
using Whatsmeow.History;
using Whatsmeow.Presence;

namespace Whatsmeow.Testing.Tests;

/// <summary>Events a test raises, built from the public records.</summary>
internal static class Samples
{
	public static readonly SessionId Session = SessionId.From("fake");

	public static readonly DateTimeOffset Now = new(2026, 11, 2, 12, 0, 0, TimeSpan.Zero);

	public static readonly Jid Ana = Jid.Parse("5511999990001@s.whatsapp.net");

	public static readonly Jid Bia = Jid.Parse("5511999990002@s.whatsapp.net");

	public static readonly Jid Team = Jid.Parse("120363025246125486@g.us");

	public static MessageReceived Message(Jid chat, string text, SessionId? session = null) =>
		new()
		{
			SessionId = session ?? Session,
			Id = MessageId.New(),
			Source = Source(chat),
			PushName = null,
			Timestamp = Now,
			Content = new TextContent { Text = text },
		};

	public static HistoryReceived HistoryBatch(Jid chat, string text) =>
		new()
		{
			SessionId = Session,
			SyncType = HistorySyncType.Recent,
			Messages =
			[
				new HistoryMessage
				{
					Id = MessageId.New(),
					Source = Source(chat),
					PushName = null,
					Timestamp = Now,
					Content = new TextContent { Text = text },
				},
			],
		};

	public static HistorySyncProgressed Progress() =>
		new()
		{
			SessionId = Session,
			SyncType = HistorySyncType.Recent,
			ChunkOrder = 1,
			Progress = 100,
			MessageCount = 1,
			SkippedCount = 0,
			Failure = null,
		};

	public static ChatPresenceChanged Typing(Jid chat) =>
		new()
		{
			SessionId = Session,
			Source = Source(chat),
			State = ChatPresenceState.Typing,
		};

	public static MessageSource Source(Jid chat) =>
		new()
		{
			Chat = chat,
			Sender = chat,
			SenderAlt = null,
			RecipientAlt = null,
			IsFromMe = false,
		};

	public static string Text(MessageReceived message) => ((TextContent)message.Content).Text;
}
