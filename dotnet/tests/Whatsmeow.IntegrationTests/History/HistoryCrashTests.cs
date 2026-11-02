using Whatsmeow.History;
using Whatsmeow.IntegrationTests.Events;
using Whatsmeow.IntegrationTests.Support;
using Whatsmeow.Sessions;

namespace Whatsmeow.IntegrationTests.History;

/// <summary>History across a crash of the shared sidecar with a blob half processed (ADR 0035, invariants 2 and 3).</summary>
[Collection(nameof(SidecarCrashes))]
public sealed class HistoryCrashTests(FakeSidecar fixture)
{
	private const int Timeout = 120_000;

	[Fact(Timeout = Timeout)]
	public async Task History_TheSidecarIsKilledWithABlobHalfProcessed_EveryMessageArrives_AndTheBlobIsDeletedAfterwards()
	{
		var ct = TestContext.Current.CancellationToken;
		const string Session = "history-crash";
		const int Messages = 6 * Histories.BatchMessages;
		var client = await Histories.CaughtUpAsync(fixture, Session, ct);
		var held = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		using var batches = new Received<HistoryReceived>(
			client,
			async (batch, token) =>
			{
				_ = held.TrySetResult();
				await release.Task.WaitAsync(token).ConfigureAwait(false);
			}
		);
		using var progress = new Received<HistorySyncProgressed>(client);

		var blob = await fixture.Fake.DeliverHistorySyncAsync(
			Histories.Blob(Session, "N1", Histories.Full, Histories.Texts(Deliveries.Chat, "M", Messages)),
			cancellationToken: ct
		);

		// The first batch is held unacked, so the blob stops half processed: a window of batches recorded, the rest not.
		await held.Task.WaitAsync(ct);
		await fixture.CrashAsync(ct);
		release.SetResult();

		// The restarted sidecar's fake starts a new log; the client connects the session again.
		var watch = StateWatch.Start(client, ct);
		await using (watch.ConfigureAwait(false))
			await watch.ReachesAsync(new SessionState.Connected(), new SessionState.Disconnected(), new SessionState.Connecting(), new SessionState.Reconnecting());
		_ = await Histories.CompleteOfflineSyncAsync(fixture, client, Session, ct);

		Assert.Equal(Histories.Delivered(Session, HistorySyncType.Full, Messages), await progress.NextAsync(ct));

		// Every message arrived; one that came again carries the same id and session.
		var received = new List<HistoryReceived>();
		while (Histories.MessagesOf(received).Distinct().Take(Messages).Count() != Messages)
			received.Add(await batches.NextAsync(ct));
		var all = Histories.MessagesOf(received);
		Assert.All(received, b => Assert.Equal(SessionId.From(Session), b.SessionId));
		Assert.Equal(Enumerable.Range(1, Messages).Select(i => Histories.Text(Deliveries.Chat, $"M{i}")), all.Distinct());

		// Downloaded again after the restart, and deleted only once every message was recorded.
		var sent = await Histories.SentUntilAsync(fixture, Session, s => s.MediaDelete is not null, ct);
		Assert.Equal(blob.DirectPath, sent[^1].MediaDelete.DirectPath);
		Assert.Contains(sent, s => s.MediaDownload?.DirectPath == blob.DirectPath);
	}
}
