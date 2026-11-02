using System.Net;

namespace Whatsmeow.Core.Transport;

/// <summary>
/// Ends a request body only once no write is in flight on it (#71): a workaround for grpc-dotnet ending a client
/// stream's request content under a write, which SocketsHttpHandler does not survive.
/// </summary>
/// <remarks>
/// <para>
/// When a call ends, grpc-dotnet cancels the task its request content waits on without waiting for the write in flight
/// on the content's stream (<c>CompleteTcs</c>, in <c>GrpcCall.Cleanup</c> and <c>CancelCall</c>):
/// https://github.com/grpc/grpc-dotnet/blob/v2.85.0/src/Grpc.Net.Client/Internal/GrpcCall.cs#L227
/// https://github.com/grpc/grpc-dotnet/blob/v2.85.0/src/Grpc.Net.Client/Internal/GrpcCall.cs#L439
/// SocketsHttpHandler then completes the HTTP/2 stream under that write. <c>Complete</c> clears <c>_creditWaiter</c>
/// under the credit lock, and <c>SendDataAsync</c>, out of flow-control credit, reads it again after leaving that lock,
/// so the write throws a <see cref="NullReferenceException"/>; a write that takes the lock after <c>Complete</c> waits
/// on a new waiter nothing completes:
/// https://github.com/dotnet/runtime/blob/v10.0.14/src/libraries/System.Net.Http/src/System/Net/Http/SocketsHttpHandler/Http2Stream.cs#L1294-L1312
/// https://github.com/dotnet/runtime/blob/v10.0.14/src/libraries/System.Net.Http/src/System/Net/Http/SocketsHttpHandler/Http2Stream.cs#L391-L399
/// </para>
/// <para>
/// The fence holds the content open until the write in flight returned, cancelling it first so it cannot wait for
/// credit forever, and refuses the writes after it. A sidecar refusing an upload mid-stream ends the call this way.
/// Remove it once grpc-dotnet waits for the write in flight before it ends the content.
/// </para>
/// </remarks>
internal sealed class RequestBodyFence : DelegatingHandler
{
	protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
	{
		if (request.Content is { } content)
			request.Content = new FencedContent(content);
		return base.SendAsync(request, cancellationToken);
	}

	/// <summary>The inner content, written through a <see cref="FencedStream"/> that is closed before it ends.</summary>
	private sealed class FencedContent : HttpContent
	{
		private readonly HttpContent _inner;

		public FencedContent(HttpContent inner)
		{
			_inner = inner;
			foreach (var (name, values) in inner.Headers)
				_ = Headers.TryAddWithoutValidation(name, values);
		}

		protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
			SerializeToStreamAsync(stream, context, CancellationToken.None);

		protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken cancellationToken)
		{
			var fenced = new FencedStream(stream);
			await using (fenced.ConfigureAwait(false))
				await _inner.CopyToAsync(fenced, context, cancellationToken).ConfigureAwait(false);
		}

		protected override bool TryComputeLength(out long length)
		{
			var known = _inner.Headers.ContentLength;
			length = known.GetValueOrDefault();
			return known.HasValue;
		}

		protected override void Dispose(bool disposing)
		{
			if (disposing)
				_inner.Dispose();
			base.Dispose(disposing);
		}
	}

	/// <summary>
	/// Writes to <paramref name="inner"/> one at a time, as any stream does. Disposing it refuses later writes, and cancels
	/// the write in flight and waits for it; without one it returns at once, so a completed body still ends inline.
	/// </summary>
	private sealed class FencedStream(Stream inner) : Stream
	{
		private readonly Lock _gate = new();
		private readonly CancellationTokenSource _closing = new();
		private TaskCompletionSource? _writing;
		private bool _closed;

		public override bool CanRead => false;
		public override bool CanSeek => false;
		public override bool CanWrite => true;
		public override long Length => throw new NotSupportedException();

		public override long Position
		{
			get => throw new NotSupportedException();
			set => throw new NotSupportedException();
		}

		public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
		{
			var writing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
			lock (_gate)
			{
				ObjectDisposedException.ThrowIf(_closed, this);
				if (_writing is not null)
					throw new InvalidOperationException("A write is already in flight on the request body.");
				_writing = writing;
			}

			try
			{
				using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _closing.Token);
				await inner.WriteAsync(buffer, linked.Token).ConfigureAwait(false);
			}
			finally
			{
				lock (_gate)
					_writing = null;
				writing.SetResult();
			}
		}

		public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
			WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

		public override Task FlushAsync(CancellationToken cancellationToken) => inner.FlushAsync(cancellationToken);

		public override async ValueTask DisposeAsync()
		{
			TaskCompletionSource? writing;
			lock (_gate)
			{
				_closed = true;
				writing = _writing;
			}

			if (writing is not null)
			{
				await _closing.CancelAsync().ConfigureAwait(false);
				await writing.Task.ConfigureAwait(false);
			}

			await base.DisposeAsync().ConfigureAwait(false);
		}

		protected override void Dispose(bool disposing)
		{
			if (disposing)
				_closing.Dispose();
			base.Dispose(disposing);
		}

		public override void Flush() => inner.Flush();

		public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

		public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

		public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

		public override void SetLength(long value) => throw new NotSupportedException();
	}
}
