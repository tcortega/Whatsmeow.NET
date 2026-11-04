using Whatsmeow.Events;
using Whatsmeow.IntegrationTests.Events;
using Whatsmeow.IntegrationTests.Fake;
using Whatsmeow.IntegrationTests.Sessions;
using Whatsmeow.IntegrationTests.Support;
using Whatsmeow.Pairing;
using Whatsmeow.Sessions;

namespace Whatsmeow.IntegrationTests.Pairing;

/// <summary>Pairing through the public API on the shared fake-mode sidecar; the fake WhatsApp plays the phone.</summary>
public sealed class PairingTests(FakeSidecar fixture)
{
	private const int Timeout = 120_000;

	private WhatsmeowSessions Sessions => fixture.Sidecar.Sessions;

	[Fact(Timeout = Timeout)]
	public async Task FullCycle_PairWithQrCode_YieldsEachCodeThenPaired_AndTheSessionIsConnected()
	{
		var ct = TestContext.Current.CancellationToken;
		var client = await Sessions.GetOrCreateAsync(SessionId.From("pair-qr"), ct);
		var updates = new List<PairingUpdate>();

		await foreach (var update in client.PairWithQrCodeAsync(ct))
		{
			updates.Add(update);
			switch (updates.Count)
			{
				case 1:
					PairingChecks.ValidatePairingStarted(await SessionSetup.StateAsync(client, ct));
					_ = await fixture.Fake.RotateQrCodeAsync(new RotateQrCodeRequest { SessionId = "pair-qr" }, cancellationToken: ct);
					break;
				case 2:
					_ = await fixture.Fake.LinkDeviceAsync(new LinkDeviceRequest { SessionId = "pair-qr", Jid = SessionSetup.Jid("pair-qr") }, cancellationToken: ct);
					break;
				default:
					break;
			}
		}

		Assert.Equal(3, updates.Count);
		PairingChecks.ValidateQrPairing(updates, await SessionSetup.StateAsync(client, ct));
	}

	[Fact(Timeout = Timeout)]
	public async Task FullCycle_PairWithPhoneNumber_YieldsTheCodeThenPaired_AndTheSessionIsConnected()
	{
		var ct = TestContext.Current.CancellationToken;
		var client = await Sessions.GetOrCreateAsync(SessionId.From("pair-phone"), ct);
		var updates = new List<PairingUpdate>();

		await foreach (var update in client.PairWithPhoneNumberAsync("+1 555 123 4567", ct))
		{
			updates.Add(update);
			if (update is not PairingUpdate.PairingCode)
				continue;
			PairingChecks.ValidatePairingStarted(await SessionSetup.StateAsync(client, ct));
			_ = await fixture.Fake.LinkDeviceAsync(new LinkDeviceRequest { SessionId = "pair-phone", Jid = SessionSetup.Jid("pair-phone") }, cancellationToken: ct);
		}

		PairingChecks.ValidatePhonePairing(updates, await SessionSetup.StateAsync(client, ct));
	}

	[Fact(Timeout = Timeout)]
	public async Task FullCycle_PairWithQrCode_ThenAMessageArrives_TheHandlerGetsItWithNoConnect()
	{
		var ct = TestContext.Current.CancellationToken;
		var client = await Sessions.GetOrCreateAsync(SessionId.From("pair-events"), ct);
		using var received = new Received<MessageReceived>(client);

		await PairLinkingAsync(client, "pair-events", ct);
		_ = await fixture.Fake.DeliverMessageAsync(Deliveries.Message("pair-events", "M1"), cancellationToken: ct);

		Assert.Equal(Deliveries.Received("pair-events", "M1"), await received.NextAsync(ct));
	}

	[Fact(Timeout = Timeout)]
	public async Task FullCycle_PairWithPhoneNumber_ThenAMessageArrives_TheHandlerGetsItWithNoConnect()
	{
		var ct = TestContext.Current.CancellationToken;
		var client = await Sessions.GetOrCreateAsync(SessionId.From("pair-events-phone"), ct);
		using var received = new Received<MessageReceived>(client);

		await foreach (var update in client.PairWithPhoneNumberAsync("+1 555 123 4567", ct))
		{
			if (update is PairingUpdate.PairingCode)
				_ = await fixture.Fake.LinkDeviceAsync(new LinkDeviceRequest { SessionId = "pair-events-phone", Jid = SessionSetup.Jid("pair-events-phone") }, cancellationToken: ct);
		}

		_ = await fixture.Fake.DeliverMessageAsync(Deliveries.Message("pair-events-phone", "M1"), cancellationToken: ct);

		Assert.Equal(Deliveries.Received("pair-events-phone", "M1"), await received.NextAsync(ct));
	}

