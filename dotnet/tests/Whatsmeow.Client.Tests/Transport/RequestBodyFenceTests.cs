using Whatsmeow.Core.Transport;

namespace Whatsmeow.Client.Tests.Transport;

/// <summary>
/// The request body fence's contract (#71): closing it cancels the write in flight and waits for it, refuses the writes
/// after it, and finishes inline when no write is in flight. Each write blocks on the test, so every step is ordered.
/// </summary>
public sealed class RequestBodyFenceTests
{
	[Fact(Timeout = 10_000)]
	public async Task Close_CancelsTheWriteInFlightAndWaitsForIt()
	{
		var ct = TestContext.Current.CancellationToken;
		var inner = new BlockingStream();
		var fenced = new RequestBodyFence.FencedStream(inner);
		var write = fenced.WriteAsync(new byte[] { 1 }, ct).AsTask();
		await inner.Started.Task.WaitAsync(ct);

		var close = fenced.DisposeAsync().AsTask();
		await inner.Cancelled.Task.WaitAsync(ct);
		Assert.False(close.IsCompleted);

		inner.Released.SetResult();
		await close;
		_ = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => write);
	}

	[Fact(Timeout = 10_000)]
	public async Task Close_WhenACancelCallbackFaults_StillWaitsForTheWrite()
	{
		var ct = TestContext.Current.CancellationToken;
		var inner = new BlockingStream { FaultOnCancel = true };
		var fenced = new RequestBodyFence.FencedStream(inner);
		var write = fenced.WriteAsync(new byte[] { 1 }, ct).AsTask();
		await inner.Started.Task.WaitAsync(ct);

		// The cancel runs its callbacks before the close first yields, so the fault has happened once it returned.
		var close = fenced.DisposeAsync().AsTask();
		Assert.True(inner.Cancelled.Task.IsCompleted);
		Assert.False(close.IsCompleted);

		inner.Released.SetResult();
		var fault = await Assert.ThrowsAnyAsync<AggregateException>(() => close);
		Assert.True(inner.Returned);
		Assert.Contains(fault.Flatten().InnerExceptions, e => e is InvalidOperationException { Message: BlockingStream.Fault });
		_ = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => write);
	}

	[Fact(Timeout = 10_000)]
	public async Task Write_AfterClose_ThrowsObjectDisposed()
	{
		var ct = TestContext.Current.CancellationToken;
		var inner = new BlockingStream();
		var fenced = new RequestBodyFence.FencedStream(inner);
		await fenced.DisposeAsync();

		Assert.False(fenced.CanWrite);
		_ = await Assert.ThrowsAsync<ObjectDisposedException>(() => fenced.WriteAsync(new byte[] { 1 }, ct).AsTask());
		Assert.Equal(0, inner.Writes);
	}

	[Fact]
	public void Close_WithNoWriteInFlight_FinishesSynchronously()
	{
		// A body that ended on CompleteAsync must still end inline, so END_STREAM goes out before CompleteAsync
		// returns (grpc-dotnet #1394).
		var fenced = new RequestBodyFence.FencedStream(new BlockingStream());

		Assert.True(FinishedInline(fenced.DisposeAsync()));
	}

	[Fact(Timeout = 10_000)]
	public async Task Dispose_CancelsTheWriteInFlightAndRefusesLaterWrites()
	{
		var ct = TestContext.Current.CancellationToken;
		var inner = new BlockingStream();
		var fenced = new RequestBodyFence.FencedStream(inner);
		var write = fenced.WriteAsync(new byte[] { 1 }, ct).AsTask();
		await inner.Started.Task.WaitAsync(ct);

		DisposeSynchronously(fenced);
		await inner.Cancelled.Task.WaitAsync(ct);

		Assert.False(fenced.CanWrite);
		_ = await Assert.ThrowsAsync<ObjectDisposedException>(() => fenced.WriteAsync(new byte[] { 2 }, ct).AsTask());
		inner.Released.SetResult();
		_ = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => write);
		Assert.Equal(1, inner.Writes);
	}

	private static bool FinishedInline(ValueTask close) => close.IsCompletedSuccessfully;

	/// <summary>The sync <see cref="Stream.Dispose()"/> under test, out of the async test method.</summary>
	private static void DisposeSynchronously(Stream stream) => stream.Dispose();

	/// <summary>
	/// A request stream whose write waits until it is cancelled, then until the test releases it, as an HTTP/2 write
	/// out of flow-control credit does.
	/// </summary>
	private sealed class BlockingStream : Stream
	{
		public const string Fault = "a cancel callback failed";

		private int _writes;
		private int _returned;

		public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
		public TaskCompletionSource Cancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
		public TaskCompletionSource Released { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

		/// <summary>The cancel callback throws.</summary>
		public bool FaultOnCancel { get; init; }

		public int Writes => Volatile.Read(ref _writes);

		/// <summary>Whether a write returned, before its caller saw it.</summary>
		public bool Returned => Volatile.Read(ref _returned) == 1;

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
			_ = Interlocked.Increment(ref _writes);
			try
			{
				using (cancellationToken.Register(OnCancel))
				{
					Started.SetResult();
					await Cancelled.Task.ConfigureAwait(false);
					await Released.Task.ConfigureAwait(false);
				}

				cancellationToken.ThrowIfCancellationRequested();
			}
			finally
			{
				Volatile.Write(ref _returned, 1);
			}
		}

		public override void Flush() { }

		public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

		public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

		public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

		public override void SetLength(long value) => throw new NotSupportedException();

		private void OnCancel()
		{
			_ = Cancelled.TrySetResult();
			if (FaultOnCancel)
				throw new InvalidOperationException(Fault);
		}
	}
}
