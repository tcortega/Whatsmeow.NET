using System.Security.Cryptography;
using System.Threading.Channels;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Whatsmeow.Events;
using Whatsmeow.IntegrationTests.Events;
using Whatsmeow.IntegrationTests.Fake;
using Whatsmeow.IntegrationTests.Sessions;
using Whatsmeow.IntegrationTests.Support;
using Whatsmeow.Sessions;

namespace Whatsmeow.IntegrationTests.Sidecar;

/// <summary>
/// External mode (ADR 0046): a client connected to a sidecar it did not start, which the test runs standalone, as an
/// orchestrator would. Each test starts its own, because what happens to that process is the behaviour under test.
/// </summary>
public sealed class ExternalTests(FakeSidecar fixture, SidecarBinaries binaries)
{
	private const int Timeout = 120_000;

	public static TheoryData<string> Listens => ["unix", "tcp"];

	[Theory(Timeout = Timeout)]
	[MemberData(nameof(Listens))]
	public async Task FullCycle_AStandaloneSidecar_ConnectsAndASessionGetsItsMessages(string listen)
	{
		var ct = TestContext.Current.CancellationToken;
		using var logs = new LogCapture();
		var token = StandaloneSidecar.NewTokenFile(binaries.NewDirectory());
		await using var standalone = await StartFakeAsync(Listen(listen), token, ct);

		await using var sidecar = await WhatsmeowSidecar.StartAsync(Options(standalone, token), logs.Factory, ct);
		var fake = new FakeWhatsAppService.FakeWhatsAppServiceClient(sidecar.CallInvoker);

		Assert.Equal(binaries.Expected, await sidecar.GetInfoAsync(ct));
		var client = await LinkAsync(sidecar, fake, "external-fullcycle", ct);
		using var received = new Received<MessageReceived>(client);
		await client.ConnectAsync(ct);
		await Deliveries.DeliverAsync(fake, "external-fullcycle", ct, "M1");

		Assert.Equal(Deliveries.Received("external-fullcycle", "M1"), await received.NextAsync(ct));
		_ = Assert.Single(logs.Named("ExternalSidecarConnected"), r => r.GetStructuredStateValue("Address") == standalone.Address);
	}

	[Fact(Timeout = Timeout)]
	public async Task Connect_AnotherReleaseOfTheSidecar_IsAccepted()
	{
		var ct = TestContext.Current.CancellationToken;
		var token = StandaloneSidecar.NewTokenFile(binaries.NewDirectory());
		await using var standalone = await StandaloneSidecar.StartAsync(binaries.OtherVersionSidecar, binaries.NewDatabase(), "tcp:127.0.0.1:0", token, [], ct);

		// Only the protocol must match an external sidecar, which its operator upgrades on its own (§8.5).
		await using var sidecar = await WhatsmeowSidecar.StartAsync(Options(standalone, token), ct);

		var info = await sidecar.GetInfoAsync(ct);
		Assert.Equal(("0.0.0-other", 1), (info.Version, info.Protocol));
		Assert.NotEqual(SidecarBinaries.ClientVersion, info.Version, StringComparer.Ordinal);
	}

	[Fact(Timeout = Timeout)]
	public async Task Connect_AProtocolOutOfRange_ThrowsAndTheSidecarKeepsRunning()
	{
		var ct = TestContext.Current.CancellationToken;
		var token = StandaloneSidecar.NewTokenFile(binaries.NewDirectory());
		await using var standalone = await StandaloneSidecar.StartAsync(binaries.Sidecar, binaries.NewDatabase(), "tcp:127.0.0.1:0", token, [], ct);

		var error = await Assert.ThrowsAsync<SidecarVersionMismatchException>(() =>
			WhatsmeowSidecar.StartAsync(Options(standalone, token), Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance, minProtocol: 2, maxProtocol: 3, ct)
		);

		Assert.Equal($"The external sidecar is version {SidecarBinaries.ClientVersion} with protocol 1; this client needs a protocol from 2 to 3.", error.Message);
		Assert.False(standalone.HasExited);
		await using var another = await WhatsmeowSidecar.StartAsync(Options(standalone, token), ct);
		Assert.Equal(1, (await another.GetInfoAsync(ct)).Protocol);
	}

