using System.IO.Pipes;
using System.Net.Sockets;
using Grpc.Core;
using Grpc.Core.Interceptors;
using Grpc.Net.Client;

namespace Whatsmeow.Core.Transport;

/// <summary>
/// One launch's gRPC channel to the sidecar, over a Unix domain socket or a Windows named pipe (ARCHITECTURE §8.6).
/// Its endpoint and token are fixed: every launch listens at an endpoint of its own, so this channel never reaches
/// another launch, and every call it makes carries its launch's token (#85, ADR 0045).
/// </summary>
internal sealed class LocalChannel : IDisposable
{
	private const int MaxMessageSize = 16 << 20;
	private const string PipePrefix = @"\\.\pipe\";

	private readonly GrpcChannel _channel;

	/// <param name="namedPipe">Connect to a named pipe instead of a Unix domain socket.</param>
	/// <param name="endpoint">The launch's socket path or pipe name; null for a channel that never connects.</param>
	/// <param name="token">The launch's token.</param>
	/// <param name="loggerFactory">For gRPC's own logs.</param>
	public LocalChannel(bool namedPipe, string? endpoint, string token, ILoggerFactory loggerFactory)
	{
		// Never call ConnectAsync or read State: both throw with a ConnectCallback (ARCHITECTURE §8.6).
		_channel = GrpcChannel.ForAddress("http://localhost", new GrpcChannelOptions
		{
			// Keeps a call that ends mid-upload from completing the HTTP/2 stream under a write (#71).
			HttpHandler = new RequestBodyFence
			{
				InnerHandler = new SocketsHttpHandler
				{
					ConnectCallback = namedPipe
						? (_, ct) => ConnectPipeAsync(endpoint, ct)
						: (_, ct) => ConnectSocketAsync(endpoint, ct),
				},
			},
			DisposeHttpClient = true,
			MaxReceiveMessageSize = MaxMessageSize,
			MaxSendMessageSize = MaxMessageSize,
			ThrowOperationCanceledOnCancellation = true,
			LoggerFactory = loggerFactory,
		});

		var authorization = "Bearer " + token;
		CallInvoker = _channel.Intercept(metadata =>
		{
			metadata.Add("authorization", authorization);
			return metadata;
		});
	}

	/// <summary>This launch's calls go through this invoker, which adds its token.</summary>
	public CallInvoker CallInvoker { get; }

	public void Dispose() => _channel.Dispose();

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

	private static async ValueTask<Stream> ConnectPipeAsync(string? name, CancellationToken cancellationToken)
	{
		if (name is null)
			NotRunning();

		if (!name.StartsWith(PipePrefix, StringComparison.Ordinal))
			throw new IOException($"The sidecar's pipe name {name} does not start with {PipePrefix}.");

		var pipe = new NamedPipeClientStream(".", name[PipePrefix.Length..], PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
		try
		{
			await pipe.ConnectAsync(cancellationToken).ConfigureAwait(false);
			return pipe;
		}
		catch
		{
			await pipe.DisposeAsync().ConfigureAwait(false);
			throw;
		}
	}

	[DoesNotReturn]
	private static void NotRunning() => throw new IOException("No sidecar is running right now.");
}
