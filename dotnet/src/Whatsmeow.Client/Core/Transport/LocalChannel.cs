using System.IO.Pipes;
using System.Net;
using System.Net.Sockets;
using Grpc.Core;
using Grpc.Core.Interceptors;
using Grpc.Net.Client;
using Whatsmeow.Sidecar;

namespace Whatsmeow.Core.Transport;

/// <summary>
/// One launch's gRPC channel to the sidecar, over a Unix domain socket or a Windows named pipe, or to an external
/// sidecar also over TCP (ARCHITECTURE §8.6). Its endpoint and token are fixed: every embedded launch listens at an
/// endpoint of its own, so this channel never reaches another launch, and every call it makes carries its launch's
/// token (#85, ADR 0045). An external sidecar keeps its address, so there a launch is a connect epoch with the token
/// its file held then (ADR 0046).
/// </summary>
internal sealed class LocalChannel : IDisposable
{
	private const int MaxMessageSize = 16 << 20;
	private const string PipePrefix = @"\\.\pipe\";

	/// <summary>
	/// How long a pipe or TCP connect waits, one rule for both. A launch's pipe exists from before its ready line until it
	/// exits, so one that does not accept within this is gone, and its name never comes back; go-winio's own
	/// <c>DialPipe</c> waits as long by default (v0.6.4 pipe.go:235-236). A TCP connect to a partitioned peer would
	/// otherwise wait for the kernel's SYN retries, about 127 s on Linux [inferred: tcp_syn_retries 6], while an external
	/// sidecar that is up answers a connect at once (ADR 0046).
	/// </summary>
	private static readonly TimeSpan s_connectTimeout = TimeSpan.FromSeconds(2);

	private readonly GrpcChannel _channel;
	private readonly CancellationTokenSource? _refused;

	/// <param name="namedPipe">Connect to a named pipe instead of a Unix domain socket.</param>
	/// <param name="endpoint">The launch's socket path or pipe name; null for a channel that never connects.</param>
	/// <param name="token">The launch's token.</param>
	/// <param name="loggerFactory">For gRPC's own logs.</param>
	/// <param name="timeProvider">Bounds a pipe connect.</param>
	public LocalChannel(bool namedPipe, string? endpoint, string token, ILoggerFactory loggerFactory, TimeProvider timeProvider)
		: this(namedPipe ? ct => ConnectPipeAsync(endpoint, timeProvider, ct) : ct => ConnectSocketAsync(endpoint, ct), token, keepAlive: null, refused: null, loggerFactory)
	{
	}

	/// <param name="connect">Opens a new connection to the sidecar.</param>
	/// <param name="token">The launch's token.</param>
	/// <param name="keepAlive">
	/// For an external sidecar: ping a connection silent this long, and drop it when the answer takes as long, so a peer
	/// that vanished without closing its end fails the calls on it. Null for none: a local child's socket closes with it.
	/// </param>
	/// <param name="refused">Cancelled when a call is refused <c>Unauthenticated</c>, for an external sidecar's epoch; null for none.</param>
	/// <param name="loggerFactory">For gRPC's own logs.</param>
	private LocalChannel(Func<CancellationToken, ValueTask<Stream>> connect, string token, TimeSpan? keepAlive, CancellationTokenSource? refused, ILoggerFactory loggerFactory)
	{
		_refused = refused;
		// No proxy: this channel only reaches its sidecar, and a proxy from the environment (HTTP_PROXY, ALL_PROXY) made
		// SocketsHttpHandler send the call as HTTP/1.1, which fails every call (SidecarTests).
		var sockets = new SocketsHttpHandler { ConnectCallback = (_, ct) => connect(ct), UseProxy = false };
		if (keepAlive is { } delay)
		{
			sockets.KeepAlivePingDelay = delay;
			sockets.KeepAlivePingTimeout = delay;
			sockets.KeepAlivePingPolicy = HttpKeepAlivePingPolicy.Always;
		}

		// Never call ConnectAsync or read State: both throw with a ConnectCallback (ARCHITECTURE §8.6).
		_channel = GrpcChannel.ForAddress("http://localhost", new GrpcChannelOptions
		{
			// Keeps a call that ends mid-upload from completing the HTTP/2 stream under a write (#71).
			HttpHandler = new RequestBodyFence { InnerHandler = sockets },
			DisposeHttpClient = true,
			MaxReceiveMessageSize = MaxMessageSize,
			MaxSendMessageSize = MaxMessageSize,
			ThrowOperationCanceledOnCancellation = true,
			LoggerFactory = loggerFactory,
		});

		var authorization = "Bearer " + token;
		var invoker = _channel.Intercept(metadata =>
		{
			metadata.Add("authorization", authorization);
			return metadata;
		});
		CallInvoker = refused is null ? invoker : invoker.Intercept(new TokenRefusal(refused));
	}

