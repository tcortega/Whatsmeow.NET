using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Whatsmeow.Events;
using Whatsmeow.History;
using Whatsmeow.IntegrationTests.Events;
using Whatsmeow.IntegrationTests.Fake;
using Whatsmeow.IntegrationTests.Sessions;
using Whatsmeow.IntegrationTests.Support;
using Whatsmeow.Messaging;
using Conversation = Whatsmeow.IntegrationTests.Fake.DeliverHistorySyncRequest.Types.Conversation;
using MessageSource = Whatsmeow.Events.MessageSource;
using E2E = Whatsmeow.Proto.WAWebProtobufsE2E;
using WebMessage = Whatsmeow.IntegrationTests.Fake.DeliverHistorySyncRequest.Types.Message;

namespace Whatsmeow.IntegrationTests.History;

/// <summary>
/// The History sync slice through the public client, on the shared fake-mode sidecar (ARCHITECTURE §7.7, ADR 0035). The
/// fake phone sends blobs (DeliverHistorySync); what the session downloaded and deleted is read from the fake's log by
/// condition. Each test uses sessions of its own.
/// </summary>
public sealed class HistoryTests(FakeSidecar fixture)
{
	private const int Timeout = 120_000;

	private const string Direct = "5511930000101@s.whatsapp.net";
	private const string Group = "120363000000000101@g.us";
	private const string Member = "5511930000102@s.whatsapp.net";

	[Fact(Timeout = Timeout)]
	public async Task FullCycle_History_ArrivesAsBatchesAndProgress_NeverAsLiveMessages()
	{
		var ct = TestContext.Current.CancellationToken;
		const string Session = "history-fullcycle";
		var client = await Histories.CaughtUpAsync(fixture, Session, ct);
		using var batches = new Received<HistoryReceived>(client);
		using var progress = new Received<HistorySyncProgressed>(client);
		using var live = new Received<MessageReceived>(client);

		var group = new Conversation { Chat = Group };
		group.Messages.Add(new WebMessage { Id = "G1", Participant = Member, Text = "from a member", MessageTime = Timestamp.FromDateTimeOffset(Histories.At), PushName = "Bia" });
		group.Messages.Add(new WebMessage { Id = "G2", FromMe = true, Text = "from the account", MessageTime = Timestamp.FromDateTimeOffset(Histories.At) });
		var blob = await fixture.Fake.DeliverHistorySyncAsync(Histories.Blob(Session, "N1", Histories.Recent, Histories.Texts(Direct, "D", 2), group), cancellationToken: ct);

		Assert.Equal(
			Histories.Batch(
				Session,
				HistorySyncType.Recent,
				Histories.Text(Direct, "D1"),
				Histories.Text(Direct, "D2"),
				new HistoryMessage
				{
					Id = MessageId.From("G1"),
					Source = new MessageSource { Chat = Jid.Parse(Group), Sender = Jid.Parse(Member), SenderAlt = null, RecipientAlt = null, IsFromMe = false },
					PushName = "Bia",
					Timestamp = Histories.At,
					Content = new TextContent { Text = "from a member" },
				},
				new HistoryMessage
				{
					Id = MessageId.From("G2"),
					Source = new MessageSource
					{
						Chat = Jid.Parse(Group),
						Sender = Jid.Parse($"{Session}@s.whatsapp.net"),
						SenderAlt = null,
						RecipientAlt = null,
						IsFromMe = true,
					},
					PushName = null,
					Timestamp = Histories.At,
					Content = new TextContent { Text = "from the account" },
				}
			),
			await batches.NextAsync(ct)
		);
		Assert.Equal(Histories.Delivered(Session, HistorySyncType.Recent, 4), await progress.NextAsync(ct));

		// The notification and the history were never live messages: a live one in the notification's own chat, the
		// account's, is the first MessageReceived.
		var own = $"{Session}@s.whatsapp.net";
		_ = await fixture.Fake.DeliverMessageAsync(Deliveries.Message(Session, "L1", own), cancellationToken: ct);
		Assert.Equal(Deliveries.Received(Session, "L1", own), await live.NextAsync(ct));

		// The notification was answered once it was queued, then the blob was downloaded, and deleted last.
		var sent = await Histories.SentUntilAsync(fixture, Session, s => s.MediaDelete is not null, ct);
		Assert.Equal<string>(["receipt N1", $"download {blob.DirectPath}", $"delete {blob.DirectPath}"], sent.Select(Describe).OfType<string>(), StringComparer.Ordinal);
	}

