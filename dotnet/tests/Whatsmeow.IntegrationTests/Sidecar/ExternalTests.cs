using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Threading.Channels;
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
	public async Task Tcp_ThePeerGoesSilent_TheClientNoticesAndConnectsAgain()
	{
		var ct = TestContext.Current.CancellationToken;
		using var logs = new LogCapture();
		var token = StandaloneSidecar.NewTokenFile(binaries.NewDirectory());
		await using var standalone = await StandaloneSidecar.StartAsync(binaries.Sidecar, binaries.NewDatabase(), "tcp:127.0.0.1:0", token, [], ct);
		await using var relay = Relay.Start(IPEndPoint.Parse(standalone.Listen));
		var options = Options(standalone, token);
		options.External!.Address = $"tcp:127.0.0.1:{relay.Port}";
		options.External.KeepAlive = TimeSpan.FromSeconds(2);
		await using var sidecar = await WhatsmeowSidecar.StartAsync(options, logs.Factory, ct);

		// Idle for several pings, on the one connection: the sidecar answers them, where grpc-go's default policy would
		// close it after the third early one (sidecar/internal/server keepalive_test.go).
		await relay.PingsAnsweredAsync(5, ct);
		Assert.Empty(logs.Named("ExternalSidecarLost"));
		Assert.Equal(1, relay.Connections);

		// A vanished peer never closes its end: only the unanswered pings show it is gone.
		relay.Freeze();
		_ = await logs.WaitForAsync(r => r.Id.Name == "ExternalSidecarLost", ct);
		relay.Thaw();

		_ = await logs.WaitForAsync(r => r.Id.Name == "SidecarRestarted", ct);
		Assert.Equal(SidecarBinaries.ClientVersion, (await sidecar.GetInfoAsync(ct)).Version);
	}

	private static string Listen(string kind) => kind == "unix" ? "unix:" + StandaloneSidecar.NewSocketPath() : "tcp:127.0.0.1:0";

	private static WhatsmeowSidecarOptions Options(StandaloneSidecar standalone, string token, string consumer = "client") =>
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

	/// <summary>
	/// A TCP relay to the sidecar that can go silent: frozen, it keeps every connection open and forwards nothing, as a
	/// network that lost the peer's host does. It counts the keepalive pings the sidecar answers, from the HTTP/2 frames it
	/// sends.
	/// </summary>
	private sealed class Relay : IAsyncDisposable
	{
		private const byte AckFlag = 0x1;

		private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
		private readonly IPEndPoint _target;
		private readonly CancellationTokenSource _stopping = new();
		private readonly List<Task> _pumps = [];
		private readonly Channel<int> _pingsAnswered = Channel.CreateUnbounded<int>();
		private readonly Task _accepting;
		private volatile TaskCompletionSource _open = Opened();
		private int _pings;
		private int _connections;

		private Relay(IPEndPoint target)
		{
			_target = target;
			_listener.Start();
			_accepting = AcceptAsync();
		}

		public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

		/// <summary>How many connections the client opened through the relay.</summary>
		public int Connections => Volatile.Read(ref _connections);

		public static Relay Start(IPEndPoint target) => new(target);

		public void Freeze() => _open = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

		public void Thaw() => _open.TrySetResult();

		/// <summary>Waits until the sidecar answered <paramref name="count"/> pings.</summary>
		public async Task PingsAnsweredAsync(int count, CancellationToken cancellationToken)
		{
			while (await _pingsAnswered.Reader.ReadAsync(cancellationToken).ConfigureAwait(false) < count)
			{
				// The next answer.
			}
		}

		public async ValueTask DisposeAsync()
		{
			await _stopping.CancelAsync().ConfigureAwait(false);
			_listener.Stop();
			_listener.Dispose();
			Thaw();
			try
			{
				await _accepting.ConfigureAwait(false);
			}
			catch (Exception e) when (e is OperationCanceledException or SocketException or ObjectDisposedException)
			{
				// Stopped.
			}

			Task[] pumps;
			lock (_pumps)
				pumps = [.. _pumps];
			await Task.WhenAll(pumps).ConfigureAwait(false);
			_stopping.Dispose();
		}

		private static TaskCompletionSource Opened()
		{
			var open = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
			open.SetResult();
			return open;
		}

		private async Task AcceptAsync()
		{
			while (true)
			{
				var client = await _listener.AcceptTcpClientAsync(_stopping.Token).ConfigureAwait(false);
				_ = Interlocked.Increment(ref _connections);
				lock (_pumps)
					_pumps.Add(PairAsync(client));
			}
		}

		private async Task PairAsync(TcpClient client)
		{
			using (client)
			using (var server = new TcpClient())
			{
				await server.ConnectAsync(_target, _stopping.Token).ConfigureAwait(false);
				_ = await Task.WhenAny(PumpAsync(client.GetStream(), server.GetStream(), countPings: false), PumpAsync(server.GetStream(), client.GetStream(), countPings: true)).ConfigureAwait(false);
			}
		}

		private async Task PumpAsync(NetworkStream from, NetworkStream to, bool countPings)
		{
			var buffer = new byte[64 * 1024];
			var frames = new FrameReader();
			try
			{
				while (true)
				{
					var read = await from.ReadAsync(buffer, _stopping.Token).ConfigureAwait(false);
					if (read == 0)
						return;
					// Frozen, the bytes are held, never sent: the peer looks gone, its connection still open.
					await _open.Task.WaitAsync(_stopping.Token).ConfigureAwait(false);
					await to.WriteAsync(buffer.AsMemory(0, read), _stopping.Token).ConfigureAwait(false);
					// .NET's keepalive pings carry a positive payload, its RTT estimation's a negative one
					// (System.Net.Http Http2Connection), which comes with data and is not what is under test.
					if (countPings)
						frames.Read(buffer.AsSpan(0, read), (flags, payload) =>
						{
							if ((flags & AckFlag) != 0 && payload > 0)
								_ = _pingsAnswered.Writer.TryWrite(Interlocked.Increment(ref _pings));
						});
				}
			}
			catch (Exception e) when (e is IOException or OperationCanceledException or ObjectDisposedException)
			{
				// One side closed, or the relay stopped.
			}
		}
	}

	/// <summary>
	/// Finds the HTTP/2 frames in the server's byte stream, a 9-byte header each, its first 3 bytes the payload length,
	/// and reports each PING with its flags and 8-byte payload.
	/// </summary>
	private sealed class FrameReader
	{
		private const byte PingFrame = 0x6;

		private readonly byte[] _header = new byte[9];
		private readonly byte[] _ping = new byte[8];
		private int _headerRead;
		private int _payloadLeft;
		private int _pingRead = -1;

		public void Read(ReadOnlySpan<byte> bytes, Action<byte, long> ping)
		{
			while (!bytes.IsEmpty)
			{
				if (_payloadLeft > 0)
				{
					var skip = Math.Min(_payloadLeft, bytes.Length);
					if (_pingRead >= 0)
					{
						bytes[..skip].CopyTo(_ping.AsSpan(_pingRead));
						_pingRead += skip;
					}

					_payloadLeft -= skip;
					bytes = bytes[skip..];
					if (_payloadLeft == 0 && _pingRead == _ping.Length)
						ping(_header[4], System.Buffers.Binary.BinaryPrimitives.ReadInt64BigEndian(_ping));
					continue;
				}

				var take = Math.Min(_header.Length - _headerRead, bytes.Length);
				bytes[..take].CopyTo(_header.AsSpan(_headerRead));
				_headerRead += take;
				bytes = bytes[take..];
				if (_headerRead < _header.Length)
					return;

				_headerRead = 0;
				_payloadLeft = (_header[0] << 16) | (_header[1] << 8) | _header[2];
				_pingRead = _header[3] == PingFrame && _payloadLeft == _ping.Length ? 0 : -1;
			}
		}
	}
}