	[Fact(Timeout = Timeout)]
	public async Task Connect_AWrongToken_IsRefusedAtOnce()
	{
		var ct = TestContext.Current.CancellationToken;
		var directory = binaries.NewDirectory();
		var token = StandaloneSidecar.NewTokenFile(directory);
		await using var standalone = await StandaloneSidecar.StartAsync(binaries.Sidecar, binaries.NewDatabase(), "tcp:127.0.0.1:0", token, [], ct);
		var wrong = Path.Combine(directory, "wrong");
		StandaloneSidecar.WriteToken(wrong);

		// An hour to connect: a refused token is not a sidecar that is not there yet, so it is never retried.
		var options = Options(standalone, wrong);
		options.StartupTimeout = TimeSpan.FromHours(1);
		var error = await Assert.ThrowsAsync<SidecarUnavailableException>(() => WhatsmeowSidecar.StartAsync(options, ct));

		Assert.Equal(WhatsmeowErrorCode.Unauthenticated, error.ErrorCode);
		Assert.Equal($"The sidecar at {standalone.Address} refused the token: its --token-file and this client's TokenPath do not hold the same token.", error.Message);
	}

	[Fact(Timeout = Timeout)]
	public async Task Connect_NothingListens_AsksAgainEverySecondAndFailsAfterTheStartupTimeout()
	{
		var ct = TestContext.Current.CancellationToken;
		var time = new TimerObservingTimeProvider();
		var token = StandaloneSidecar.NewTokenFile(binaries.NewDirectory());
		var address = "unix:" + StandaloneSidecar.NewSocketPath();
		var options = new WhatsmeowSidecarOptions { External = new ExternalSidecarOptions { Address = address, TokenPath = token } };

		// The orchestrator may start the sidecar after the application: the client asks again until the timeout.
		var connecting = WhatsmeowSidecar.StartAsync(options, Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance, time, ct);
		Assert.Equal(TimeSpan.FromSeconds(30), await time.Timers.Reader.ReadAsync(ct));
		for (var second = 1; second < 30; second++)
		{
			Assert.Equal(TimeSpan.FromSeconds(1), await time.Timers.Reader.ReadAsync(ct));
			time.Advance(TimeSpan.FromSeconds(1));
		}

		Assert.Equal(TimeSpan.FromSeconds(1), await time.Timers.Reader.ReadAsync(ct));
		Assert.False(connecting.IsCompleted);
		time.Advance(TimeSpan.FromSeconds(1));

		var error = await Assert.ThrowsAsync<SidecarUnavailableException>(() => connecting);
		Assert.Equal($"The sidecar at {address} did not answer within 00:00:30.", error.Message);
	}

	[Fact(Timeout = Timeout)]
	public async Task Connect_TheSidecarStartsAfterTheClient_Connects()
	{
		var ct = TestContext.Current.CancellationToken;
		var time = new TimerObservingTimeProvider();
		var token = StandaloneSidecar.NewTokenFile(binaries.NewDirectory());
		var path = StandaloneSidecar.NewSocketPath();
		var options = new WhatsmeowSidecarOptions { External = new ExternalSidecarOptions { Address = "unix:" + path, TokenPath = token } };

		var connecting = WhatsmeowSidecar.StartAsync(options, Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance, time, ct);
		_ = await time.Timers.Reader.ReadAsync(ct);
		Assert.Equal(TimeSpan.FromSeconds(1), await time.Timers.Reader.ReadAsync(ct));
		await using var standalone = await StandaloneSidecar.StartAsync(binaries.Sidecar, binaries.NewDatabase(), "unix:" + path, token, [], ct);
		time.Advance(TimeSpan.FromSeconds(1));

		await using var sidecar = await connecting;
		Assert.Equal(SidecarBinaries.ClientVersion, (await sidecar.GetInfoAsync(ct)).Version);
	}