	[Fact(Timeout = Timeout)]
	public async Task HistoryMediaMessage_ArrivesWithTheContentALiveOneHas()
	{
		var ct = TestContext.Current.CancellationToken;
		const string Session = "history-media";
		var client = await Histories.CaughtUpAsync(fixture, Session, ct);
		using var batches = new Received<HistoryReceived>(client);
		var image = new E2E.ImageMessage
		{
			URL = "https://mmg.whatsapp.net/v/history",
			DirectPath = "/v/history",
			MediaKey = ByteString.CopyFrom(1, 2),
			FileSHA256 = ByteString.CopyFrom(3, 4),
			FileEncSHA256 = ByteString.CopyFrom(5, 6),
			FileLength = 7,
			Mimetype = "image/jpeg",
			Caption = "old photo",
		};
		var conversation = new Conversation { Chat = Direct };
		conversation.Messages.Add(new WebMessage { Id = "I1", Message_ = new E2E.Message { ImageMessage = image }, MessageTime = Timestamp.FromDateTimeOffset(Histories.At) });
		conversation.Messages.Add(
			new WebMessage
			{
				Id = "I2",
				Message_ = new E2E.Message { ViewOnceMessageV2 = new E2E.FutureProofMessage { Message = new E2E.Message { ImageMessage = image } } },
				MessageTime = Timestamp.FromDateTimeOffset(Histories.At),
			}
		);
		_ = await fixture.Fake.DeliverHistorySyncAsync(Histories.Blob(Session, "N1", Histories.Recent, conversation), cancellationToken: ct);

		// The MediaRef downloads it lazily, as a live message's does (ARCHITECTURE §7.6).
		var content = new ImageContent
		{
			Media = new MediaRef
			{
				Type = MediaType.Image,
				DirectPath = "/v/history",
				Url = new Uri("https://mmg.whatsapp.net/v/history"),
				MediaKey = [1, 2],
				FileSha256 = [3, 4],
				FileEncSha256 = [5, 6],
				FileLength = 7,
				StreamingSidecar = [],
			},
			Metadata = new ImageMetadata { Mimetype = "image/jpeg", Caption = "old photo" },
			IsViewOnce = false,
		};
		Assert.Equal(
			Histories.Batch(
				Session,
				HistorySyncType.Recent,
				Histories.Text(Direct, "I1") with { Content = content },
				Histories.Text(Direct, "I2") with { Content = content with { IsViewOnce = true } }
			),
			await batches.NextAsync(ct)
		);
	}

	[Fact(Timeout = Timeout)]
	public async Task History_BeforeTheOfflineSync_IsDownloadedOnlyAfterIt()
	{
		var ct = TestContext.Current.CancellationToken;
		const string Session = "history-after-offline-sync";
		var client = await SessionSetup.ConnectedAsync(fixture, Session, ct);
		using var batches = new Received<HistoryReceived>(client);
		using var live = new Received<MessageReceived>(client);

		// WhatsApp sends history while it still sends what the session missed offline.
		var blob = await fixture.Fake.DeliverHistorySyncAsync(Histories.Blob(Session, "N1", Histories.Recent, Histories.Texts(Direct, "A", 1)), cancellationToken: ct);
		_ = await fixture.Fake.DeliverMessageAsync(Deliveries.Message(Session, "L1"), cancellationToken: ct);
		Assert.Equal(Deliveries.Received(Session, "L1"), await live.NextAsync(ct));

		var caughtUp = await Histories.CompleteOfflineSyncAsync(fixture, client, Session, ct);
		Assert.Equal(Histories.Batch(Session, HistorySyncType.Recent, Histories.Text(Direct, "A1")), await batches.NextAsync(ct));

		// Queued, never dropped, and downloaded only once the offline sync completed.
		var sent = await Histories.SentUntilAsync(fixture, Session, s => s.MediaDownload is not null, ct);
		Assert.Equal(blob.DirectPath, sent[^1].MediaDownload.DirectPath);
		Assert.True(sent[^1].Index > caughtUp, $"downloaded at {sent[^1].Index}, before the offline sync completed at {caughtUp}");
	}

