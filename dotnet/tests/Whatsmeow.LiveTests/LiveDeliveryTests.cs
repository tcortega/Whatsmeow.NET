using System.Diagnostics;
using System.Globalization;
using Whatsmeow.Events;
using Whatsmeow.IntegrationTests.Events;
using Whatsmeow.IntegrationTests.Support;
using Whatsmeow.Sessions;

namespace Whatsmeow.LiveTests;

/// <summary>
/// The Phase 1 exit (#10): real WhatsApp, either process killed mid-stream, no lost messages, with the fake-mode kill
/// tests' own checks (<see cref="DeliveryChecks"/>). The peer (<see cref="LivePeer"/>) sends each burst to the live
/// account (<see cref="LiveSidecar.AccountAsync"/>). Every timing goes to <c>sidecar.log</c> and the test output, for
/// ARCHITECTURE §14.
/// </summary>
[Collection(nameof(Live))]
public sealed class LiveDeliveryTests(LiveSidecar live, LivePeer peer)
{
	/// <summary>How long a burst may take to arrive after its last send, and the wait for repeats after it.</summary>
	private static readonly TimeSpan s_arrivalBound = TimeSpan.FromMinutes(5);

	private static readonly TimeSpan s_repeatGrace = TimeSpan.FromSeconds(30);

	/// <summary>
	/// 20 messages; the handler of the 8th holds while the sidecar is killed, the peer goes on, and the client
	/// restarts the sidecar. The client keeps its place across a sidecar restart, so each message comes exactly once,
	/// as in the fake-mode <c>EventCrashTests</c>: a repeat here would be one of an acked event, which the design rules
	/// out.
	/// </summary>
	[LiveFact(NeedsPeer = true)]
	public async Task Live_TheSidecarIsKilledMidStream_EveryMessageReachesTheHandlersOnce()
	{
		var ct = TestContext.Current.CancellationToken;
		var (account, _) = await live.AccountAsync(ct);
		var from = await peer.ConnectedAsync(live, ct);
		var client = await live.LiveAsync(ct);
		var burst = Burst.Of("sidecar-kill", 20);
		var holdOn = burst.Messages[7].Id;
		var held = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		using var arrivals = new Arrivals(
			client,
			async (e, token) =>
			{
				if (e.Id != holdOn)
					return;
				_ = held.TrySetResult();
				await release.Task.WaitAsync(token).ConfigureAwait(false);
			}
		);
		var restartedAt = TimeSpan.Zero;
		using var states = client.On<ConnectionStateChanged>(
			(e, _) =>
			{
				if (e.State is SessionState.Connected && held.Task.IsCompleted)
					restartedAt = LiveClock.Time.GetElapsedTime(arrivals.Origin);
				return ValueTask.CompletedTask;
			}
		);

		var sending = LivePeer.SendAsync(from, account, burst, ct);
		await held.Task.WaitAsync(TimeSpan.FromMinutes(2), LiveClock.Time, ct);
		var killedAt = LiveClock.Time.GetElapsedTime(arrivals.Origin);
		await live.CrashAsync(ct);
		release.SetResult();
		_ = await sending;

		var received = await arrivals.CollectAsync(burst, [.. burst.ByText.Keys], s_arrivalBound, s_repeatGrace, ct);
		var afterKill = received.Where(a => a.At > killedAt).ToList();
		live.Write(
			$"live: sidecar kill at {killedAt.TotalSeconds:F1} s, Connected again at {restartedAt.TotalSeconds:F1} s; {afterKill.Count} of {received.Count} arrivals after the kill, "
				+ $"the first at {At(afterKill, 0)} s, the last at {At(afterKill, afterKill.Count - 1)} s"
		);
		DeliveryChecks.ValidateEachOnce(burst.ByText, [.. received.Select(a => a.Message)], client.SessionId);
	}