	[Fact(Timeout = Timeout)]
	public async Task Pair_Cancelled_StartsNoEvents_TheHandlerGetsThePairingStatesOnTheConnect()
	{
		var ct = TestContext.Current.CancellationToken;
		var client = await Sessions.GetOrCreateAsync(SessionId.From("pair-events-cancelled"), ct);
		using var received = new Received<SessionStateChanged>(client);
		using var cancel = CancellationTokenSource.CreateLinkedTokenSource(ct);
		_ = await Assert.ThrowsAnyAsync<OperationCanceledException>(
			async () =>
			{
				await foreach (var update in client.PairWithQrCodeAsync(cancel.Token).ConfigureAwait(false))
					await cancel.CancelAsync().ConfigureAwait(false);
			}
		);

		await ValidateNoEventsStartedAsync(client, received, "pair-events-cancelled", ct);
	}

	[Fact(Timeout = Timeout)]
	public async Task Pair_TheCodesRunOut_StartsNoEvents_TheHandlerGetsThePairingStatesOnTheConnect()
	{
		var ct = TestContext.Current.CancellationToken;
		var client = await Sessions.GetOrCreateAsync(SessionId.From("pair-events-expired"), ct);
		using var received = new Received<SessionStateChanged>(client);
		_ = await Assert.ThrowsAsync<PairingException>(
			() =>
				PairAsync(
					client,
					() => fixture.Fake.ExpireQrCodesAsync(new ExpireQrCodesRequest { SessionId = "pair-events-expired" }, cancellationToken: ct).ResponseAsync,
					ct
				)
		);

		await ValidateNoEventsStartedAsync(client, received, "pair-events-expired", ct);
	}

	[Fact(Timeout = Timeout)]
	public async Task Pair_ThePreKeyUploadFails_ThrowsPairingFailed_AndTheSessionIsDisconnectedAndStillPaired()
	{
		var ct = TestContext.Current.CancellationToken;
		var client = await Sessions.GetOrCreateAsync(SessionId.From("pair-prekeys"), ct);
		_ = await fixture.Fake.FailNextPreKeyUploadAsync(new FailNextPreKeyUploadRequest { SessionId = "pair-prekeys" }, cancellationToken: ct);

		var error = await Assert.ThrowsAsync<PairingException>(() => PairLinkingAsync(client, "pair-prekeys", ct));

		Assert.Equal(WhatsmeowErrorCode.PairingFailed, error.ErrorCode);
		Assert.Equal(new SessionState.Disconnected(), await SessionSetup.StateAsync(client, ct));
		await client.ConnectAsync(ct);
		await using var watch = StateWatch.Start(client, ct);
		await watch.ReachesAsync(new SessionState.Connected(), new SessionState.Disconnected(), new SessionState.Connecting());
	}

	[Fact(Timeout = Timeout)]
	public async Task Pair_TheCodesRunOut_ThrowsPairingTimedOut_AndTheSessionIsUnpaired()
	{
		var ct = TestContext.Current.CancellationToken;
		var client = await Sessions.GetOrCreateAsync(SessionId.From("pair-expired"), ct);

		var error = await Assert.ThrowsAsync<PairingException>(
			() => PairAsync(client, () => fixture.Fake.ExpireQrCodesAsync(new ExpireQrCodesRequest { SessionId = "pair-expired" }, cancellationToken: ct).ResponseAsync, ct)
		);

		Assert.Equal(WhatsmeowErrorCode.PairingTimedOut, error.ErrorCode);
		await ReachesUnpairedAsync(client, ct);
	}

	[Fact(Timeout = Timeout)]
	public async Task Pair_TheConnectionDrops_ThrowsConnectionFailed_AndTheSessionIsUnpaired()
	{
		var ct = TestContext.Current.CancellationToken;
		var client = await Sessions.GetOrCreateAsync(SessionId.From("pair-dropped"), ct);

		var error = await Assert.ThrowsAsync<NotConnectedException>(
			() => PairAsync(client, () => fixture.Fake.DropConnectionAsync(new DropConnectionRequest { SessionId = "pair-dropped" }, cancellationToken: ct).ResponseAsync, ct)
		);

		Assert.Equal(WhatsmeowErrorCode.ConnectionFailed, error.ErrorCode);
		await ReachesUnpairedAsync(client, ct);
	}

