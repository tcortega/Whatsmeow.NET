using System.Diagnostics;
using Whatsmeow.Events;
using Whatsmeow.Extensions.Hosting;
using Whatsmeow.IntegrationTests.Events;
using Whatsmeow.IntegrationTests.Fake;
using Whatsmeow.IntegrationTests.Sessions;
using Whatsmeow.IntegrationTests.Support;
using Whatsmeow.Sessions;

namespace Whatsmeow.IntegrationTests.Hosting;

/// <summary>Declared sessions connect together at host start, with the same events and restart ownership as ConnectAsync.</summary>
public sealed class HostingBatchTests(FakeSidecar fixture, SidecarBinaries binaries)
{
	private const int Timeout = 120_000;

	[Fact(Timeout = Timeout)]
	public async Task HostStart_ConnectsPairedDeclarationsInOneCall_AndKeepsTheirEventsAcrossACrash()
	{
		var ct = TestContext.Current.CancellationToken;
		var database = binaries.NewDatabase();
		string[] paired = ["hosting-batch-1", "hosting-batch-2", "hosting-batch-3"];
		using var seedLogs = new LogCapture();
		var seed = Hosts.Builder(seedLogs, Hosts.Settings(fixture.Binary, database));
		_ = seed.Services.AddWhatsmeow().UseFakeWhatsApp();
		using (var firstHost = seed.Build())
		{
			await firstHost.StartAsync(ct);
			foreach (var id in paired.Append("hosting-batch-undeclared"))
			{
				_ = await Hosts.Sidecar(firstHost).Sessions.GetOrCreateAsync(SessionId.From(id), ct);
				await Hosts.LinkAsync(firstHost, id, ct);
			}

			await firstHost.StopAsync(ct);
		}

		using var logs = new LogCapture();
		var builder = Hosts.Builder(logs, Hosts.Settings(fixture.Binary, database));
		var whatsmeow = builder.Services.AddWhatsmeow().UseFakeWhatsApp().AddHandler<MessageReceived, RecordingHandler>();
		foreach (var id in paired.Append("hosting-batch-unpaired"))
			_ = whatsmeow.AddSession(id);
		using var host = builder.Build();
		await host.StartAsync(ct);

		Assert.Equal(["http://localhost/whatsmeow.v1.SessionService/ConnectSessions"], ConnectCalls(logs));
		Assert.Equal(new SessionState.Created(), await SessionSetup.StateAsync(Hosts.Client(host, "hosting-batch-unpaired"), ct));
		var undeclared = await Hosts.Sidecar(host).Sessions.GetOrCreateAsync(SessionId.From("hosting-batch-undeclared"), ct);
		Assert.Equal(new SessionState.Disconnected(), await SessionSetup.StateAsync(undeclared, ct));
		foreach (var id in paired)
		{
			await using var watch = StateWatch.Start(Hosts.Client(host, id), ct);
			await watch.ReachesAsync(new SessionState.Connected(), new SessionState.Connecting());
			await Deliveries.DeliverAsync(Hosts.Fake(host), id, ct, "M1");
			Assert.Equal(Deliveries.Received(id, "M1"), (await Hosts.Handled(host).NextAsync(ct)).Event);
		}

		var (processId, _) = await logs.StartedAsync(1, ct);
		using (var process = Process.GetProcessById(processId))
			process.Kill();
		_ = await logs.WaitForAsync(record => record.Id.Name == "SidecarRestarted", ct);

		foreach (var id in paired)
		{
			await using var watch = StateWatch.Start(Hosts.Client(host, id), ct);
			await watch.ReachesAsync(new SessionState.Connected(), new SessionState.Connecting());
			await Deliveries.DeliverAsync(Hosts.Fake(host), id, ct, "M2");
			Assert.Equal(Deliveries.Received(id, "M2"), (await Hosts.Handled(host).NextAsync(ct)).Event);
		}

		await host.StopAsync(ct);
	}

	[Fact(Timeout = Timeout)]
	public async Task HostStart_OneDeclaredConnectFails_TheOthersConnectBeforeTheStartReportsFailure()
	{
		var ct = TestContext.Current.CancellationToken;
		var directory = binaries.NewDirectory();
		var token = StandaloneSidecar.NewTokenFile(directory);
		await using var standalone = await StandaloneSidecar.StartAsync(fixture.Binary, binaries.NewDatabase(), "tcp:127.0.0.1:0", token, ["--fake-whatsapp"], ct);
		await using var observer = await WhatsmeowSidecar.StartAsync(
			new WhatsmeowSidecarOptions { External = new ExternalSidecarOptions { Address = standalone.Address, TokenPath = token, Consumer = "observer" } },
			ct
		);
		var fake = new FakeWhatsAppService.FakeWhatsAppServiceClient(observer.CallInvoker);
		string[] ids = ["hosting-batch-fail-1", "hosting-batch-fail-2", "hosting-batch-fail-3"];
		foreach (var id in ids)
		{
			_ = await observer.Sessions.GetOrCreateAsync(SessionId.From(id), ct);
			_ = await fake.LinkDeviceAsync(new LinkDeviceRequest { SessionId = id, Jid = SessionSetup.Jid(id) }, cancellationToken: ct);
		}

		_ = await fake.FailNextConnectAsync(new FailNextConnectRequest { SessionId = ids[0] }, cancellationToken: ct);

		using var logs = new LogCapture();
		var builder = Hosts.Builder(logs, new(StringComparer.Ordinal) { ["Whatsmeow:External:Address"] = standalone.Address, ["Whatsmeow:External:TokenPath"] = token });
		var whatsmeow = builder.Services.AddWhatsmeow();
		foreach (var id in ids)
			_ = whatsmeow.AddSession(id);
		using var host = builder.Build();

		var error = await Assert.ThrowsAsync<NotConnectedException>(() => host.StartAsync(ct));
		Assert.Equal(WhatsmeowErrorCode.ConnectionFailed, error.ErrorCode);
		Assert.Equal(new SessionState.Disconnected(), await SessionSetup.StateAsync(await observer.Sessions.GetOrCreateAsync(SessionId.From(ids[0]), ct), ct));
		foreach (var id in ids.Skip(1))
		{
			var client = await observer.Sessions.GetOrCreateAsync(SessionId.From(id), ct);
			await using var watch = StateWatch.Start(client, ct);
			await watch.ReachesAsync(new SessionState.Connected(), new SessionState.Connecting());
			var delivered = new TaskCompletionSource<MessageReceived>(TaskCreationOptions.RunContinuationsAsynchronously);
			using var handler = client.On<MessageReceived>((message, _) =>
			{
				delivered.SetResult(message);
				return ValueTask.CompletedTask;
			});
			await client.ConnectAsync(ct);
			await Deliveries.DeliverAsync(fake, id, ct, "M1");
			Assert.Equal(Deliveries.Received(id, "M1"), await delivered.Task.WaitAsync(ct));
		}

		Assert.Equal(["http://localhost/whatsmeow.v1.SessionService/ConnectSessions"], ConnectCalls(logs));
		Assert.False(standalone.HasExited, standalone.Stderr);
	}

	// Grpc.Net.Client 2.85.0 emits StartingCall with Uri before each actual RPC (LocalChannel supplies this logger).
	private static string?[] ConnectCalls(LogCapture logs) =>
		[.. logs.Named("StartingCall").Select(record => record.GetStructuredStateValue("Uri")).Where(uri => uri is "http://localhost/whatsmeow.v1.SessionService/Connect" or "http://localhost/whatsmeow.v1.SessionService/ConnectSessions")];
}
