using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.Options;
using Whatsmeow.IntegrationTests.Fake;
using Whatsmeow.IntegrationTests.Sessions;
using Whatsmeow.IntegrationTests.Support;
using Whatsmeow.Messaging;
using Whatsmeow.Sessions;
using Raise = Whatsmeow.IntegrationTests.Fake.RaisePermanentDisconnectRequest;

namespace Whatsmeow.IntegrationTests.Messaging;

/// <summary>
/// Each session's send pacing (ADR 0042). The options apply when the sidecar starts, so the test of them starts a
/// fake-mode sidecar of its own, with a pacing whose wait it shows without waiting: an hour between sends.
/// </summary>
public sealed class SendPacingTests(FakeSidecar fixture, SidecarBinaries binaries)
{
	private const int Timeout = 120_000;

	[Fact(Timeout = Timeout)]
	public async Task SendPacing_EverySessionPacesWithIt_ABudgetEach_AndASendWaitingItsTurnIsCancelledHavingSentNothing()
	{
		var ct = TestContext.Current.CancellationToken;
		using var logs = new LogCapture();
		var options = new WhatsmeowSidecarOptions
		{
			SidecarPath = fixture.Binary,
			DatabasePath = binaries.NewDatabase(),
			SendPacing = new SendPacingOptions { Burst = 2, Interval = TimeSpan.FromHours(1) },
		};
		await using var sidecar = await WhatsmeowSidecar.StartFakeWhatsAppAsync(options, logs.Factory, ct);
		var fake = new FakeWhatsAppService.FakeWhatsAppServiceClient(sidecar.CallInvoker);
		var first = await ConnectedAsync(sidecar, fake, "pacing-first", ct);
		var other = await ConnectedAsync(sidecar, fake, "pacing-other", ct);
		var to = Jid.Parse("900000201@lid");

		// The first session's burst of two leaves at once.
		_ = await first.SendTextAsync(to, "1", cancellationToken: ct);
		_ = await first.SendTextAsync(to, "2", cancellationToken: ct);
		var burst = await SentAsync(fake, "pacing-first", 0, ct);
		var last = await SentAsync(fake, "pacing-first", burst.Index, ct);

		// Its third waits its turn, an hour away, while the other session's burst leaves.
		using var call = CancellationTokenSource.CreateLinkedTokenSource(ct);
		var third = first.SendTextAsync(to, "3", cancellationToken: call.Token);
		_ = await other.SendTextAsync(to, "1", cancellationToken: ct);
		_ = await other.SendTextAsync(to, "2", cancellationToken: ct);
		Assert.False(third.IsCompleted);

		await call.CancelAsync();
		_ = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => third);

