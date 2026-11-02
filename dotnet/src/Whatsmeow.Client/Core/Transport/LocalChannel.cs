using System.IO.Pipes;
using System.Net.Sockets;
using Grpc.Core;
using Grpc.Core.Interceptors;
using Grpc.Net.Client;

namespace Whatsmeow.Core.Transport;

/// <summary>
/// The one gRPC channel to the sidecar, over a Unix domain socket or a Windows named pipe (ARCHITECTURE §8.6).
/// The endpoint and the token are read on every connection and call, so the channel outlives sidecar restarts.
/// </summary>
internal sealed class LocalChannel : IDisposable
{
	private const int MaxMessageSize = 16 << 20;
	private const string PipePrefix = @"\\.\pipe\";

	private readonly GrpcChannel _channel;

	/// <param name="namedPipe">Connect to a named pipe instead of a Unix domain socket.</param>
	/// <param name="endpoint">The current socket path or pipe name; null while no sidecar is ready.</param>
	/// <param name="token">The current sidecar's token.</param>
	/// <param name="loggerFactory">For gRPC's own logs.</param>
	public LocalChannel(bool namedPipe, Func<string?> endpoint, Func<string> token, ILoggerFactory loggerFactory)
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
						? (_, ct) => ConnectPipeAsync(endpoint(), ct)
						: (_, ct) => ConnectSocketAsync(endpoint(), ct),
				},
			},
			DisposeHttpClient = true,
			MaxReceiveMessageSize = MaxMessageSize,
			MaxSendMessageSize = MaxMessageSize,
			ThrowOperationCanceledOnCancellation = true,
			LoggerFactory = loggerFactory,
		});

		CallInvoker = _channel.Intercept(metadata =>
		{
			metadata.Add("authorization", "Bearer " + token());
			return metadata;
		});
	}

	/// <summary>Every call goes through this invoker, which adds the token.</summary>
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
