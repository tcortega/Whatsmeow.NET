using System.Diagnostics;
using Microsoft.Extensions.Time.Testing;
using Whatsmeow.Events;
using Whatsmeow.IntegrationTests.Fake;
using Whatsmeow.IntegrationTests.Sessions;
using Whatsmeow.IntegrationTests.Support;

namespace Whatsmeow.IntegrationTests.Events;

/// <summary>
/// Events across the end of the .NET side: a graceful stop and a hard kill. Each test starts fake-mode sidecars of its
/// own on a database of its own, because stopping the process is the behaviour under test.
/// </summary>
public sealed class EventProcessTests(FakeSidecar fixture, SidecarBinaries binaries)
{
	private const int Timeout = 120_000;

	[Fact(Timeout = Timeout)]
	public async Task Dispose_AcksTheHandledEvents_SoTheNextProcessGetsOnlyNewOnes()
	{
		var ct = TestContext.Current.CancellationToken;
		using var logs = new LogCapture();
		var options = new WhatsmeowSidecarOptions { SidecarPath = fixture.Binary, DatabasePath = binaries.NewDatabase() };

		// The clock never moves, so the timer never acks: only the stop can.
		await using (var first = await WhatsmeowSidecar.StartFakeWhatsAppAsync(options, logs.Factory, new FakeTimeProvider(), ct))
		{
			var fake = new FakeWhatsAppService.FakeWhatsAppServiceClient(first.CallInvoker);
			var client = await LinkAsync(first, fake, "events-flush", ct);
			using var received = new Received<MessageReceived>(client);
			await client.ConnectAsync(ct);
			await Deliveries.DeliverAsync(fake, "events-flush", ct, "M1", "M2", "M3");
			_ = await received.NextAsync(3, ct);
		}

		await using var second = await WhatsmeowSidecar.StartFakeWhatsAppAsync(options, logs.Factory, ct);
		var again = await second.Sessions.GetOrCreateAsync(SessionId.From("events-flush"), ct);
		using var next = new Received<MessageReceived>(again);
		await again.ConnectAsync(ct);
		await Deliveries.DeliverAsync(new FakeWhatsAppService.FakeWhatsAppServiceClient(second.CallInvoker), "events-flush", ct, "M4");

		Assert.Equal(Deliveries.Received("events-flush", "M4"), await next.NextAsync(ct));
	}

	[Fact(Timeout = Timeout)]
	public async Task Dispose_WhileAHandlerRuns_ThatEventComesFirstInTheNextProcess()
	{
		var ct = TestContext.Current.CancellationToken;
		using var logs = new LogCapture();
		var options = new WhatsmeowSidecarOptions { SidecarPath = fixture.Binary, DatabasePath = binaries.NewDatabase() };

		// M2's handler is still running when the sidecar is disposed: the last ack is M1, not the highest received.
		await using (var first = await WhatsmeowSidecar.StartFakeWhatsAppAsync(options, logs.Factory, new FakeTimeProvider(), ct))
		{
			var fake = new FakeWhatsAppService.FakeWhatsAppServiceClient(first.CallInvoker);
			var client = await LinkAsync(first, fake, "events-held", ct);
			var held = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
			using var received = new Received<MessageReceived>(
				client,
				async (e, token) =>
				{
					if (e.Id != MessageId.From("M2"))
						return;
					held.SetResult();
					await new TaskCompletionSource().Task.WaitAsync(token).ConfigureAwait(false);
				}
			);
			await client.ConnectAsync(ct);
			await Deliveries.DeliverAsync(fake, "events-held", ct, "M1", "M2", "M3");
			_ = await received.NextAsync(ct);
			await held.Task.WaitAsync(ct);
		}

		await using var second = await WhatsmeowSidecar.StartFakeWhatsAppAsync(options, logs.Factory, ct);
		var again = await second.Sessions.GetOrCreateAsync(SessionId.From("events-held"), ct);
		using var next = new Received<MessageReceived>(again);
		await again.ConnectAsync(ct);

		Assert.Equal([Deliveries.Received("events-held", "M2"), Deliveries.Received("events-held", "M3")], await next.NextAsync(2, ct));
	}

	[Fact(Timeout = Timeout)]
	public async Task Stream_TheSidecarIsKilledWhileTheReaderWaits_EventsFlowAfterTheRestart()
	{
		var ct = TestContext.Current.CancellationToken;
		using var logs = new LogCapture();
		var options = new WhatsmeowSidecarOptions { SidecarPath = fixture.Binary, DatabasePath = binaries.NewDatabase() };
		await using var sidecar = await WhatsmeowSidecar.StartFakeWhatsAppAsync(options, logs.Factory, ct);
		var client = await LinkAsync(sidecar, new FakeWhatsAppService.FakeWhatsAppServiceClient(sidecar.CallInvoker), "events-reader-kill", ct);
		using var received = new Received<MessageReceived>(client);
		await client.ConnectAsync(ct);
		await Deliveries.DeliverAsync(new FakeWhatsAppService.FakeWhatsAppServiceClient(sidecar.CallInvoker), "events-reader-kill", ct, "M1");
		_ = await received.NextAsync(ct);

		// Logged out, then killed: the reader's call then ended with a TaskCanceledException instead of a status
		// [observed: the restart tests, instrumented].
		_ = await new FakeWhatsAppService.FakeWhatsAppServiceClient(sidecar.CallInvoker).RaisePermanentDisconnectAsync(
			new RaisePermanentDisconnectRequest { SessionId = "events-reader-kill", LoggedOut = new RaisePermanentDisconnectRequest.Types.LoggedOut { Reason = 401 } },
			cancellationToken: ct
		);
		for (var kill = 1; kill <= 2; kill++)
		{
			var (processId, _) = await logs.StartedAsync(kill, ct);
			using (var process = Process.GetProcessById(processId))
				process.Kill();
			_ = await logs.WaitForAsync(_ => logs.Named("SidecarRestarted").Count == kill, ct);
		}

		await Deliveries.DeliverAsync(new FakeWhatsAppService.FakeWhatsAppServiceClient(sidecar.CallInvoker), "events-reader-kill", ct, "M2");
		Assert.Equal(Deliveries.Received("events-reader-kill", "M2"), await received.NextAsync(ct));
	}