	/// <summary>
	/// The .NET side as a child process (<c>SidecarHost events-live</c>) on the live database: it handles the burst,
	/// acks once exactly the messages before the 4th, holds in the 4th's handler and is killed there; its sidecar ends
	/// with it. The next process gets the 4th and every later one, with the same ids, and none of the acked ones.
	/// </summary>
	[LiveFact(NeedsPeer = true)]
	public async Task Live_TheDotNetProcessIsKilledMidStream_TheMessagesAfterTheAckComeAgainWithTheSameIds()
	{
		var ct = TestContext.Current.CancellationToken;
		var (account, _) = await live.AccountAsync(ct);
		var from = await peer.ConnectedAsync(live, ct);
		var binary = await live.BinaryAsync(ct);
		var burst = Burst.Of("dotnet-kill", 10);
		var blockOn = burst.Messages[3].Id;

		// The live database has one owner: this process gives it to the host, and takes it back after the kill.
		await live.StopAsync();
		var host = Path.Combine(AppContext.BaseDirectory, OperatingSystem.IsWindows() ? "SidecarHost.exe" : "SidecarHost");
		var info = new ProcessStartInfo(host) { UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
		foreach (var argument in new[] { "events-live", binary, LiveSettings.Database, LiveSidecar.LiveSessionId, blockOn.Value })
			info.ArgumentList.Add(argument);

		var handledBefore = new List<MessageId>();
		var started = LiveClock.Time.GetTimestamp();
		// The host's log, redacted, beside the live sidecar's: read as it comes, so a full pipe never blocks the host.
		using var hostLog = new LiveLog(Path.Combine(live.OutputDirectory, "host.log"));
		using (var killed = Process.Start(info)!)
		{
			killed.ErrorDataReceived += (_, e) =>
			{
				if (e.Data is not null)
					hostLog.Write(e.Data);
			};
			killed.BeginErrorReadLine();
			try
			{
				using var bound = LiveClock.Bound(TimeSpan.FromMinutes(3), ct);
				var line = await killed.StandardOutput.ReadLineAsync(bound.Token);
				if (!int.TryParse(line, CultureInfo.InvariantCulture, out var sidecar))
					Assert.Fail($"the host printed no sidecar process id: {line} (its log is host.log)");

				var sending = LivePeer.SendAsync(from, account, burst, ct);
				// "session id" for each message; the account's other traffic is printed too.
				var (handled, ackedSeq) = await EventsHostOutput.UntilAckedAsync(killed.StandardOutput, bound.Token);
				handledBefore.AddRange(handled.Select(label => MessageId.From(label.Split(' ')[1])).Where(id => burst.Messages.Any(m => m.Id == id)));
				live.Write($"live: the killed process acked up to seq {ackedSeq}");

				// The host alone, as a crash would: its sidecar stops on its own when stdin closes.
				killed.Kill();
				await killed.WaitForExitAsync(ct);
				await Processes.WaitUntilGoneAsync(sidecar, ct);
				_ = await sending;
			}
			finally
			{
				// A failure above must not leave the host or its sidecar running on the live database.
				killed.Kill(entireProcessTree: true);
			}
		}

		live.Write($"live: the .NET side was killed {LiveClock.Time.GetElapsedTime(started).TotalSeconds:F1} s after it started, holding message 4 of {burst.Messages.Count}");
		live.Write($"live: the killed process handled, in order, message {string.Join(", ", handledBefore.Select(id => burst.Messages.ToList().FindIndex(m => m.Id == id) + 1))}");
		Assert.Equal(blockOn, handledBefore[^1]);
		var acked = handledBefore[..^1];

		// Registered before the connect, so the outbox's backlog reaches it.
		var again = await live.SessionAsync(LiveSidecar.LiveSessionId, ct);
		using var arrivals = new Arrivals(again);
		_ = await live.LiveAsync(ct);
		var expected = burst.Messages.Where(m => !acked.Contains(m.Id)).Select(m => m.Text).ToList();
		var received = await arrivals.CollectAsync(burst, expected, s_arrivalBound, s_repeatGrace, ct);

		live.Write($"live: after the restart {received.Count} arrivals, the first at {At(received, 0)} s, the last at {At(received, received.Count - 1)} s");
		DeliveryChecks.ValidateRepeatsFollowTheAck(burst.ByText, acked, [.. received.Select(a => a.Message)], again.SessionId);
	}

	/// <summary>
	/// How long WhatsApp takes to deliver a message again whose answer was withheld, as a failed outbox write withholds
	/// it (ARCHITECTURE §7.3) [unknown before this test]: the live seam withholds the first answer to a marked message
	/// (ADR 0032). The session stays connected for up to ten minutes; if nothing came by then, it reconnects and measures
	/// from Connected. Either way the message must come, with its id.
	/// </summary>
	[LiveFact(NeedsPeer = true, Timeout = 1_200_000)]
	public async Task Live_AWithheldAnswer_WhatsAppDeliversTheMessageAgain_TheTimeIsRecorded()
	{
		var ct = TestContext.Current.CancellationToken;
		var (account, _) = await live.AccountAsync(ct);
		var from = await peer.ConnectedAsync(live, ct);
		var client = await live.LiveAsync(ct);
		var id = MessageId.New();
		var text = $"{WithholdPrefix}{id}";
		using var arrivals = new Arrivals(client);
		var sent = await from.SendTextAsync(account, text, id, ct);

		var withheld = await live.Logs.WaitForAsync(r => r.Message.Contains(WithheldLine, StringComparison.Ordinal) && r.Message.Contains($"\"message_id\":\"{id}\"", StringComparison.Ordinal), ct)
			.WaitAsync(TimeSpan.FromMinutes(2), LiveClock.Time, ct);
		var withheldAt = LiveClock.Time.GetElapsedTime(arrivals.Origin);
		live.Write($"live: the answer to {id} was withheld at {withheldAt.TotalSeconds:F1} s ({withheld.Timestamp:O}); sent at {sent.Timestamp:O}");

		var burst = Burst.One(text, id);
		var connected = await arrivals.CollectAsync(burst, [text], TimeSpan.FromMinutes(10), TimeSpan.Zero, ct);
		if (connected.Count > 0)
		{
			live.Write($"live: delivered again while connected, {(connected[0].At - withheldAt).TotalSeconds:F1} s after the withheld answer");
			DeliveryChecks.ValidateEachOnce(burst.ByText, [.. connected.Select(a => a.Message)], client.SessionId);
			return;
		}

		live.Write("live: not delivered again within 10 min while connected; reconnecting");
		await client.DisconnectAsync(ct);
		var reconnecting = LiveClock.Time.GetElapsedTime(arrivals.Origin);
		_ = await live.LiveAsync(ct);
		var reconnected = await arrivals.CollectAsync(burst, [text], TimeSpan.FromMinutes(3), TimeSpan.Zero, ct);
		live.Write(
			reconnected.Count > 0
				? $"live: delivered again {(reconnected[0].At - reconnecting).TotalSeconds:F1} s after the reconnect began"
				: "live: not delivered again within 3 min of the reconnect"
		);
		DeliveryChecks.ValidateEachOnce(burst.ByText, [.. reconnected.Select(a => a.Message)], client.SessionId);
	}

	/// <summary>When the <paramref name="index"/>th arrival came, in seconds, or "none".</summary>
	private static string At(IReadOnlyList<(TimeSpan At, MessageReceived Message)> arrivals, int index) =>
		index >= 0 && index < arrivals.Count ? arrivals[index].At.TotalSeconds.ToString("F1", CultureInfo.InvariantCulture) : "none";

	/// <summary>The live seam's marker (sidecar/internal/events/livetest_on.go WithholdPrefix).</summary>
	private const string WithholdPrefix = "wmn-withhold ";

	/// <summary>The live seam's log message when it withholds an answer.</summary>
	private const string WithheldLine = "livetest: the answer to this message is withheld once";
}