	[Fact(Timeout = Timeout)]
	public async Task Pair_TheConnectFails_ThrowsConnectionFailed_AndTheSessionIsUnpaired()
	{
		var ct = TestContext.Current.CancellationToken;
		var client = await Sessions.GetOrCreateAsync(SessionId.From("pair-connect-fails"), ct);
		_ = await fixture.Fake.FailNextConnectAsync(new FailNextConnectRequest { SessionId = "pair-connect-fails" }, cancellationToken: ct);

		var error = await Assert.ThrowsAsync<NotConnectedException>(() => PairAsync(client, () => Task.CompletedTask, ct));

		Assert.Equal(WhatsmeowErrorCode.ConnectionFailed, error.ErrorCode);
		Assert.Equal(new SessionState.Created(), await SessionSetup.StateAsync(client, ct));
	}

	[Fact(Timeout = Timeout)]
	public async Task Pair_Cancelled_StopsPairing_AndTheSessionStaysUnpaired()
	{
		var ct = TestContext.Current.CancellationToken;
		var client = await Sessions.GetOrCreateAsync(SessionId.From("pair-cancelled"), ct);
		using var cancel = CancellationTokenSource.CreateLinkedTokenSource(ct);

		_ = await Assert.ThrowsAnyAsync<OperationCanceledException>(
			async () =>
			{
				await foreach (var update in client.PairWithQrCodeAsync(cancel.Token).ConfigureAwait(false))
					await cancel.CancelAsync().ConfigureAwait(false);
			}
		);

		// Pairing stopped: the connection closed, and the session is as it was, so it can be paired again.
		var sent = await fixture.Fake.WaitForSentAsync(new WaitForSentRequest { SessionId = "pair-cancelled" }, cancellationToken: ct);
		Assert.Equal(new Sent { Index = sent.Sent.Index, SessionId = "pair-cancelled", Disconnect = new Disconnect() }, sent.Sent);
		await ReachesUnpairedAsync(client, ct);
		await using var again = client.PairWithQrCodeAsync(ct).GetAsyncEnumerator(ct);
		Assert.True(await again.MoveNextAsync());
		_ = Assert.IsType<PairingUpdate.QrCode>(again.Current);
	}

	[Fact(Timeout = Timeout)]
	public async Task Pair_AlreadyPaired_Throws_AndLeavesThePairingUntouched()
	{
		var ct = TestContext.Current.CancellationToken;
		var client = await SessionSetup.ConnectedAsync(fixture, "pair-already", ct);

		var error = await Assert.ThrowsAsync<PairingException>(() => PairAsync(client, () => Task.CompletedTask, ct));

		Assert.Equal(WhatsmeowErrorCode.AlreadyPaired, error.ErrorCode);
		Assert.Equal(new SessionState.Connected(), await SessionSetup.StateAsync(client, ct));
		// Still paired and connected: WhatsApp can be told to unlink it.
		await client.LogoutAsync(ct);
	}

	[Fact(Timeout = Timeout)]
	public async Task Pair_WhilePairing_ThrowsPairingInProgress_AndTheFirstGoesOn()
	{
		var ct = TestContext.Current.CancellationToken;
		var client = await Sessions.GetOrCreateAsync(SessionId.From("pair-twice"), ct);
		await using var first = client.PairWithQrCodeAsync(ct).GetAsyncEnumerator(ct);
		Assert.True(await first.MoveNextAsync());

		var second = await Assert.ThrowsAsync<PairingException>(() => PairAsync(client, () => Task.CompletedTask, ct));
		var connect = await Assert.ThrowsAsync<PairingException>(() => client.ConnectAsync(ct));

		Assert.Equal(WhatsmeowErrorCode.PairingInProgress, second.ErrorCode);
		Assert.Equal(WhatsmeowErrorCode.PairingInProgress, connect.ErrorCode);
		_ = await fixture.Fake.LinkDeviceAsync(new LinkDeviceRequest { SessionId = "pair-twice", Jid = SessionSetup.Jid("pair-twice") }, cancellationToken: ct);
		Assert.True(await first.MoveNextAsync());
		Assert.Equal(new PairingUpdate.Paired(), first.Current);
	}

	[Fact(Timeout = Timeout)]
	public async Task Pair_AfterALogout_PairsAgain()
	{
		var ct = TestContext.Current.CancellationToken;
		var client = await SessionSetup.ConnectedAsync(fixture, "pair-again", ct);
		await client.LogoutAsync(ct);

		await PairLinkingAsync(client, "pair-again", ct);

		Assert.Equal(new SessionState.Connected(), await SessionSetup.StateAsync(client, ct));
	}