	[Fact(Timeout = Timeout)]
	public async Task LiveMessage_DuringAHistoryBacklog_ReachesItsHandlerBeforeTheRestOfTheBacklog()
	{
		var ct = TestContext.Current.CancellationToken;
		string[] sessions = ["history-backlog-1", "history-backlog-2"];
		var held = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var clients = new List<IWhatsAppClient>();
		var received = new List<(Received<HistoryReceived> Batches, Received<HistorySyncProgressed> Progress, Received<MessageReceived> Live)>();
		var heldBatches = 0;
		foreach (var session in sessions)
		{
			var client = await Histories.CaughtUpAsync(fixture, session, ct);
			clients.Add(client);
			received.Add(
				(
					new Received<HistoryReceived>(
						client,
						async (_, token) =>
						{
							if (Interlocked.Increment(ref heldBatches) == sessions.Length)
								held.SetResult();
							await release.Task.WaitAsync(token).ConfigureAwait(false);
						}
					),
					new Received<HistorySyncProgressed>(client),
					new Received<MessageReceived>(client)
				)
			);

			// Six batches, in the chat the live message comes in.
			_ = await fixture.Fake.DeliverHistorySyncAsync(
				Histories.Blob(session, "N1", Histories.Full, Histories.Texts(Deliveries.Chat, "M", 6 * Histories.BatchMessages)),
				cancellationToken: ct
			);
		}

		// Every session's handler holds its first batch: the backlog is draining.
		await held.Task.WaitAsync(ct);

		foreach (var (session, (_, _, live)) in sessions.Zip(received))
		{
			_ = await fixture.Fake.DeliverMessageAsync(Deliveries.Message(session, "L1"), cancellationToken: ct);
			Assert.Equal(Deliveries.Received(session, "L1"), await live.NextAsync(ct));
		}

		// The live messages arrived while each session's history handler still held its first batch.
		Assert.Equal(sessions.Length, Volatile.Read(ref heldBatches));
		release.SetResult();

		foreach (var (session, (batches, progress, live)) in sessions.Zip(received))
		{
			var all = Histories.MessagesOf(await batches.NextAsync(6, ct));
			Assert.Equal(Enumerable.Range(1, 6 * Histories.BatchMessages).Select(i => Histories.Text(Deliveries.Chat, $"M{i}")), all);
			Assert.Equal(Histories.Delivered(session, HistorySyncType.Full, 6 * Histories.BatchMessages), await progress.NextAsync(ct));
			batches.Dispose();
			progress.Dispose();
			live.Dispose();
		}
	}

	[Fact(Timeout = Timeout)]
	public async Task ExpiredBlob_IsReportedAndTheNextArrives()
	{
		var ct = TestContext.Current.CancellationToken;
		const string Session = "history-expired";
		var client = await SessionSetup.ConnectedAsync(fixture, Session, ct);
		using var batches = new Received<HistoryReceived>(client);
		using var progress = new Received<HistorySyncProgressed>(client);

		var expired = await fixture.Fake.DeliverHistorySyncAsync(Histories.Blob(Session, "N1", Histories.Recent, Histories.Texts(Direct, "A", 1)), cancellationToken: ct);
		_ = await fixture.Fake.ExpireMediaAsync(new ExpireMediaRequest { DirectPath = expired.DirectPath, Status = 404 }, cancellationToken: ct);
		_ = await fixture.Fake.DeliverHistorySyncAsync(Histories.Blob(Session, "N2", Histories.Recent, Histories.Texts(Direct, "B", 1)), cancellationToken: ct);
		_ = await Histories.CompleteOfflineSyncAsync(fixture, client, Session, ct);

		Assert.Equal(Histories.Delivered(Session, HistorySyncType.Recent, 0) with { Failure = HistorySyncFailure.Expired }, await progress.NextAsync(ct));
		Assert.Equal(Histories.Batch(Session, HistorySyncType.Recent, Histories.Text(Direct, "B1")), await batches.NextAsync(ct));
		Assert.Equal(Histories.Delivered(Session, HistorySyncType.Recent, 1), await progress.NextAsync(ct));
	}

