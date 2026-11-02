using System.Net;
using System.Net.Sockets;
using System.Threading.Channels;
using Whatsmeow.IntegrationTests.Support;

namespace Whatsmeow.IntegrationTests.Sidecar;

/// <summary>
/// External mode's keepalive over TCP (ADR 0046), on real time: the pings are SocketsHttpHandler's, which no fake clock
/// drives. Never in parallel with another test, so a loaded machine does not delay a ping answer past the timeout.
/// </summary>
[Collection(nameof(KeepaliveTiming))]
public sealed class ExternalKeepaliveTests(SidecarBinaries binaries)
{
	private const int Timeout = 120_000;

	[Fact(Timeout = Timeout)]
	public async Task Tcp_ThePeerGoesSilent_TheClientNoticesAndConnectsAgain()
	{
		var ct = TestContext.Current.CancellationToken;
		using var logs = new LogCapture();
		var token = StandaloneSidecar.NewTokenFile(binaries.NewDirectory());
		await using var standalone = await StandaloneSidecar.StartAsync(binaries.Sidecar, binaries.NewDatabase(), "tcp:127.0.0.1:0", token, [], ct);
		await using var relay = Relay.Start(IPEndPoint.Parse(standalone.Listen));
		var options = ExternalTests.Options(standalone, token);
		options.External!.Address = $"tcp:127.0.0.1:{relay.Port}";
		options.External.KeepAlive = TimeSpan.FromSeconds(3);
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

/// <summary>The tests whose margins are real time: never in parallel with another test.</summary>
[CollectionDefinition(nameof(KeepaliveTiming), DisableParallelization = true)]
public sealed class KeepaliveTiming;
