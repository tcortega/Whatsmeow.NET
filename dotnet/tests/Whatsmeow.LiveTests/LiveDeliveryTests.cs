using System.Collections.Concurrent;
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
	/// 20 messages. The peer sends the first 10, and the handler of the 8th holds while the sidecar is killed; once the
	/// session is Connected again on the restarted sidecar, the peer sends the other 10, so they can only come through
	/// it, as M6 does in the fake-mode <c>EventCrashTests</c>. The client keeps its place across a sidecar restart, so
	/// each message comes exactly once, in order: a repeat would be one of an acked event, which the design rules out.
	/// Each send's time relative to the kill goes to the log.
	/// </summary>
	[LiveFact(NeedsPeer = true, Timeout = KillTimeout)]
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
		var killed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var reconnected = new TaskCompletionSource<TimeSpan>(TaskCreationOptions.RunContinuationsAsynchronously);
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
		using var states = client.On<SessionStateChanged>(
			(e, token) =>
			{
				if (e.State is SessionState.Connected && killed.Task.IsCompleted)
					_ = reconnected.TrySetResult(LiveClock.Time.GetElapsedTime(arrivals.Origin));
				return ValueTask.CompletedTask;
			}
		);

		var sending = LivePeer.SendAsync(from, account, burst.Messages.Take(10), ct);
		await held.Task.WaitAsync(TimeSpan.FromMinutes(2), LiveClock.Time, ct);
		var killedAt = LiveClock.Time.GetElapsedTime(arrivals.Origin);
		killed.SetResult();
		await live.CrashAsync(ct);
		release.SetResult();
		var restartedAt = TimeSpan.Zero;
		try
		{
			restartedAt = await reconnected.Task.WaitAsync(TimeSpan.FromMinutes(2), LiveClock.Time, ct);
		}
		catch (TimeoutException)
		{
			Assert.Fail("the session was not Connected again within 2 min of the sidecar kill");
		}

		Assert.True(restartedAt > killedAt, $"Connected again at {restartedAt.TotalSeconds:F1} s, not after the kill at {killedAt.TotalSeconds:F1} s");
		var sent = (await sending).Concat(await LivePeer.SendAsync(from, account, burst.Messages.Skip(10), ct)).ToList();
		live.Write(
			$"live: sidecar kill at {killedAt.TotalSeconds:F1} s, Connected again at {restartedAt.TotalSeconds:F1} s; each send returned, relative to the kill: "
				+ string.Join(", ", sent.Select((s, k) => $"#{k + 1} {(LiveClock.Time.GetElapsedTime(arrivals.Origin, s.SentAt) - killedAt).TotalSeconds:+0.0;-0.0} s"))
		);

		var received = await arrivals.CollectAsync(burst, [.. burst.ByText.Keys], s_arrivalBound, s_repeatGrace, ct);
		var afterKill = received.Where(a => a.At > killedAt).ToList();
		live.Write(
			$"live: {afterKill.Count} of {received.Count} arrivals after the kill, the first at {At(afterKill, 0)} s, the last at {At(afterKill, afterKill.Count - 1)} s"
		);
		DeliveryChecks.ValidateEachOnce(burst.Messages, [.. received.Select(a => a.Message)], client.SessionId);
	}

	/// <summary>
	/// The .NET side as a child process (<c>SidecarHost events-live</c>) on the live database: it handles the burst
	/// in order, acks exactly the messages before the 4th, holds in the 4th's handler and is killed there; its sidecar
	/// ends with it. The next process gets the 4th and every later one, in order, with the same ids, and none of the
	/// acked ones.
	/// </summary>
	[LiveFact(NeedsPeer = true, Timeout = KillTimeout)]
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

				var sending = LivePeer.SendAsync(from, account, burst.Messages, ct);
				// "session id" for each message; the account's other traffic is printed too, and only the burst's labels count.
				var labels = burst.Messages.ToDictionary(m => $"{LiveSidecar.LiveSessionId} {m.Id}", m => m.Id, StringComparer.Ordinal);
				var (handled, ackedSeq) = await EventsHostOutput.UntilAckedAsync(killed.StandardOutput, bound.Token);
				Assert.Matches("^[0-9]+$", ackedSeq);
				handledBefore.AddRange(handled.Where(labels.ContainsKey).Select(label => labels[label]));
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
		Assert.Equal(burst.Messages.Take(4).Select(m => m.Id), handledBefore);
		var acked = handledBefore[..^1];

		// Registered before the connect, so the outbox's backlog reaches it.
		var again = await live.SessionAsync(LiveSidecar.LiveSessionId, ct);
		using var arrivals = new Arrivals(again);
		_ = await live.LiveAsync(ct);
		var expected = burst.Messages.Where(m => !acked.Contains(m.Id)).Select(m => m.Text).ToList();
		var received = await arrivals.CollectAsync(burst, expected, s_arrivalBound, s_repeatGrace, ct);

		live.Write($"live: after the restart {received.Count} arrivals, the first at {At(received, 0)} s, the last at {At(received, received.Count - 1)} s");
		DeliveryChecks.ValidateRepeatsFollowTheAck(burst.Messages, acked, [.. received.Select(a => a.Message)], again.SessionId);
	}

	/// <summary>
	/// A message whose answer was withheld, as a failed outbox write withholds it (ARCHITECTURE §7.3), comes again within
	/// a minute, with its id, and the test never reconnects: the live seam withholds the first answer to a marked message
	/// (ADR 0032), and the sidecar resets the connection 5 s later, so WhatsApp delivers it again at the login (ADR 0044,
	/// #91). The test logs what the reset costs: when the sidecar reset, how long the session was not logged in, when it
	/// was caught up again, and when the message came.
	/// </summary>
	[LiveFact(NeedsPeer = true, Timeout = 600_000)]
	public async Task Live_AWithheldAnswer_TheSidecarResetsTheConnection_TheMessageComesAgainWithinAMinute()
	{
		var ct = TestContext.Current.CancellationToken;
		var (account, _) = await live.AccountAsync(ct);
		var from = await peer.ConnectedAsync(live, ct);
		var client = await live.LiveAsync(ct);
		var id = MessageId.New();
		var text = $"{WithholdPrefix}{id}";
		using var arrivals = new Arrivals(client);
		var changes = new ConcurrentQueue<(TimeSpan At, SessionState State)>();
		using var states = client.On<SessionStateChanged>(
			(e, token) =>
			{
				changes.Enqueue((LiveClock.Time.GetElapsedTime(arrivals.Origin), e.State));
				return ValueTask.CompletedTask;
			}
		);
		var sent = await from.SendTextAsync(account, text, id, cancellationToken: ct);

		var withheld = await live.Logs.WaitForAsync(r => r.Message.Contains(WithheldLine, StringComparison.Ordinal) && r.Message.Contains($"\"message_id\":\"{id}\"", StringComparison.Ordinal), ct)
			.WaitAsync(TimeSpan.FromMinutes(2), LiveClock.Time, ct);
		var withheldAt = LiveClock.Time.GetElapsedTime(arrivals.Origin);
		live.Write($"live: the answer to {id} was withheld at {withheldAt.TotalSeconds:F1} s ({withheld.Timestamp:O}); sent at {sent.Timestamp:O}");

		var burst = Burst.One(text, id);
		var again = await arrivals.CollectAsync(burst, [text], s_redeliveryBound, TimeSpan.Zero, ct);
		var reset = live.Logs.Collector.GetSnapshot().FirstOrDefault(r => r.Timestamp >= withheld.Timestamp && r.Message.Contains(ResetLine, StringComparison.Ordinal));
		live.Write(
			$"live: the sidecar reset the connection at {(reset is null ? "no time: no reset logged" : reset.Timestamp.ToString("O", CultureInfo.InvariantCulture))}; "
				+ $"the states after the withheld answer: {string.Join(", ", changes.Where(c => c.At >= withheldAt).Select(c => $"{c.State} at {c.At.TotalSeconds:F1} s"))}"
		);
		live.Write(
			again.Count > 0
				? $"live: delivered again {(again[0].At - withheldAt).TotalSeconds:F1} s after the withheld answer, with no reconnect by the test"
				: $"live: not delivered again within {s_redeliveryBound.TotalSeconds:F0} s of the withheld answer"
		);
		DeliveryChecks.ValidateEachOnce(burst.Messages, [.. again.Select(a => a.Message)], client.SessionId);
		Assert.Contains(changes, c => c.At >= withheldAt && c.State is SessionState.Reconnecting);
	}

	/// <summary>
	/// How long the sidecar may take to have a withheld message delivered again: its 5 s settle delay, the reset's
	/// login, 1.1 to 1.4 s in the #10 runs (ARCHITECTURE §14), and room for a slow network.
	/// </summary>
	private static readonly TimeSpan s_redeliveryBound = TimeSpan.FromMinutes(1);

	/// <summary>
	/// A kill test's timeout, well above the sum of its bounds (a peer connect and the live one, the hold or the host, the
	/// restart, the arrivals and the grace), so a lost message fails with its own message, not the test's timeout.
	/// </summary>
	private const int KillTimeout = 1_800_000;

	/// <summary>When the <paramref name="index"/>th arrival came, in seconds, or "none".</summary>
	private static string At(IReadOnlyList<(TimeSpan At, MessageReceived Message)> arrivals, int index) =>
		index >= 0 && index < arrivals.Count ? arrivals[index].At.TotalSeconds.ToString("F1", CultureInfo.InvariantCulture) : "none";

	/// <summary>The live seam's marker (sidecar/internal/events/livetest_on.go WithholdPrefix).</summary>
	private const string WithholdPrefix = "wmn-withhold ";

	/// <summary>The live seam's log message when it withholds an answer.</summary>
	private const string WithheldLine = "livetest: the answer to this message is withheld once";

	/// <summary>The sidecar's log message when it resets a connection after a withheld answer (session/redelivery.go).</summary>
	private const string ResetLine = "an answer to WhatsApp was withheld; resetting the connection";
}