	/// <summary>
	/// One connect epoch's channel to an external sidecar (ADR 0046): its address is the same for every epoch, its token
	/// the one the token file held when the epoch began, so a call never carries another epoch's token. A call it
	/// refuses <c>Unauthenticated</c> cancels <see cref="Refused"/>.
	/// </summary>
	public static LocalChannel ToExternal(SidecarAddress address, string token, TimeSpan keepAlive, ILoggerFactory loggerFactory, TimeProvider timeProvider) =>
		new(
			address.Path is { } path ? ct => ConnectSocketAsync(path, ct) : ct => ConnectTcpAsync(address.Host!, address.Port, timeProvider, ct),
			token,
			keepAlive,
			new CancellationTokenSource(),
			loggerFactory
		);

	/// <summary>This launch's calls go through this invoker, which adds its token.</summary>
	public CallInvoker CallInvoker { get; }

	/// <summary>
	/// Cancelled once the external sidecar refused one of this channel's calls <c>Unauthenticated</c>: it no longer takes
	/// this epoch's token. Never cancelled for an embedded launch, whose token never changes.
	/// </summary>
	public CancellationToken Refused => _refused?.Token ?? CancellationToken.None;

	public void Dispose()
	{
		_channel.Dispose();
		_refused?.Dispose();
	}

	private static async ValueTask<Stream> ConnectSocketAsync(string? path, CancellationToken cancellationToken)
	{
		if (path is null)
			NotRunning();

		Socket? socket = new(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
		try
		{
			await socket.ConnectAsync(new UnixDomainSocketEndPoint(path), cancellationToken).ConfigureAwait(false);
			var stream = new NetworkStream(socket, ownsSocket: true);
			socket = null;
			return stream;
		}
		finally
		{
			socket?.Dispose();
		}
	}

	/// <summary>
	/// Connects to the pipe within <see cref="s_connectTimeout"/>. Without a bound, a connect to a launch that is gone
	/// would wait forever: a missing pipe makes <see cref="NamedPipeClientStream"/> try again until its timeout, infinite
	/// by default (.NET 10.0.14 NamedPipeClientStream.cs:131-162, NamedPipeClientStream.Windows.cs:113-129).
	/// </summary>
	private static async ValueTask<Stream> ConnectPipeAsync(string? name, TimeProvider timeProvider, CancellationToken cancellationToken)
	{
		if (name is null)
			NotRunning();

		if (!name.StartsWith(PipePrefix, StringComparison.Ordinal))
			throw new IOException($"The sidecar's pipe name {name} does not start with {PipePrefix}.");

		var pipe = new NamedPipeClientStream(".", name[PipePrefix.Length..], PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
		using var timeout = new CancellationTokenSource(s_connectTimeout, timeProvider);
		using var connect = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
		try
		{
			await pipe.ConnectAsync(connect.Token).ConfigureAwait(false);
			return pipe;
		}
		catch (OperationCanceledException e) when (timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
		{
			await pipe.DisposeAsync().ConfigureAwait(false);

			// An IOException, so gRPC reports the call Unavailable; a TimeoutException would be Internal (grpc-dotnet
			// v2.85.0 GrpcProtocolHelpers.cs:450-462). The token is checked every 50 ms (NamedPipeClientStream.cs:137-145).
			throw new IOException($"The sidecar's pipe {name} accepted no connection within {s_connectTimeout}; that sidecar is gone.", e);
		}
		catch
		{
			await pipe.DisposeAsync().ConfigureAwait(false);
			throw;
		}
	}

	/// <summary>
	/// Connects to an external sidecar over TCP within <see cref="s_connectTimeout"/>, without Nagle's delay: gRPC writes
	/// whole frames.
	/// </summary>
	private static async ValueTask<Stream> ConnectTcpAsync(string host, int port, TimeProvider timeProvider, CancellationToken cancellationToken)
	{
		Socket? socket = new(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
		using var timeout = new CancellationTokenSource(s_connectTimeout, timeProvider);
		using var connect = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
		try
		{
			await socket.ConnectAsync(new DnsEndPoint(host, port), connect.Token).ConfigureAwait(false);
			var stream = new NetworkStream(socket, ownsSocket: true);
			socket = null;
			return stream;
		}
		catch (OperationCanceledException e) when (timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
		{
			// An IOException, so gRPC reports the call Unavailable, as for a pipe (ConnectPipeAsync).
			throw new IOException($"The sidecar at {host}:{port} accepted no connection within {s_connectTimeout}.", e);
		}
		finally
		{
			socket?.Dispose();
		}
	}

	[DoesNotReturn]
	private static void NotRunning() => throw new IOException("No sidecar is running right now.");
}
