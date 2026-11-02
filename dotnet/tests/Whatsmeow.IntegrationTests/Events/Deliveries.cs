using Google.Protobuf.WellKnownTypes;
using Whatsmeow.Events;
using Whatsmeow.Groups;
using Whatsmeow.IntegrationTests.Fake;

namespace Whatsmeow.IntegrationTests.Events;

/// <summary>What the Events tests make the fake WhatsApp deliver, and the records the application then gets.</summary>
internal static class Deliveries
{
	/// <summary>The message's WhatsApp timestamp in every delivery.</summary>
	public static DateTimeOffset At { get; } = DateTimeOffset.FromUnixTimeSeconds(1_700_000_000);

	/// <summary>A direct chat with one user.</summary>
	public const string Chat = "5511999999999@s.whatsapp.net";

	public static DeliverMessageRequest Message(string session, string id, string chat = Chat) =>
		new()
		{
			SessionId = session,
			Id = id,
			Chat = chat,
			Sender = chat,
			Text = $"text of {id}",
			MessageTime = Timestamp.FromDateTimeOffset(At),
		};

	/// <summary>What the application gets for <see cref="Message"/>.</summary>
	public static MessageReceived Received(string session, string id, string chat = Chat) =>
		new()
		{
			SessionId = SessionId.From(session),
			Id = MessageId.From(id),
			Source = new MessageSource { Chat = Jid.Parse(chat), Sender = Jid.Parse(chat), SenderAlt = null, RecipientAlt = null, IsFromMe = false },
			PushName = null,
			Timestamp = At,
			Content = new TextContent { Text = $"text of {id}" },
		};

	/// <summary>The group the group deliveries change, and the user who changes it.</summary>
	public const string Group = "120363199000000001@g.us";

	/// <summary>A notification that the group was renamed to <paramref name="name"/>.</summary>
	public static DeliverGroupNotificationRequest Renamed(string session, string name) =>
		new()
		{
			SessionId = session,
			Group = Group,
			Sender = Chat,
			Time = Timestamp.FromDateTimeOffset(At),
			Name = name,
		};

	/// <summary>What the application gets for <see cref="Renamed"/>.</summary>
	public static GroupInfoChanged Renaming(string session, string name) =>
		new()
		{
			SessionId = SessionId.From(session),
			Group = Jid.Parse(Group),
			Sender = Jid.Parse(Chat),
			SenderAlt = null,
			Timestamp = At,
			Changes = [new GroupChange.Renamed { Name = name }],
		};

	public static async Task DeliverGroupAsync(FakeWhatsAppService.FakeWhatsAppServiceClient fake, string session, CancellationToken cancellationToken, params string[] names)
	{
		foreach (var name in names)
			_ = await fake.DeliverGroupNotificationAsync(Renamed(session, name), cancellationToken: cancellationToken).ConfigureAwait(false);
	}

	/// <summary>What <see cref="DeliverAsync"/> sends, by text and id in order, for <see cref="DeliveryChecks"/>.</summary>
	public static List<(string Text, MessageId Id)> Sent(params string[] ids) => [.. ids.Select(id => ($"text of {id}", MessageId.From(id)))];

	public static async Task DeliverAsync(FakeWhatsAppService.FakeWhatsAppServiceClient fake, string session, CancellationToken cancellationToken, params string[] ids)
	{
		foreach (var id in ids)
			_ = await fake.DeliverMessageAsync(Message(session, id), cancellationToken: cancellationToken).ConfigureAwait(false);
	}
}
