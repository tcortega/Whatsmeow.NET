using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Testing;
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
/// session's state lane handled <see cref="SessionState.Connected"/>, moves its clock
/// one ack interval, waits until the ack went out, prints "acked", and never returns; the test kills this process
/// there. Its clock moves only then, so that is the one ack, and it covers exactly the events before <c>blockOn</c>.
/// </summary>
/// <remarks>
/// The state lane runs beside the chat's lane, and the session's state events before the first message hold the
/// watermark until that lane ran them: a tick before that acks nothing, and no tick comes after it (#61). This
/// process's connect records <see cref="SessionState.Connected"/> after the messages, and a lane runs its events one
/// after the other, so once that handler runs, every state event before the messages finished.
/// </remarks>
internal static class EventsHost
{
	public static async Task RunAsync(string sidecarPath, string database, string session, string blockOn, bool fakeWhatsApp)
	{
		var logs = new FakeLogCollector();
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
			using var states = client.On<ConnectionStateChanged>(
				(e, token) =>
				{
					if (e.State is SessionState.Connected)
						_ = connected.TrySetResult();
					return ValueTask.CompletedTask;
				}
			);
			async Task HandleAsync(string label, bool block)
			{
				await PrintAsync(label).ConfigureAwait(false);
				if (!block)
					return;

				await connected.Task.ConfigureAwait(false);
				time.Advance(TimeSpan.FromSeconds(1));
				await AckedAsync(logs).ConfigureAwait(false);
				await PrintAsync("acked").ConfigureAwait(false);
				await stuck.Task.ConfigureAwait(false);
			}

			using var handler = client.On<MessageReceived>((e, _) => new ValueTask(HandleAsync($"{e.SessionId} {e.Id}", e.Id == MessageId.From(blockOn))));
			using var renames = client.On<GroupInfoChanged>(
				(e, _) =>
					e.Changes is [GroupChange.Renamed { Name: var name }]
						? new ValueTask(HandleAsync($"{e.SessionId} {name}", name == blockOn))
						: ValueTask.CompletedTask
			);
			await client.ConnectAsync(CancellationToken.None).ConfigureAwait(false);
			_ = await Console.In.ReadLineAsync().ConfigureAwait(false);
		}
	}

	/// <summary>Waits for the client's first "Acked" log record.</summary>
	private static async Task AckedAsync(FakeLogCollector logs)
	{
		var records = logs.GetLogsAsync().GetAsyncEnumerator();
		await using var disposeRecords = records.ConfigureAwait(false);
		while (!logs.GetSnapshot().Any(r => r.Id.Name == "Acked"))
			_ = await records.MoveNextAsync().ConfigureAwait(false);
	}

	private static async Task PrintAsync(string? line)
	{
		await Console.Out.WriteLineAsync(line).ConfigureAwait(false);
		await Console.Out.FlushAsync().ConfigureAwait(false);
	}
}
