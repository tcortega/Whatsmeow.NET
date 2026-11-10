using System.Collections.Immutable;
using Google.Protobuf.WellKnownTypes;
using Whatsmeow.Events;
using Whatsmeow.History;
using Whatsmeow.IntegrationTests.Fake;
using Whatsmeow.IntegrationTests.Sessions;
using Whatsmeow.IntegrationTests.Support;
using Whatsmeow.Sessions;
using Conversation = Whatsmeow.IntegrationTests.Fake.DeliverHistorySyncRequest.Types.Conversation;
using MessageSource = Whatsmeow.Events.MessageSource;
using WebMessage = Whatsmeow.IntegrationTests.Fake.DeliverHistorySyncRequest.Types.Message;

namespace Whatsmeow.IntegrationTests.History;

/// <summary>The history blobs the History tests make the fake phone send, and what the application then gets.</summary>
internal static class Histories
{
	/// <summary>Every history message's WhatsApp timestamp.</summary>
	public static DateTimeOffset At { get; } = DateTimeOffset.FromUnixTimeSeconds(1_600_000_000);

	/// <summary>waHistorySync's sync type numbers.</summary>
	public const int Recent = 3;

	public const int Full = 2;

	public const int OnDemand = 6;

	/// <summary>A blob's batch holds at most this many messages (internal/history maxBatchMessages).</summary>
	public const int BatchMessages = 256;

	/// <summary>A conversation of <paramref name="count"/> text messages from the chat's other user, ids <c>{prefix}1</c> on.</summary>
	public static Conversation Texts(string chat, string prefix, int count)
	{
		var conversation = new Conversation { Chat = chat };
		conversation.Messages.Add(
			Enumerable.Range(1, count).Select(i => new WebMessage { Id = $"{prefix}{i}", Text = $"text {prefix}{i}", MessageTime = Timestamp.FromDateTimeOffset(At) })
		);
		return conversation;
	}

	public static DeliverHistorySyncRequest Blob(string session, string messageId, int syncType, params Conversation[] conversations)
	{
		var request = new DeliverHistorySyncRequest { SessionId = session, MessageId = messageId, SyncType = syncType, ChunkOrder = 2, Progress = 40 };
		request.Conversations.Add(conversations);
		return request;
	}

	/// <summary>What the application gets for a message of <see cref="Texts"/>.</summary>
	public static HistoryMessage Text(string chat, string id) =>
		new()
		{
			Id = MessageId.From(id),
			Source = new MessageSource { Chat = Jid.Parse(chat), Sender = Jid.Parse(chat), SenderAlt = null, RecipientAlt = null, IsFromMe = false },
			PushName = null,
			Timestamp = At,
			Content = new TextContent { Text = $"text {id}" },
		};

	public static HistoryReceived Batch(string session, HistorySyncType type, params IEnumerable<HistoryMessage> messages) =>
		new()
		{
			SessionId = SessionId.From(session),
			SyncType = type,
			Messages = [.. messages],
		};

	public static HistorySyncProgressed Delivered(string session, HistorySyncType type, long messages) =>
		new()
		{
			SessionId = SessionId.From(session),
			SyncType = type,
			ChunkOrder = 2,
			Progress = 40,
			MessageCount = messages,
			SkippedCount = 0,
			Failure = null,
		};

	/// <summary>A linked session connected, its offline sync completed: history is downloaded from here on.</summary>
	public static async Task<IWhatsAppClient> CaughtUpAsync(FakeSidecar fixture, string session, CancellationToken cancellationToken)
	{
		var client = await SessionSetup.ConnectedAsync(fixture, session, cancellationToken).ConfigureAwait(false);
		_ = await CompleteOfflineSyncAsync(fixture, client, session, cancellationToken).ConfigureAwait(false);
		return client;
	}

	/// <summary>Completes the session's offline sync, and returns where the fake's log was just before it.</summary>
	public static async Task<ulong> CompleteOfflineSyncAsync(FakeSidecar fixture, IWhatsAppClient client, string session, CancellationToken cancellationToken)
	{
		var index = (await fixture.Fake.CompleteOfflineSyncAsync(new CompleteOfflineSyncRequest { SessionId = session }, cancellationToken: cancellationToken).ConfigureAwait(false)).Index;
		var watch = StateWatch.Start(client, cancellationToken);
		await using (watch.ConfigureAwait(false))
			await watch.ReachesAsync(new SessionState.CaughtUp(), new SessionState.Connected()).ConfigureAwait(false);
		return index;
	}

	/// <summary>The session's entries of the fake's log after <paramref name="after"/>, up to the first that <paramref name="last"/> matches.</summary>
	public static async Task<IReadOnlyList<Sent>> SentUntilAsync(FakeSidecar fixture, string session, Func<Sent, bool> last, CancellationToken cancellationToken, ulong after = 0)
	{
		var sent = new List<Sent>();
		while (true)
		{
			var next = (await fixture.Fake.WaitForSentAsync(new WaitForSentRequest { SessionId = session, After = after }, cancellationToken: cancellationToken).ConfigureAwait(false)).Sent;
			sent.Add(next);
			after = next.Index;
			if (last(next))
				return sent;
		}
	}

	/// <summary>Every message the batches carried, in order.</summary>
	public static ImmutableArray<HistoryMessage> MessagesOf(IEnumerable<HistoryReceived> batches) => [.. batches.SelectMany(b => b.Messages)];
}
