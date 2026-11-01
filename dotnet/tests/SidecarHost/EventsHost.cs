using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Testing;
using Microsoft.Extensions.Time.Testing;
using Whatsmeow;
using Whatsmeow.Events;

namespace SidecarHost;

/// <summary>
/// The process the events kill test kills: it starts the fake-mode sidecar on the database, prints the sidecar's
/// process id, connects the session and prints "session id" as each message handler starts. In the handler of
/// <c>blockOn</c> it moves its clock one ack interval, waits until the ack went out, prints "acked", and never
/// returns; the test kills this process there. Its clock moves only then, so that is the one ack, and it covers
/// exactly the events before <c>blockOn</c>.
/// </summary>
internal static class EventsHost
{
	public static async Task RunAsync(string fakeSidecar, string database, string session, string blockOn)
	{
		var logs = new FakeLogCollector();
		using var provider = new FakeLoggerProvider(logs);
		using var loggerFactory = LoggerFactory.Create(builder => builder.AddProvider(provider).SetMinimumLevel(LogLevel.Debug));
		var time = new FakeTimeProvider();

		var sidecar = await WhatsmeowSidecar
			.StartFakeWhatsAppAsync(new WhatsmeowSidecarOptions { SidecarPath = fakeSidecar, DatabasePath = database }, loggerFactory, time, CancellationToken.None)
			.ConfigureAwait(false);
		await using (sidecar.ConfigureAwait(false))
		{
			await PrintAsync(logs.GetSnapshot().First(r => r.Id.Name == "SidecarStarted").GetStructuredStateValue("ProcessId")).ConfigureAwait(false);

			var stuck = new TaskCompletionSource();
			var client = await sidecar.Sessions.GetOrCreateAsync(SessionId.From(session), CancellationToken.None).ConfigureAwait(false);
			using var handler = client.On<MessageReceived>(
				async (e, _) =>
				{
					await PrintAsync($"{e.SessionId} {e.Id}").ConfigureAwait(false);
					if (e.Id != MessageId.From(blockOn))
						return;

					time.Advance(TimeSpan.FromSeconds(1));
					await AckedAsync(logs).ConfigureAwait(false);
					await PrintAsync("acked").ConfigureAwait(false);
					await stuck.Task.ConfigureAwait(false);
				}
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
