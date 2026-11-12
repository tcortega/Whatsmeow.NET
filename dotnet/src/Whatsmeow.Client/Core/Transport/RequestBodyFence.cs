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
/// https://github.com/dotnet/runtime/blob/v11.0.0/src/libraries/System.Net.Http/src/System/Net/Http/SocketsHttpHandler/Http2Stream.cs#L1294-L1312
/// https://github.com/dotnet/runtime/blob/v11.0.0/src/libraries/System.Net.Http/src/System/Net/Http/SocketsHttpHandler/Http2Stream.cs#L391-L399
/// </para>
/// <para>
/// The fence holds the content open until the write in flight returned, cancelling it first so it cannot wait for
/// credit forever, and refuses the writes after it. A sidecar refusing an upload mid-stream ends the call this way.
/// The cut-short write still surfaces as the call's status: grpc-dotnet turns the write's cancellation or
/// <see cref="ObjectDisposedException"/> into the status <c>RpcException</c> once the call completed
/// (<c>StreamExtensions.TryCreateCallCompleteException</c>).
/// </para>
/// <para>
/// The fence reaches the inner content through the public
/// <see cref="HttpContent.CopyToAsync(Stream, TransportContext?, CancellationToken)"/>, which wraps an
/// <see cref="IOException"/> or <see cref="ObjectDisposedException"/> the content throws in an
/// <see cref="HttpRequestException"/>, as SocketsHttpHandler's own copy of the body does, so a failed body is wrapped
/// here instead of there. The call's status code is unchanged; only the message text may differ.
/// </para>
/// <para>
/// Remove it once either is fixed: grpc-dotnet waiting for the write in flight before it ends the content, or
/// SocketsHttpHandler reading <c>_creditWaiter</c> under the credit lock and failing a write on a completed stream
/// instead of waiting for credit.
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
	/// Writes to <paramref name="inner"/> one at a time, as any stream does. Closing it refuses later writes and cancels
	/// the write in flight; <see cref="DisposeAsync"/> also waits for that write, and without one it finishes inline, so
	/// a completed body still ends before <c>CompleteAsync</c> returns (grpc-dotnet #1394). The sync
	/// <see cref="Stream.Dispose()"/> cannot wait without blocking, so it only refuses and cancels.
	/// </summary>
	internal sealed class FencedStream(Stream inner) : Stream
	{
		private readonly Lock _gate = new();
		private readonly CancellationTokenSource _closing = new();
		private TaskCompletionSource? _writing;
		private bool _closed;

		public override bool CanRead => false;
		public override bool CanSeek => false;

		public override bool CanWrite
		{
			get
			{
				lock (_gate)
					return !_closed;
			}
		}

		public override long Length => throw new NotSupportedException();

		public override long Position
		{
			get => throw new NotSupportedException();
			set => throw new NotSupportedException();
		}

		public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
		{
			var writing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
			CancellationTokenSource linked;
			lock (_gate)
			{
				ObjectDisposedException.ThrowIf(_closed, this);
				if (_writing is not null)
					throw new InvalidOperationException("A write is already in flight on the request body.");
				_writing = writing;
				linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _closing.Token);
			}

			try
			{
				await inner.WriteAsync(buffer, linked.Token).ConfigureAwait(false);
			}
			finally
			{
				linked.Dispose();
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
			try
			{
				if (Refuse() is { } writing)
				{
					// A cancel callback that throws must not skip the wait: the write would outlive the body again.
					try
					{
						CancelInFlight();
					}
					finally
					{
						await writing.Task.ConfigureAwait(false);
					}
				}
			}
			finally
			{
				await base.DisposeAsync().ConfigureAwait(false);
			}
		}

		protected override void Dispose(bool disposing)
		{
			if (disposing)
			{
				try
				{
					if (Refuse() is not null)
						CancelInFlight();
				}
				finally
				{
					_closing.Dispose();
				}
			}

			base.Dispose(disposing);
		}

		/// <summary>Refuses later writes; returns the write in flight, if any.</summary>
		private TaskCompletionSource? Refuse()
		{
			lock (_gate)
			{
				_closed = true;
				return _writing;
			}
		}

		/// <summary>
		/// Cancels the write in flight before <see cref="DisposeAsync"/> first yields, with the callbacks inline, so a
		/// callback's exception surfaces there, after the wait.
		/// </summary>
		private void CancelInFlight() => _closing.Cancel();

		public override void Flush() => inner.Flush();

		public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

		public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

		public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

		public override void SetLength(long value) => throw new NotSupportedException();
	}
}