	[Fact(Timeout = Timeout)]
	public async Task RequestHistory_ThePhonesAnswerArrivesAsOnDemandHistoryForTheChat()
	{
		var ct = TestContext.Current.CancellationToken;
		const string Session = "history-on-demand";
		var client = await Histories.CaughtUpAsync(fixture, Session, ct);
		using var batches = new Received<HistoryReceived>(client);
		var oldest = DateTimeOffset.FromUnixTimeSeconds(1_650_000_000);

		await client.RequestHistoryAsync(Jid.Parse(Group), MessageId.From("OLD1"), oldestIsFromMe: true, oldest, 30, ct);

		var sent = (await Histories.SentUntilAsync(fixture, Session, s => s.Message is not null, ct))[^1].Message;
		var request = sent.Message.ProtocolMessage.PeerDataOperationRequestMessage;
		Assert.Equal(
			($"{Session}@s.whatsapp.net", Group, "OLD1", true, 1_650_000_000L, 30),
			(
				sent.Chat,
				request.HistorySyncOnDemandRequest.ChatJID,
				request.HistorySyncOnDemandRequest.OldestMsgID,
				request.HistorySyncOnDemandRequest.OldestMsgFromMe,
				request.HistorySyncOnDemandRequest.OldestMsgTimestampMS,
				request.HistorySyncOnDemandRequest.OnDemandMsgCount
			)
		);

		var answer = new Conversation { Chat = Group };
		answer.Messages.Add(new WebMessage { Id = "OLDER1", Participant = Member, Text = "older", MessageTime = Timestamp.FromDateTimeOffset(Histories.At) });
		_ = await fixture.Fake.DeliverHistorySyncAsync(Histories.Blob(Session, "N1", Histories.OnDemand, answer), cancellationToken: ct);

		Assert.Equal(
			Histories.Batch(
				Session,
				HistorySyncType.OnDemand,
				new HistoryMessage
				{
					Id = MessageId.From("OLDER1"),
					Source = new MessageSource { Chat = Jid.Parse(Group), Sender = Jid.Parse(Member), SenderAlt = null, RecipientAlt = null, IsFromMe = false },
					PushName = null,
					Timestamp = Histories.At,
					Content = new TextContent { Text = "older" },
				}
			),
			await batches.NextAsync(ct)
		);
	}

	[Fact(Timeout = Timeout)]
	public async Task RequestHistory_NotConnected_Throws()
	{
		var ct = TestContext.Current.CancellationToken;
		var client = await SessionSetup.LinkedAsync(fixture, "history-request-offline", ct);

		_ = await Assert.ThrowsAsync<NotConnectedException>(() => client.RequestHistoryAsync(Jid.Parse(Direct), MessageId.From("OLD1"), oldestIsFromMe: false, Histories.At, cancellationToken: ct));
		_ = await Assert.ThrowsAsync<WhatsmeowValidationException>(() =>
			client.RequestHistoryAsync(Jid.Parse("status@broadcast"), MessageId.From("OLD1"), oldestIsFromMe: false, Histories.At, cancellationToken: ct)
		);
	}

	[Fact(Timeout = Timeout)]
	public async Task LargeBlob_ArrivesInBatchesThatEachFitAGrpcMessage()
	{
		var ct = TestContext.Current.CancellationToken;
		const string Session = "history-large";
		var client = await Histories.CaughtUpAsync(fixture, Session, ct);
		using var batches = new Received<HistoryReceived>(client);
		using var progress = new Received<HistorySyncProgressed>(client);

		// Twenty messages of a MiB each: a blob above the 16 MiB a gRPC message may carry (ARCHITECTURE §8.6).
		const int Pad = 1 << 20;
		var request = Histories.Blob(Session, "N1", Histories.Full, Histories.Texts(Direct, "B", 20));
		request.PadText = Pad;
		_ = await fixture.Fake.DeliverHistorySyncAsync(request, cancellationToken: ct);

		var delivered = await progress.NextAsync(ct);
		Assert.Equal(Histories.Delivered(Session, HistorySyncType.Full, 20), delivered);

		var received = new List<HistoryReceived>();
		while (received.Sum(b => b.Messages.Length) < 20)
			received.Add(await batches.NextAsync(ct));
		Assert.True(received.Count > 1, $"{received.Count} batch for 20 MiB");
		Assert.Equal(
			Enumerable.Range(1, 20).Select(i => Histories.Text(Direct, $"B{i}") with { Content = new TextContent { Text = $"text B{i}" + new string('x', Pad) } }),
			Histories.MessagesOf(received)
		);
	}

	/// <summary>A blob's entry of the fake's log, or null for any other.</summary>
	private static string? Describe(Sent sent)
	{
		if (sent.Receipt is { Id: "N1" })
			return "receipt N1";
		if (sent.MediaDownload is { } download)
			return $"download {download.DirectPath}";
		return sent.MediaDelete is { } delete ? $"delete {delete.DirectPath}" : null;
	}
}
