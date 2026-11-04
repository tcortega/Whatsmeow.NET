using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Testing;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Whatsmeow;
using Whatsmeow.Events;
using Whatsmeow.Groups;
using Whatsmeow.Sessions;

namespace SidecarHost;

/// <summary>
/// The process the events kill tests kill: it starts the sidecar on the database (the fake-mode build, or with
/// <c>fakeWhatsApp</c> false a release build against real WhatsApp, for the live test), prints the sidecar's
/// process id, connects the session and prints "session id" as each message handler starts, and "session name" as each
/// handler of a group rename to name does. In the handler of <c>blockOn</c>, a message id or a name, it waits until the
/// session's state lane handled <see cref="SessionState.Connected"/>, moves its clock one ack interval at a time until
/// the acks settle, prints "acked" and the last acked seq, and never returns; the test kills this process there. Its
/// clock moves only then, so the acks cover exactly the events before <c>blockOn</c>. When its stdin closes it exits
/// at once.
/// </summary>
/// <remarks>
/// The state lane runs beside the chat's lane, and the session's state events before the first message hold the
/// watermark until that lane ran them: a tick before that acks nothing, and no tick comes after it (#61). This
/// process's connect records <see cref="SessionState.Connected"/> after the messages, and a lane runs its events one
/// after the other, so once that handler runs, every state event before the messages finished.
/// The clock is a <see cref="FakeTimeProvider"/> that only <see cref="SettledAckAsync"/> moves, and it is the client's
/// every clock: it also freezes the sidecar's <c>StartupTimeout</c>, the event stream's retry delay, the supervisor's
/// restart backoff and the stop's grace. A start that hangs or a stream that fails therefore waits for the test's own
/// bound, which kills this process.
/// </remarks>
internal static class EventsHost
{
	public static async Task RunAsync(string sidecarPath, string database, string session, string blockOn, bool fakeWhatsApp)
	{
		// The live host's log goes to stderr, which the live test redacts into host.log; the fake one's stays here.
		var logs = new FakeLogCollector(Options.Create(new FakeLogCollectorOptions { OutputSink = fakeWhatsApp ? null : Console.Error.WriteLine }));
		using var provider = new FakeLoggerProvider(logs);
		using var loggerFactory = LoggerFactory.Create(builder => builder.AddProvider(provider).SetMinimumLevel(LogLevel.Debug));
		var time = new FakeTimeProvider();

		var options = new WhatsmeowSidecarOptions { SidecarPath = sidecarPath, DatabasePath = database };
		var sidecar = fakeWhatsApp
			? await WhatsmeowSidecar.StartFakeWhatsAppAsync(options, loggerFactory, time, CancellationToken.None).ConfigureAwait(false)
			: await WhatsmeowSidecar.StartAsync(options, loggerFactory, time, CancellationToken.None).ConfigureAwait(false);
		await using (sidecar.ConfigureAwait(false))
		{
			await PrintAsync(logs.GetSnapshot().First(r => r.Id.Name == "SidecarStarted").GetStructuredStateValue("ProcessId")).ConfigureAwait(false);

			var stuck = new TaskCompletionSource();
			var connected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
			var client = await sidecar.Sessions.GetOrCreateAsync(SessionId.From(session), CancellationToken.None).ConfigureAwait(false);
			using var states = client.On<SessionStateChanged>(
				(e, token) =>
				{
					if (e.State is SessionState.Connected)
						_ = connected.TrySetResult();
					return ValueTask.CompletedTask;
				}
			);
			async Task HandleAsync(string label, bool block, CancellationToken token)
			{
				await PrintAsync(label).ConfigureAwait(false);
				if (!block)
					return;

				await connected.Task.WaitAsync(token).ConfigureAwait(false);
				await PrintAsync($"acked {await SettledAckAsync(logs, time, token).ConfigureAwait(false)}").ConfigureAwait(false);
				await stuck.Task.WaitAsync(token).ConfigureAwait(false);
			}

			using var handler = client.On<MessageReceived>((e, token) => new ValueTask(HandleAsync($"{e.SessionId} {e.Id}", e.Id == MessageId.From(blockOn), token)));
			using var renames = client.On<GroupInfoChanged>(
				(e, token) =>
					e.Changes is [GroupChange.Renamed { Name: var name }]
						? new ValueTask(HandleAsync($"{e.SessionId} {name}", name == blockOn, token))
						: ValueTask.CompletedTask
			);
			await client.ConnectAsync(CancellationToken.None).ConfigureAwait(false);
			_ = await Console.In.ReadLineAsync().ConfigureAwait(false);

			// The test that started this process is gone or done with it: end at once, as a crash would. A graceful stop
			// would wait for the stuck handler and keep the session connected.
#pragma warning disable RS0030 // Justification: a test host process, not the library; it must not outlive its test.
			Environment.Exit(1);
#pragma warning restore RS0030
		}
	}

