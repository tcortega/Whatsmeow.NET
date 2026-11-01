using Whatsmeow.IntegrationTests.Fake;
using Whatsmeow.IntegrationTests.Sessions;
using Whatsmeow.Pairing;
using Whatsmeow.Sessions;

namespace Whatsmeow.IntegrationTests.Pairing;

/// <summary>A paired session across a crash of the shared sidecar (ARCHITECTURE §8.5).</summary>
[Collection(nameof(SidecarCrashes))]
public sealed class PairingRestartTests(FakeSidecar fixture)
{
	private const int Timeout = 120_000;

	[Fact(Timeout = Timeout)]
	public async Task Pair_ThenTheSidecarRestarts_ConnectsTheSessionAgain()
	{
		var ct = TestContext.Current.CancellationToken;
		var client = await fixture.Sidecar.Sessions.GetOrCreateAsync(SessionId.From("pair-restart"), ct);
		await foreach (var update in client.PairWithQrCodeAsync(ct))
		{
			if (update is PairingUpdate.QrCode)
				_ = await fixture.Fake.LinkDeviceAsync(new LinkDeviceRequest { SessionId = "pair-restart", Jid = SessionSetup.Jid("pair-restart") }, cancellationToken: ct);
		}

		await fixture.CrashAsync(ct);

		// CrashAsync returns after the client's reconnects: a session connected again is Connecting or Connected by now.
		Assert.Contains(await SessionSetup.StateAsync(client, ct), new SessionState[] { new SessionState.Connecting(), new SessionState.Connected() });
	}
}
