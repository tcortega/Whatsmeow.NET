namespace Whatsmeow.IntegrationTests.Media;

/// <summary>A stream whose first read returns 64 KiB and whose second waits until its read is cancelled.</summary>
internal sealed class StallingStream : Stream
{
	private readonly TaskCompletionSource _stalled = new(TaskCreationOptions.RunContinuationsAsynchronously);
	private int _reads;

	public override bool CanRead => true;

	public override bool CanSeek => false;

	public override bool CanWrite => false;

	public override long Length => throw new NotSupportedException();

	public override long Position
	{
		get => throw new NotSupportedException();
		set => throw new NotSupportedException();
	}

	/// <summary>Completes once the second read waits: the first chunk went out, and only the read's token can end it.</summary>
	public Task StalledAsync(CancellationToken cancellationToken) => _stalled.Task.WaitAsync(cancellationToken);

	public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
	{
		if (Interlocked.Increment(ref _reads) == 1)
		{
			var n = Math.Min(buffer.Length, 64 * 1024);
			buffer.Span[..n].Fill(7);
			return n;
		}

		var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		await using (cancellationToken.Register(() => cancelled.TrySetCanceled(cancellationToken)).ConfigureAwait(false))
		{
			_ = _stalled.TrySetResult();
			await cancelled.Task.ConfigureAwait(false);
		}

		return 0;
	}

	public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

	public override void Flush() { }

	public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

	public override void SetLength(long value) => throw new NotSupportedException();

	public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