	[Fact(Timeout = Timeout)]
	public async Task Pair_AnInvalidPhoneNumber_ThrowsValidation_AndTheSessionStaysUnpaired()
	{
		var ct = TestContext.Current.CancellationToken;
		var client = await Sessions.GetOrCreateAsync(SessionId.From("pair-bad-phone"), ct);

		var error = await Assert.ThrowsAsync<WhatsmeowValidationException>(
			async () =>
			{
				await foreach (var update in client.PairWithPhoneNumberAsync("0555 1234 567", ct).ConfigureAwait(false))
					Assert.Fail($"pairing yielded {update}");
			}
		);

		Assert.Equal(WhatsmeowErrorCode.InvalidArgument, error.ErrorCode);
		Assert.Equal(new SessionState.Created(), await SessionSetup.StateAsync(client, ct));
	}

	[Fact(Timeout = Timeout)]
	public async Task Pair_WhatsAppRateLimitsThePhoneCode_ThrowsRateLimited_AndTheSessionIsUnpaired()
	{
		var ct = TestContext.Current.CancellationToken;
		var client = await Sessions.GetOrCreateAsync(SessionId.From("pair-rate-limited"), ct);
		_ = await fixture.Fake.FailNextPairPhoneAsync(new FailNextPairPhoneRequest { SessionId = "pair-rate-limited", Code = 429 }, cancellationToken: ct);

		var error = await Assert.ThrowsAsync<RateLimitedException>(
			async () =>
			{
				await foreach (var update in client.PairWithPhoneNumberAsync("+1 555 123 4567", ct).ConfigureAwait(false))
					Assert.Fail($"pairing yielded {update}");
			}
		);

		Assert.Equal(WhatsmeowErrorCode.RateLimited, error.ErrorCode);
		await ReachesUnpairedAsync(client, ct);
	}

	/// <summary>Pairs by QR code; on the first code the fake phone links the device.</summary>
	private async Task PairLinkingAsync(IWhatsAppClient client, string id, CancellationToken cancellationToken) =>
		await PairAsync(
				client,
				() => fixture.Fake.LinkDeviceAsync(new LinkDeviceRequest { SessionId = id, Jid = SessionSetup.Jid(id) }, cancellationToken: cancellationToken).ResponseAsync,
				cancellationToken
			)
			.ConfigureAwait(false);

	/// <summary>Pairs by QR code; on the first code, the fake WhatsApp does <paramref name="onFirstCode"/>.</summary>
	private static async Task PairAsync(IWhatsAppClient client, Func<Task> onFirstCode, CancellationToken cancellationToken)
	{
		var codes = 0;
		await foreach (var update in client.PairWithQrCodeAsync(cancellationToken).ConfigureAwait(false))
		{
			if (update is PairingUpdate.QrCode && ++codes == 1)
				await onFirstCode().ConfigureAwait(false);
		}
	}

	/// <summary>
	/// After a pairing that ended without <see cref="PairingUpdate.Paired"/>, the session's events have not started, and
	/// the handler <paramref name="received"/> registered before it gets the pairing's states first once the session
	/// is connected. The check is the client's own: an event that never arrives cannot be told from a late one without
	/// waiting, and a reader started at the end of the pairing still lost the race to a later handler 9 times in 10
	/// (ADR 0052).
	/// </summary>
	private async Task ValidateNoEventsStartedAsync(IWhatsAppClient client, Received<SessionStateChanged> received, string id, CancellationToken cancellationToken)
	{
		Assert.False(((SessionClient)client).EventsStarted);
		await ReachesUnpairedAsync(client, cancellationToken).ConfigureAwait(false);
		_ = await fixture.Fake.LinkDeviceAsync(new LinkDeviceRequest { SessionId = id, Jid = SessionSetup.Jid(id) }, cancellationToken: cancellationToken).ConfigureAwait(false);

		await client.ConnectAsync(cancellationToken).ConfigureAwait(false);

		SessionState[] states = [new SessionState.Pairing(), new SessionState.Created()];
		Assert.Equal(
			states.Select(state => new SessionStateChanged { SessionId = SessionId.From(id), State = state }),
			await received.NextAsync(states.Length, cancellationToken).ConfigureAwait(false)
		);
	}

	/// <summary>The session goes back to Created: the sidecar records it once the pairing ended.</summary>
	private static async Task ReachesUnpairedAsync(IWhatsAppClient client, CancellationToken cancellationToken)
	{
		var watch = StateWatch.Start(client, cancellationToken);
		await using (watch.ConfigureAwait(false))
			await watch.ReachesAsync(new SessionState.Created(), new SessionState.Pairing()).ConfigureAwait(false);
	}
}