	[Fact(Timeout = Timeout)]
	public async Task Restart_TheSidecarIsKilledAndComesBackWithANewToken_TheSessionGoesOnWithNoEventLost()
	{
		var ct = TestContext.Current.CancellationToken;
		using var logs = new LogCapture();
		var token = StandaloneSidecar.NewTokenFile(binaries.NewDirectory());
		var database = binaries.NewDatabase();
		var listen = "unix:" + StandaloneSidecar.NewSocketPath();
		var standalone = await StartFakeAsync(listen, token, ct, database);
		try
		{
			await using var sidecar = await WhatsmeowSidecar.StartAsync(Options(standalone, token), logs.Factory, ct);
			var fake = new FakeWhatsAppService.FakeWhatsAppServiceClient(sidecar.CallInvoker);
			var client = await LinkAsync(sidecar, fake, "external-restart", ct);
			using var received = new Received<MessageReceived>(client);
			await client.ConnectAsync(ct);
			await Deliveries.DeliverAsync(fake, "external-restart", ct, "M1");
			_ = await received.NextAsync(ct);

			// Recorded, maybe not yet streamed or acked, when the sidecar dies: they come from the cursor.
			await Deliveries.DeliverAsync(fake, "external-restart", ct, "M2", "M3");
			await standalone.KillAsync();
			await standalone.DisposeAsync();

			// The orchestrator starts it again on the same database and address, with a rotated secret.
			StandaloneSidecar.WriteToken(token);
			standalone = await StartFakeAsync(listen, token, ct, database);
			_ = await logs.WaitForAsync(r => r.Id.Name == "SidecarRestarted", ct);

			await using (var watch = StateWatch.Start(client, ct))
				await watch.ReachesAsync(new SessionState.Connected(), new SessionState.Connecting(), new SessionState.Disconnected());
			await Deliveries.DeliverAsync(fake, "external-restart", ct, "M4");

			// At least once: M1 may come again, unacked when the sidecar died; none is missing.
			HashSet<MessageId> after = [MessageId.From("M2"), MessageId.From("M3"), MessageId.From("M4")];
			var ids = new HashSet<MessageId>();
			while (!ids.IsSupersetOf(after))
				_ = ids.Add((await received.NextAsync(ct)).Id);
			Assert.Subset(new HashSet<MessageId>([MessageId.From("M1"), .. after]), ids);
			_ = Assert.Single(logs.Named("ExternalSidecarLost"));
		}
		finally
		{
			await standalone.DisposeAsync();
		}
	}

	[Fact(Timeout = Timeout)]
	public async Task Dispose_TheExternalSidecarKeepsRunning()
	{
		var ct = TestContext.Current.CancellationToken;
		var token = StandaloneSidecar.NewTokenFile(binaries.NewDirectory());
		await using var standalone = await StartFakeAsync("tcp:127.0.0.1:0", token, ct);
		var first = await WhatsmeowSidecar.StartAsync(Options(standalone, token), ct);
		var client = await LinkAsync(first, new FakeWhatsAppService.FakeWhatsAppServiceClient(first.CallInvoker), "external-dispose", ct);
		await client.ConnectAsync(ct);

		await first.DisposeAsync();

		// The client never stops what it did not start: the process, and the session in it, go on. A sidecar told to
		// shut down refuses every new call at once, so the next client would not connect.
		await using var second = await WhatsmeowSidecar.StartAsync(Options(standalone, token), ct);
		var again = await second.Sessions.GetOrCreateAsync(SessionId.From("external-dispose"), ct);
		Assert.Equal(new SessionState.Connected(), await SessionSetup.StateAsync(again, ct));
		Assert.False(standalone.HasExited, standalone.Stderr);
	}

	[Fact(Timeout = Timeout)]
	public async Task TwoClients_EachWithItsConsumer_EachGetsEveryEvent()
	{
		var ct = TestContext.Current.CancellationToken;
		var token = StandaloneSidecar.NewTokenFile(binaries.NewDirectory());
		await using var standalone = await StartFakeAsync("tcp:127.0.0.1:0", token, ct);
		await using var first = await WhatsmeowSidecar.StartAsync(Options(standalone, token, "first"), ct);
		await using var second = await WhatsmeowSidecar.StartAsync(Options(standalone, token, "second"), ct);
		var fake = new FakeWhatsAppService.FakeWhatsAppServiceClient(first.CallInvoker);
		var one = await LinkAsync(first, fake, "external-consumers", ct);
		var other = await second.Sessions.GetOrCreateAsync(SessionId.From("external-consumers"), ct);
		using var firstReceived = new Received<MessageReceived>(one);
		using var secondReceived = new Received<MessageReceived>(other);
		await one.ConnectAsync(ct);
		await other.ConnectAsync(ct);

		await Deliveries.DeliverAsync(fake, "external-consumers", ct, "M1", "M2");

		var expected = new[] { Deliveries.Received("external-consumers", "M1"), Deliveries.Received("external-consumers", "M2") };
		Assert.Equal(expected, await firstReceived.NextAsync(2, ct));
		Assert.Equal(expected, await secondReceived.NextAsync(2, ct));
	}

