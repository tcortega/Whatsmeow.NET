using Whatsmeow.IntegrationTests.Pairing;
using Whatsmeow.IntegrationTests.Sessions;
using Whatsmeow.Pairing;
using Whatsmeow.Sessions;

namespace Whatsmeow.LiveTests;

/// <summary>
/// Pairing a real phone, with the fake-mode tests' own checks (<see cref="PairingChecks"/>): a person scans
/// <c>qr.png</c> or enters the code in <c>pairing-code.txt</c> (<see cref="LiveSidecar"/>). The live tests run one
/// after the other (<see cref="Live"/>).
/// </summary>
[Collection(nameof(Live))]
public sealed class LivePairingTests(LiveSidecar live, LivePeer peer)
{
	/// <summary>
	/// Pairs the persistent live session by QR code the first time, or after the phone logged it out; on every other
	/// run it connects the session paired before, so nobody scans again. Either way it stays connected until CaughtUp,
	/// so the phone finishes linking the device (<see cref="LiveSidecar.StayUntilCaughtUpAsync(IWhatsAppClient, CancellationToken)"/>).
	/// </summary>
	[LiveFact]
	public async Task Live_PairWithQrCode_YieldsEachCodeThenPaired_AndTheSessionIsConnected()
	{
		var ct = TestContext.Current.CancellationToken;
		await PairOrConnectAsync(await live.SessionAsync(LiveSidecar.LiveSessionId, ct), () => live.LiveAsync(ct), ct);
	}

	/// <summary>
	/// The same for the persistent peer session, on its own database and sidecar (<see cref="LivePeer"/>), scanned with
	/// the second account's phone: paired the first time or after a logout, connected on every other run. It never
	/// pairs again, logs out or deletes a paired session.
	/// </summary>
	[LiveFact(NeedsPeer = true)]
	public async Task Live_PairThePeerWithQrCode_YieldsEachCodeThenPaired_AndTheSessionIsConnected()
	{
		var ct = TestContext.Current.CancellationToken;
		await PairOrConnectAsync(await peer.SessionAsync(live, ct), () => peer.ConnectedAsync(live, ct), ct);
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

	/// <summary>
	/// Pairs <paramref name="client"/> by QR code when it is Created or LoggedOut, showing each code in <c>qr.png</c>,
	/// and stays connected until CaughtUp; a session in any other state is paired already, so it only connects it
	/// (<paramref name="connected"/>) and checks it is usable.
	/// </summary>
	private async Task PairOrConnectAsync(IWhatsAppClient client, Func<Task<IWhatsAppClient>> connected, CancellationToken ct)
	{
		if (await SessionSetup.StateAsync(client, ct).ConfigureAwait(false) is not (SessionState.Created or SessionState.LoggedOut))
		{
			PairingChecks.ValidateUsable(await SessionSetup.StateAsync(await connected().ConfigureAwait(false), ct).ConfigureAwait(false));
			return;
		}

		var updates = new List<PairingUpdate>();
		await foreach (var update in client.PairWithQrCodeAsync(ct).ConfigureAwait(false))
		{
			updates.Add(update);
			if (update is not PairingUpdate.QrCode qrCode)
				continue;
			if (updates.Count == 1)
				PairingChecks.ValidatePairingStarted(await SessionSetup.StateAsync(client, ct).ConfigureAwait(false));
			await live.ShowQrCodeAsync(qrCode.Code, ct).ConfigureAwait(false);
		}

		live.DeleteShown();
		PairingChecks.ValidateQrPairing(updates, await SessionSetup.StateAsync(client, ct).ConfigureAwait(false));
		_ = await live.StayUntilCaughtUpAsync(client, ct).ConfigureAwait(false);
	}
}
