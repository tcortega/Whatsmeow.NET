using Google.Protobuf.WellKnownTypes;
using Whatsmeow.Events;
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

	public static async Task DeliverAsync(FakeWhatsAppService.FakeWhatsAppServiceClient fake, string session, CancellationToken cancellationToken, params string[] ids)
	{
		foreach (var id in ids)
			_ = await fake.DeliverMessageAsync(Message(session, id), cancellationToken: cancellationToken).ConfigureAwait(false);
	}
}