	[Fact(Timeout = Timeout)]
	public async Task Media_NoSharedRoot_TravelsInChunksEvenFromTheSidecarsRoot()
	{
		var ct = TestContext.Current.CancellationToken;
		using var logs = new LogCapture();
		var token = StandaloneSidecar.NewTokenFile(binaries.NewDirectory());
		var root = binaries.NewDirectory();
		await using var standalone = await StandaloneSidecar.StartAsync(fixture.Binary, binaries.NewDatabase(), "tcp:127.0.0.1:0", token, ["--fake-whatsapp", "--media-root", root], ct);
		await using var sidecar = await WhatsmeowSidecar.StartAsync(Options(standalone, token), logs.Factory, ct);
		var client = await LinkAsync(sidecar, new FakeWhatsAppService.FakeWhatsAppServiceClient(sidecar.CallInvoker), "external-media", ct);
		await client.ConnectAsync(ct);
		await using (var watch = StateWatch.Start(client, ct))
			await watch.ReachesAsync(new SessionState.Connected(), new SessionState.Connecting());

		// The file is in the sidecar's root, but this client was given none: it cannot know the two share it.
		var file = Path.Combine(root, "photo.jpg");
		var content = RandomNumberGenerator.GetBytes(100 * 1024);
		await File.WriteAllBytesAsync(file, content, ct);
		var media = await client.UploadMediaFileAsync(file, MediaType.Image, ct);

		Assert.Equal(content.Length, media.FileLength);
		_ = Assert.Single(logs.Named("UploadingInChunks"));
		Assert.Empty(logs.Named("UploadingByPath"));
	}

	[Fact(Timeout = Timeout)]
	public async Task Dispose_AcksWhatTheHandlersFinished_SoTheNextClientGetsOnlyNewEvents()
	{
		var ct = TestContext.Current.CancellationToken;
		var token = StandaloneSidecar.NewTokenFile(binaries.NewDirectory());
		await using var standalone = await StartFakeAsync("tcp:127.0.0.1:0", token, ct);

		// The clock never moves, so the ack timer never fires: only the dispose can ack (ARCHITECTURE §8.4).
		await using (var first = await WhatsmeowSidecar.StartAsync(Options(standalone, token), NullLoggerFactory.Instance, new FakeTimeProvider(), ct))
		{
			var fake = new FakeWhatsAppService.FakeWhatsAppServiceClient(first.CallInvoker);
			var client = await LinkAsync(first, fake, "external-ack", ct);
			using var received = new Received<MessageReceived>(client);
			await client.ConnectAsync(ct);
			await Deliveries.DeliverAsync(fake, "external-ack", ct, "M1", "M2");
			_ = await received.NextAsync(2, ct);
		}

		await using var second = await WhatsmeowSidecar.StartAsync(Options(standalone, token), ct);
		var again = await second.Sessions.GetOrCreateAsync(SessionId.From("external-ack"), ct);
		using var next = new Received<MessageReceived>(again);
		await again.ConnectAsync(ct);
		await Deliveries.DeliverAsync(new FakeWhatsAppService.FakeWhatsAppServiceClient(second.CallInvoker), "external-ack", ct, "M3");

		Assert.Equal(Deliveries.Received("external-ack", "M3"), await next.NextAsync(ct));
	}

	private static string Listen(string kind) => kind == "unix" ? "unix:" + StandaloneSidecar.NewSocketPath() : "tcp:127.0.0.1:0";

	internal static WhatsmeowSidecarOptions Options(StandaloneSidecar standalone, string token, string consumer = "client") =>
		new() { External = new ExternalSidecarOptions { Address = standalone.Address, TokenPath = token, Consumer = consumer } };

	private Task<StandaloneSidecar> StartFakeAsync(string listen, string token, CancellationToken ct, string? database = null) =>
		StandaloneSidecar.StartAsync(fixture.Binary, database ?? binaries.NewDatabase(), listen, token, ["--fake-whatsapp"], ct);

	/// <summary>A session the phone linked, on a sidecar of the test's own (SessionSetup.LinkedAsync, for the shared one).</summary>
	private static async Task<IWhatsAppClient> LinkAsync(WhatsmeowSidecar sidecar, FakeWhatsAppService.FakeWhatsAppServiceClient fake, string id, CancellationToken ct)
	{
		var client = await sidecar.Sessions.GetOrCreateAsync(SessionId.From(id), ct).ConfigureAwait(false);
		_ = await fake.LinkDeviceAsync(new LinkDeviceRequest { SessionId = id, Jid = SessionSetup.Jid(id) }, cancellationToken: ct).ConfigureAwait(false);
		return client;
	}

	/// <summary>A fake clock that reports the due time of every timer created on it, such as each Task.Delay.</summary>
	private sealed class TimerObservingTimeProvider : FakeTimeProvider
	{
		public Channel<TimeSpan> Timers { get; } = Channel.CreateUnbounded<TimeSpan>();

		public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
		{
			var timer = base.CreateTimer(callback, state, dueTime, period);
			_ = Timers.Writer.TryWrite(dueTime);
			return timer;
		}
	}
}
