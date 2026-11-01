using Whatsmeow.IntegrationTests.Pairing;
using Whatsmeow.IntegrationTests.Sessions;
using Whatsmeow.Pairing;

namespace Whatsmeow.LiveTests;

/// <summary>
/// Pairing a real phone, with the fake-mode tests' own checks (<see cref="PairingChecks"/>): a person scans
/// <c>qr.png</c>, then enters the code in <c>pairing-code.txt</c> (<see cref="LiveSidecar"/>). The tests in this class
/// run one after the other.
/// </summary>
public sealed class LivePairingTests(LiveSidecar live) : IClassFixture<LiveSidecar>
{
	[LiveFact]
	public async Task Live_PairWithQrCode_YieldsEachCodeThenPaired_AndTheSessionIsConnected()
	{
		var ct = TestContext.Current.CancellationToken;
		var client = await live.NewSessionAsync("live-qr", ct);
		var updates = new List<PairingUpdate>();

		await foreach (var update in client.PairWithQrCodeAsync(ct))
		{
			updates.Add(update);
			if (update is PairingUpdate.QrCode qrCode)
				await live.ShowQrCodeAsync(qrCode.Code, ct);
		}

		PairingChecks.ValidateQrPairing(updates, await SessionSetup.StateAsync(client, ct));
	}

	[LiveFact]
	public async Task Live_PairWithPhoneNumber_YieldsTheCodeThenPaired_AndTheSessionIsConnected()
	{
		var ct = TestContext.Current.CancellationToken;
		var client = await live.NewSessionAsync("live-phone", ct);
		var updates = new List<PairingUpdate>();

		await foreach (var update in client.PairWithPhoneNumberAsync(LiveSettings.PhoneNumber, ct))
		{
			updates.Add(update);
			if (update is PairingUpdate.PairingCode pairingCode)
				await live.ShowPairingCodeAsync(pairingCode.Code, ct);
		}

		PairingChecks.ValidatePhonePairing(updates, await SessionSetup.StateAsync(client, ct));
	}
}