	[Fact(Timeout = Timeout)]
	public async Task Handler_DisposesTheSidecar_TheDisposeReturns()
	{
		var ct = TestContext.Current.CancellationToken;
		using var logs = new LogCapture();
		var options = new WhatsmeowSidecarOptions { SidecarPath = fixture.Binary, DatabasePath = binaries.NewDatabase() };
		var sidecar = await WhatsmeowSidecar.StartFakeWhatsAppAsync(options, logs.Factory, ct);
		await using (sidecar)
		{
			var client = await LinkAsync(sidecar, new FakeWhatsAppService.FakeWhatsAppServiceClient(sidecar.CallInvoker), "events-self-dispose", ct);
			var disposed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
			using var handler = client.On<MessageReceived>(
				async (_, _) =>
				{
					await sidecar.DisposeAsync().ConfigureAwait(false);
					disposed.SetResult();
				}
			);
			await client.ConnectAsync(ct);
			await Deliveries.DeliverAsync(new FakeWhatsAppService.FakeWhatsAppServiceClient(sidecar.CallInvoker), "events-self-dispose", ct, "M1");

			await disposed.Task.WaitAsync(ct);
		}

		_ = await Assert.ThrowsAsync<ObjectDisposedException>(() => sidecar.GetInfoAsync(ct));
	}

	[Fact(Timeout = Timeout)]
	public async Task Stream_TheDotNetProcessIsKilledMidStream_TheEventsAfterTheAckComeAgainWithTheSameIds()
	{
		var ct = TestContext.Current.CancellationToken;
		using var logs = new LogCapture();
		var options = new WhatsmeowSidecarOptions { SidecarPath = fixture.Binary, DatabasePath = binaries.NewDatabase() };
		string[] ids = ["M1", "M2", "M3", "M4", "M5"];

		// The events are durable in the outbox before any consumer runs.
		await using (var setup = await WhatsmeowSidecar.StartFakeWhatsAppAsync(options, logs.Factory, ct))
		{
			var fake = new FakeWhatsAppService.FakeWhatsAppServiceClient(setup.CallInvoker);
			_ = await LinkAsync(setup, fake, "events-kill", ct);
			await Deliveries.DeliverAsync(fake, "events-kill", ct, ids);
		}

		// A process that handles M1 and M2, acks them, is stuck in M3's handler, and is killed there.
		var host = Path.Combine(AppContext.BaseDirectory, OperatingSystem.IsWindows() ? "SidecarHost.exe" : "SidecarHost");
		var info = new ProcessStartInfo(host) { UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
		foreach (var argument in new[] { "events", fixture.Binary, options.DatabasePath, "events-kill", "M3" })
			info.ArgumentList.Add(argument);

		var handledBefore = new List<string>();
		using (var killed = Process.Start(info)!)
		{
			try
			{
				var line = await killed.StandardOutput.ReadLineAsync(ct);
				if (!int.TryParse(line, System.Globalization.CultureInfo.InvariantCulture, out var sidecar))
					Assert.Fail($"the host printed no sidecar process id: {line} {await killed.StandardError.ReadToEndAsync(ct)}");

				while (handledBefore.LastOrDefault() != "acked")
					handledBefore.Add(await killed.StandardOutput.ReadLineAsync(ct) ?? throw new InvalidOperationException("the host ended"));

				// The host alone, as a crash would: its sidecar stops on its own when stdin closes.
				killed.Kill();
				await killed.WaitForExitAsync(ct);
				await Processes.WaitUntilGoneAsync(sidecar, ct);
			}
			finally
			{
				// A failure above must not leave the host or its sidecar running.
				killed.Kill(entireProcessTree: true);
			}
		}

		// The next process gets what was not acked: from the last acked event on, in order, with the same ids.
		await using var restarted = await WhatsmeowSidecar.StartFakeWhatsAppAsync(options, logs.Factory, ct);
		var client = await restarted.Sessions.GetOrCreateAsync(SessionId.From("events-kill"), ct);
		using var received = new Received<MessageReceived>(client);
		await client.ConnectAsync(ct);

		Assert.Equal(["events-kill M1", "events-kill M2", "events-kill M3", "acked"], handledBefore);
		Assert.Equal(ids[2..].Select(id => Deliveries.Received("events-kill", id)), await received.NextAsync(3, ct));
	}

	/// <summary>A session the phone linked on a sidecar of the test's own (SessionSetup.LinkedAsync, for the shared one).</summary>
	private static async Task<IWhatsAppClient> LinkAsync(WhatsmeowSidecar sidecar, FakeWhatsAppService.FakeWhatsAppServiceClient fake, string id, CancellationToken ct)
	{
		var client = await sidecar.Sessions.GetOrCreateAsync(SessionId.From(id), ct).ConfigureAwait(false);
		_ = await fake.LinkDeviceAsync(new LinkDeviceRequest { SessionId = id, Jid = SessionSetup.Jid(id) }, cancellationToken: ct).ConfigureAwait(false);
		return client;
	}
}
