using Whatsmeow.Events;
using Whatsmeow.IntegrationTests.Events;
using Whatsmeow.IntegrationTests.Fake;
using Whatsmeow.IntegrationTests.Sessions;
using Whatsmeow.IntegrationTests.Support;
using Whatsmeow.Pairing;
using Whatsmeow.Sessions;

namespace Whatsmeow.IntegrationTests.Pairing;

/// <summary>A paired session across a crash of the shared sidecar (ARCHITECTURE §8.5).</summary>
[Collection(nameof(SidecarCrashes))]
public sealed class PairingRestartTests(FakeSidecar fixture)
{
	private const int Timeout = 120_000;

	[Fact(Timeout = Timeout)]
	public async Task Pair_ThenTheSidecarRestarts_ConnectsTheSessionAgain_AndItsMessagesReachTheHandler()
	{
		var ct = TestContext.Current.CancellationToken;
		var client = await fixture.Sidecar.Sessions.GetOrCreateAsync(SessionId.From("pair-restart"), ct);
		using var received = new Received<MessageReceived>(client);
		await foreach (var update in client.PairWithQrCodeAsync(ct))
		{
			if (update is PairingUpdate.QrCode)
				_ = await fixture.Fake.LinkDeviceAsync(new LinkDeviceRequest { SessionId = "pair-restart", Jid = SessionSetup.Jid("pair-restart") }, cancellationToken: ct);
		}

		await fixture.CrashAsync(ct);

		// CrashAsync returns after the client's reconnects: a session connected again is Connecting or Connected by now.
		Assert.Contains(await SessionSetup.StateAsync(client, ct), new SessionState[] { new SessionState.Connecting(), new SessionState.Connected() });
		await using (var watch = StateWatch.Start(client, ct))
			await watch.ReachesAsync(new SessionState.Connected(), new SessionState.Connecting());
		_ = await fixture.Fake.DeliverMessageAsync(Deliveries.Message("pair-restart", "M1"), cancellationToken: ct);
		Assert.Equal(Deliveries.Received("pair-restart", "M1"), await received.NextAsync(ct));
	}

	[Fact(Timeout = Timeout)]
	public async Task Pair_StoppedRightAtPaired_ThenTheSidecarRestarts_ConnectsTheSessionAgain()
	{
		var ct = TestContext.Current.CancellationToken;
		var client = await fixture.Sidecar.Sessions.GetOrCreateAsync(SessionId.From("pair-restart-break"), ct);
		await foreach (var update in client.PairWithQrCodeAsync(ct))
		{
			if (update is PairingUpdate.Paired)
				break;
			_ = await fixture.Fake.LinkDeviceAsync(
				new LinkDeviceRequest { SessionId = "pair-restart-break", Jid = SessionSetup.Jid("pair-restart-break") },
				cancellationToken: ct
			);
		}

		await fixture.CrashAsync(ct);

		Assert.Contains(await SessionSetup.StateAsync(client, ct), new SessionState[] { new SessionState.Connecting(), new SessionState.Connected() });
	}

	[Fact(Timeout = Timeout)]
	public async Task Pair_StoppedRightAfterTheLink_ThenTheSidecarRestarts_ConnectsTheSessionAgain()
	{
		var ct = TestContext.Current.CancellationToken;
		var client = await fixture.Sidecar.Sessions.GetOrCreateAsync(SessionId.From("pair-restart-linked"), ct);
		var updates = client.PairWithQrCodeAsync(ct).GetAsyncEnumerator(ct);
		Assert.True(await updates.MoveNextAsync());
		_ = await fixture.Fake.LinkDeviceAsync(
			new LinkDeviceRequest { SessionId = "pair-restart-linked", Jid = SessionSetup.Jid("pair-restart-linked") },
			cancellationToken: ct
		);
		// Stops reading before Paired: after the link the login goes on without the call.
		await updates.DisposeAsync();

		await using (var watch = StateWatch.Start(client, ct))
			await watch.ReachesAsync(new SessionState.Connected(), new SessionState.Pairing());

		await fixture.CrashAsync(ct);

		Assert.Contains(await SessionSetup.StateAsync(client, ct), new SessionState[] { new SessionState.Connecting(), new SessionState.Connected() });
	}

	[Fact(Timeout = Timeout)]
	public async Task Pair_AnInvalidPhoneNumber_ThenTheSidecarRestarts_LeavesTheSessionDisconnected()
	{
		var ct = TestContext.Current.CancellationToken;
		var client = await SessionSetup.LinkedAsync(fixture, "pair-restart-bad-phone", ct);
		_ = await Assert.ThrowsAsync<WhatsmeowValidationException>(
			async () =>
			{
				await foreach (var update in client.PairWithPhoneNumberAsync("0555 1234 567", ct).ConfigureAwait(false))
					Assert.Fail($"pairing yielded {update}");
			}
		);

		await fixture.CrashAsync(ct);

		// Nothing started: the application never connected it.
		Assert.Equal(new SessionState.Disconnected(), await SessionSetup.StateAsync(client, ct));
	}

	[Fact(Timeout = Timeout)]
	public async Task Pair_AlreadyPaired_ThenTheSidecarRestarts_LeavesTheSessionDisconnected()
	{
		var ct = TestContext.Current.CancellationToken;
		var client = await SessionSetup.LinkedAsync(fixture, "pair-restart-already", ct);
		var error = await Assert.ThrowsAsync<PairingException>(
			async () =>
			{
				await foreach (var update in client.PairWithQrCodeAsync(ct).ConfigureAwait(false))
					Assert.Fail($"pairing yielded {update}");
			}
		);
		Assert.Equal(WhatsmeowErrorCode.AlreadyPaired, error.ErrorCode);

		await fixture.CrashAsync(ct);

		// The application never connected it, and the refused Pair did not either.
		Assert.Equal(new SessionState.Disconnected(), await SessionSetup.StateAsync(client, ct));
	}

	[Fact(Timeout = Timeout)]
	public async Task Pair_TheSidecarCrashesWhilePairing_ThrowsSidecarUnavailable()
	{
		var ct = TestContext.Current.CancellationToken;
		var client = await fixture.Sidecar.Sessions.GetOrCreateAsync(SessionId.From("pair-crash"), ct);
		await using var updates = client.PairWithQrCodeAsync(ct).GetAsyncEnumerator(ct);
		Assert.True(await updates.MoveNextAsync());

		await fixture.CrashAsync(ct);

		// The pairing died with the process: an error, never a silent end of the enumeration.
		_ = await Assert.ThrowsAsync<SidecarUnavailableException>(async () => await updates.MoveNextAsync().ConfigureAwait(false));
		Assert.Equal(new SessionState.Created(), await SessionSetup.StateAsync(client, ct));
	}
}