		// The third never left: the first session's next entry is the read receipt it sent after it.
		await first.MarkReadAsync(to, [MessageId.New()], cancellationToken: ct);
		Assert.Equal(Sent.KindOneofCase.Read, (await SentAsync(fake, "pacing-first", last.Index, ct)).KindCase);
	}

	[Fact(Timeout = Timeout)]
	public async Task Send_TheSessionIsTemporarilyBanned_TheQueuedAndNewSendsThrowWithItsEnd()
	{
		var ct = TestContext.Current.CancellationToken;
		const string Session = "pacing-banned";
		var client = await SessionSetup.ConnectedAsync(fixture, Session, ct);
		await using var watch = await WatchAsync(client);
		var to = Jid.Parse("900000202@lid");
		_ = await fixture.Fake.HoldNextSendAsync(new HoldNextSendRequest { SessionId = Session }, cancellationToken: ct);

		var held = client.SendTextAsync(to, "in flight", cancellationToken: ct);
		_ = await SentAsync(fixture.Fake, Session, 0, ct);
		var queued = client.SendTextAsync(to, "queued", cancellationToken: ct);
		var state = await BanAsync(Session, watch, TimeSpan.FromHours(1), ct);

		var error = await Assert.ThrowsAsync<TemporarilyBannedException>(() => queued);
		var again = await Assert.ThrowsAsync<TemporarilyBannedException>(() => client.SendTextAsync(to, "new", cancellationToken: ct));

		Assert.Equal((WhatsmeowErrorCode.TemporarilyBanned, state.Until, state.Until), (error.ErrorCode, error.Until, again.Until));
		// The one handed off may have reached WhatsApp (message.proto).
		_ = await Assert.ThrowsAsync<OutcomeUnknownException>(() => held);
	}

	// A ban with an end refuses Connect until then (PermanentDisconnectTests); one without lets the application connect.
	[Fact(Timeout = Timeout)]
	public async Task Send_AfterABan_ThrowsUntilTheApplicationConnectsTheSessionAgain()
	{
		var ct = TestContext.Current.CancellationToken;
		const string Session = "pacing-ban-ends";
		var client = await SessionSetup.ConnectedAsync(fixture, Session, ct);
		await using var watch = await WatchAsync(client);
		var to = Jid.Parse("900000204@lid");
		_ = await BanAsync(Session, watch, TimeSpan.Zero, ct);

		_ = await Assert.ThrowsAsync<TemporarilyBannedException>(() => client.SendTextAsync(to, "banned", cancellationToken: ct));

		// Nothing connects it; the application does, and then it sends.
		await client.ConnectAsync(ct);
		await watch.ReachesAsync(new SessionState.Connected(), new SessionState.Connecting());
		Assert.Equal(to, (await client.SendTextAsync(to, "after", cancellationToken: ct)).Chat);
	}

	/// <summary>A watch of the connected session's states, past its first, Connected.</summary>
	private static async Task<StateWatch> WatchAsync(IWhatsAppClient client)
	{
		var watch = StateWatch.Start(client, TestContext.Current.CancellationToken);
		Assert.Equal(new SessionState.Connected(), await watch.NextAsync().ConfigureAwait(false));
		return watch;
	}

	/// <summary>Bans the session for <paramref name="expire"/>, none for zero, and returns its state once it is TempBanned.</summary>
	private async Task<SessionState.TempBanned> BanAsync(string session, StateWatch watch, TimeSpan expire, CancellationToken ct)
	{
		_ = await fixture.Fake.RaisePermanentDisconnectAsync(
			new Raise { SessionId = session, TemporaryBan = new Raise.Types.TemporaryBan { Code = 101, Expire = Duration.FromTimeSpan(expire) } },
			cancellationToken: ct
		).ConfigureAwait(false);
		return Assert.IsType<SessionState.TempBanned>(await watch.NextAsync().ConfigureAwait(false));
	}

	[Fact]
	public void SendPacing_ByDefault_IsABurstOfFiveThenOneASecond()
	{
		var pacing = new WhatsmeowSidecarOptions().SendPacing;

		Assert.Equal((5, TimeSpan.FromSeconds(1)), (pacing.Burst, pacing.Interval));
	}

	private static readonly Dictionary<string, (Action<WhatsmeowSidecarOptions> Set, string Failure)> s_invalid = new(StringComparer.Ordinal)
	{
		["no burst"] = (o => o.SendPacing.Burst = 0, "SendPacing.Burst 0 must be 1 to 1000."),
		["a burst too large"] = (o => o.SendPacing.Burst = 1001, "SendPacing.Burst 1001 must be 1 to 1000."),
		["no interval"] = (o => o.SendPacing.Interval = TimeSpan.Zero, "SendPacing.Interval 00:00:00 must be more than zero and at most 01:00:00."),
		["a negative interval"] = (o => o.SendPacing.Interval = TimeSpan.FromSeconds(-1), "SendPacing.Interval -00:00:01 must be more than zero and at most 01:00:00."),
		["an interval too long"] = (
			o => o.SendPacing.Interval = TimeSpan.FromHours(1) + TimeSpan.FromTicks(1),
			"SendPacing.Interval 01:00:00.0000001 must be more than zero and at most 01:00:00."
		),
		["no pacing"] = (o => o.SendPacing = null!, "SendPacing must not be null."),
	};

	public static TheoryData<string> Invalid => [.. s_invalid.Keys];

	[Theory(Timeout = 60_000)]
	[MemberData(nameof(Invalid))]
	public async Task Start_InvalidSendPacing_FailsBeforeSpawningAnything(string invalid)
	{
		var stub = binaries.Stage(binaries.HangingStub);
		var options = new WhatsmeowSidecarOptions { SidecarPath = stub };
		s_invalid[invalid].Set(options);

		var error = await Assert.ThrowsAsync<OptionsValidationException>(() => WhatsmeowSidecar.StartAsync(options, TestContext.Current.CancellationToken));

		Assert.Equal([s_invalid[invalid].Failure], error.Failures, StringComparer.Ordinal);
		Assert.False(Processes.StubStarted(stub));
	}

	private static async Task<IWhatsAppClient> ConnectedAsync(WhatsmeowSidecar sidecar, FakeWhatsAppService.FakeWhatsAppServiceClient fake, string id, CancellationToken ct)
	{
		var client = await sidecar.Sessions.GetOrCreateAsync(SessionId.From(id), ct).ConfigureAwait(false);
		_ = await fake.LinkDeviceAsync(new LinkDeviceRequest { SessionId = id, Jid = SessionSetup.Jid(id) }, cancellationToken: ct).ConfigureAwait(false);
		await client.ConnectAsync(ct).ConfigureAwait(false);
		var watch = StateWatch.Start(client, ct);
		await using (watch.ConfigureAwait(false))
			await watch.ReachesAsync(new SessionState.Connected(), new SessionState.Connecting()).ConfigureAwait(false);
		return client;
	}

	private static async Task<Sent> SentAsync(FakeWhatsAppService.FakeWhatsAppServiceClient fake, string session, ulong after, CancellationToken ct) =>
		(await fake.WaitForSentAsync(new WaitForSentRequest { SessionId = session, After = after }, cancellationToken: ct).ConfigureAwait(false)).Sent;
}

/// <summary>The pacing a restarted sidecar runs with: the options it started with, a copy (ADR 0042).</summary>
[Collection(nameof(SidecarCrashes))]
public sealed class SendPacingRestartTests(FakeSidecar fixture)
{
	[Fact(Timeout = 120_000)]
	public async Task SendPacing_ChangedByTheCallerAfterTheStart_ARestartedSidecarStillPacesAsItStarted()
	{
		var ct = TestContext.Current.CancellationToken;
		var pacing = fixture.Options.SendPacing;
		pacing.Burst = 1;
		pacing.Interval = TimeSpan.FromHours(1);
		try
		{
			await fixture.CrashAsync(ct);
			var client = await SessionSetup.ConnectedAsync(fixture, "pacing-restart", ct);
			var to = Jid.Parse("900000203@lid");

			// The burst it started with, five: the second send leaves at once, not an hour later.
			_ = await client.SendTextAsync(to, "1", cancellationToken: ct);
			Assert.Equal(to, (await client.SendTextAsync(to, "2", cancellationToken: ct)).Chat);
		}
		finally
		{
			fixture.Options.SendPacing = new SendPacingOptions();
		}
	}
}
