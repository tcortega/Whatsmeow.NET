using System.Buffers;
using Google.Protobuf;
using Grpc.Core;
using Whatsmeow.Core.Errors;

namespace Whatsmeow.Core.Transport;

/// <summary>
/// A client stream that sends a header, then a stream's content in 64 KiB chunks, never as one message (ARCHITECTURE
/// invariant 8), and maps the call's failure: the media upload's (media.proto) and a group photo's (group.proto, ADR
/// 0070). One rented 64 KiB buffer is in use at a time, so memory does not grow with the content.
/// </summary>
internal static class ChunkedCall
{
	/// <summary>The most bytes one chunk carries (media.proto, group.proto), below the large object heap's 85,000 bytes.</summary>
	private const int ChunkSize = 64 * 1024;

	/// <summary>
	/// Sends <paramref name="header"/>, then what <paramref name="content"/> holds from its position to its end as
	/// <paramref name="chunk"/> messages, ends the stream, and returns the response. The content is read, not disposed.
	/// </summary>
	public static async Task<TResponse> SendAsync<TRequest, TResponse>(
		AsyncClientStreamingCall<TRequest, TResponse> call,
		TRequest header,
		Stream? content,
		Func<ByteString, TRequest> chunk,
		CancellationToken cancellationToken
	)
	{
		using (call)
		{
			try
			{
				await WriteAsync(call.RequestStream, header, content, chunk, cancellationToken).ConfigureAwait(false);
			}
			catch (Exception e) when (e is RpcException or InvalidOperationException or OperationCanceledException && !cancellationToken.IsCancellationRequested)
			{
				// The sidecar ended the call while chunks were still going out, which cuts the write short. Once the call
				// has completed, grpc-dotnet throws its status as an RpcException
				// (StreamExtensions.TryCreateCallCompleteException); the other two are for a write that fails before that.
				// Either way the status says why.
				_ = await ResponseAsync(call, cancellationToken).ConfigureAwait(false);
				throw;
			}

			return await ResponseAsync(call, cancellationToken).ConfigureAwait(false);
		}
	}

	/// <summary>Sends the header, then the content in chunks of one rented buffer, reused once each write returned.</summary>
	private static async Task WriteAsync<TRequest>(
		IClientStreamWriter<TRequest> requests,
		TRequest header,
		Stream? content,
		Func<ByteString, TRequest> chunk,
		CancellationToken cancellationToken
	)
	{
		await requests.WriteAsync(header, cancellationToken).ConfigureAwait(false);
		if (content is not null)
		{
			var buffer = ArrayPool<byte>.Shared.Rent(ChunkSize);
			try
			{
				int read;
				while ((read = await content.ReadAtLeastAsync(buffer.AsMemory(0, ChunkSize), ChunkSize, throwOnEndOfStream: false, cancellationToken).ConfigureAwait(false)) > 0)
				{
					// The write serializes the message before it returns, so the buffer is free again after it.
					await requests.WriteAsync(chunk(UnsafeByteOperations.UnsafeWrap(buffer.AsMemory(0, read))), cancellationToken).ConfigureAwait(false);
				}
			}
			finally
			{
				ArrayPool<byte>.Shared.Return(buffer);
			}
		}

		await requests.CompleteAsync().ConfigureAwait(false);
	}

	/// <summary>
	/// Awaits the response and maps the call's failure. A write the call's end cut short comes here too, so a call the
	/// sidecar's crash or stop cut off is mapped here whether a write or the response saw it first.
	/// </summary>
	private static async Task<TResponse> ResponseAsync<TRequest, TResponse>(AsyncClientStreamingCall<TRequest, TResponse> call, CancellationToken cancellationToken)
	{
		try
		{
			return await call.ResponseAsync.ConfigureAwait(false);
		}
		catch (RpcException e)
		{
			RpcErrors.Throw(e);
			throw;
		}
		catch (OperationCanceledException e) when (!cancellationToken.IsCancellationRequested)
		{
			SidecarUnavailableException.ThrowCallCut(e);
			throw;
		}
	}
}
