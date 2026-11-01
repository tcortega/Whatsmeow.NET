using System.Diagnostics;
using Whatsmeow.IntegrationTests.Fake;
using Whatsmeow.IntegrationTests.Support;
using Whatsmeow.Sessions;

namespace Whatsmeow.IntegrationTests.Sessions;

/// <summary>
/// A state watch opened while the sidecar is down, before the supervisor restarts it. It starts a fake-mode sidecar of
/// its own, because killing it and catching the second before the restart is the behaviour under test.
/// </summary>
public sealed class WatchDownTests(FakeSidecar fixture, SidecarBinaries binaries)
{
	[Fact(Timeout = 120_000)]
	public async Task Watch_OpenedWhileTheSidecarIsDown_GoesOnWithTheRestartedOne()
	{
		var ct = TestContext.Current.CancellationToken;
		using var logs = new LogCapture();
		var options = new WhatsmeowSidecarOptions { SidecarPath = fixture.Binary, DatabasePath = binaries.NewDatabase() };
		await using var sidecar = await WhatsmeowSidecar.StartFakeWhatsAppAsync(options, logs.Factory, ct);
		var client = await sidecar.Sessions.GetOrCreateAsync(SessionId.From("watch-down"), ct);
		_ = await new FakeWhatsAppService.FakeWhatsAppServiceClient(sidecar.CallInvoker)
			.LinkDeviceAsync(new LinkDeviceRequest { SessionId = "watch-down", Jid = SessionSetup.Jid("watch-down") }, cancellationToken: ct);

		var (processId, _) = await logs.StartedAsync(1, ct);
		using (var process = Process.GetProcessById(processId))
		{
			process.Kill();
			await process.WaitForExitAsync(ct);
		}

		// The supervisor waits 1 s before the restart (ADR 0017); the call goes out in that second. A call to a sidecar
		// that is gone can end with a TaskCanceledException instead of a status [observed: #8 review, O2].
		await using var watch = StateWatch.Start(client, ct);
		Assert.Equal(new SessionState.Disconnected(), await watch.NextAsync());
		_ = Assert.Single(logs.Named("SidecarRestarted"));
	}
}
