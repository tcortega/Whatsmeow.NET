using Whatsmeow.IntegrationTests.Pairing;
using Whatsmeow.IntegrationTests.Sessions;
using Whatsmeow.Pairing;
using Whatsmeow.Sessions;

namespace Whatsmeow.LiveTests;

/// <summary>
/// Pairing a real phone, with the fake-mode tests' own checks (<see cref="PairingChecks"/>): a person scans
/// <c>qr.png</c> or enters the code in <c>pairing-code.txt</c> (<see cref="LiveSidecar"/>). The tests in this class
/// run one after the other.
/// </summary>
public sealed class LivePairingTests(LiveSidecar live) : IClassFixture<LiveSidecar>
{
	/// <summary>
	/// Pairs the persistent live session by QR code the first time, or after the phone logged it out; on every other
	/// run it connects the session paired before, so nobody scans again. Either way it stays connected until CaughtUp,
	/// so the phone finishes linking the device (<see cref="LiveSidecar.StayUntilCaughtUpAsync"/>).
	/// </summary>
	[LiveFact]
	public async Task Live_PairWithQrCode_YieldsEachCodeThenPaired_AndTheSessionIsConnected()
	{
		var ct = TestContext.Current.CancellationToken;
		var client = await live.SessionAsync(LiveSidecar.LiveSessionId, ct);

		if (await SessionSetup.StateAsync(client, ct) is not (SessionState.Created or SessionState.LoggedOut))
		{
			PairingChecks.ValidateUsable(await LiveSidecar.ConnectAsync(client, ct));
			TestContext.Current.TestOutputHelper?.WriteLine($"CaughtUp {(await live.StayUntilCaughtUpAsync(client, ct)).TotalSeconds:F1} s after Connected");
			return;
		}

		var updates = new List<PairingUpdate>();
		await foreach (var update in client.PairWithQrCodeAsync(ct))
		{
			updates.Add(update);
			if (update is not PairingUpdate.QrCode qrCode)
				continue;
			if (updates.Count == 1)
				PairingChecks.ValidatePairingStarted(await SessionSetup.StateAsync(client, ct));
			await live.ShowQrCodeAsync(qrCode.Code, ct);
		}

		PairingChecks.ValidateQrPairing(updates, await SessionSetup.StateAsync(client, ct));
		TestContext.Current.TestOutputHelper?.WriteLine($"CaughtUp {(await live.StayUntilCaughtUpAsync(client, ct)).TotalSeconds:F1} s after Paired");
	}

	/// <summary>Pairs a throwaway session by phone code; it is logged out and deleted at the end.</summary>
	[LiveFact(NeedsPhoneNumber = true)]
	public async Task Live_PairWithPhoneNumber_YieldsTheCodeThenPaired_AndTheSessionIsConnected()
	{
		var ct = TestContext.Current.CancellationToken;
		var client = await live.ThrowawaySessionAsync("live-phone", ct);
		var updates = new List<PairingUpdate>();

		await foreach (var update in client.PairWithPhoneNumberAsync(LiveSettings.PhoneNumber, ct))
		{
			updates.Add(update);
			if (update is not PairingUpdate.PairingCode pairingCode)
				continue;
			PairingChecks.ValidatePairingStarted(await SessionSetup.StateAsync(client, ct));
			await live.ShowPairingCodeAsync(pairingCode.Code, ct);
		}

		PairingChecks.ValidatePhonePairing(updates, await SessionSetup.StateAsync(client, ct));
	}
}