	/// <summary>
	/// Moves the clock one ack interval at a time until an interval acks nothing new within <see cref="s_quiet"/> of
	/// real time, and returns the last acked seq. The watermark spans the session's lanes, so a slow lane of other
	/// traffic can hold it back for a tick; the blocked handler holds its own lane, so it settles just below it.
	/// There is no exact signal to wait for instead: the seq just below the blocked event is the target, but the public
	/// events carry no seq, and the client logs only the seq it acked, not the one it could ack next. So a quiet window
	/// stands in for "every other lane finished": a tick that acks nothing for <see cref="s_quiet"/> means none moved.
	/// </summary>
	private static async Task<string> SettledAckAsync(FakeLogCollector logs, FakeTimeProvider time, CancellationToken cancellationToken)
	{
		for (var tick = 0; tick < MaxTicks; tick++)
		{
			var seen = Acks(logs).Count;
			time.Advance(TimeSpan.FromSeconds(1));
			if (!await NewAckAsync(logs, seen, cancellationToken).ConfigureAwait(false) && seen > 0)
				break;
		}

		return Acks(logs) is [.., var last] ? last.GetStructuredStateValue("Seq") ?? "" : throw new InvalidOperationException("nothing was acked");
	}

	/// <summary>Whether an Acked record after the first <paramref name="seen"/> came within <see cref="s_quiet"/>.</summary>
	private static async Task<bool> NewAckAsync(FakeLogCollector logs, int seen, CancellationToken cancellationToken)
	{
		using var quiet = new CancellationTokenSource(s_quiet, RealTime);
		using var linked = CancellationTokenSource.CreateLinkedTokenSource(quiet.Token, cancellationToken);
		var records = logs.GetLogsAsync(linked.Token).GetAsyncEnumerator(linked.Token);
		await using var disposeRecords = records.ConfigureAwait(false);
		try
		{
			while (Acks(logs).Count <= seen)
				_ = await records.MoveNextAsync().ConfigureAwait(false);
			return true;
		}
		catch (OperationCanceledException) when (quiet.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
		{
			return false;
		}
	}

	private static List<FakeLogRecord> Acks(FakeLogCollector logs) => [.. logs.GetSnapshot().Where(r => r.Id.Name == "Acked")];

	/// <summary>The most clock ticks <see cref="SettledAckAsync"/> moves before it gives up settling.</summary>
	private const int MaxTicks = 30;

	/// <summary>How long an ack interval may stay without a new ack for the watermark to count as settled.</summary>
	private static readonly TimeSpan s_quiet = TimeSpan.FromSeconds(2);

#pragma warning disable RS0030 // Justification: the quiet window is real time; the client's own clock is held still on purpose.
	private static TimeProvider RealTime => TimeProvider.System;
#pragma warning restore RS0030

	private static async Task PrintAsync(string? line)
	{
		await Console.Out.WriteLineAsync(line).ConfigureAwait(false);
		await Console.Out.FlushAsync().ConfigureAwait(false);
	}
}
