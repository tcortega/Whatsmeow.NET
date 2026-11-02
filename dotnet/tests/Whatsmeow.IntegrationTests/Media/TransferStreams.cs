using System.Diagnostics;
using System.Security.Cryptography;

namespace Whatsmeow.IntegrationTests.Media;

/// <summary>
/// The peak growth of this process's live managed heap and of the sidecar's working set, sampled as the bytes move.
/// The managed heap is read after a full collection, so garbage the collector has not reached yet never counts: what
/// grows is what is held.
/// </summary>
internal sealed class MemorySampler
{
	/// <summary>How many reads or writes between samples: 256 chunks of 64 KiB, every 16 MiB.</summary>
	private const int Every = 256;

	private readonly Process _sidecar;
	private readonly long _managedBaseline;
	private readonly long _sidecarBaseline;
	private int _calls;

	public MemorySampler(Process sidecar)
		: this(sidecar, GC.GetTotalMemory(forceFullCollection: true), WorkingSet(sidecar)) { }

	private MemorySampler(Process sidecar, long managedBaseline, long sidecarBaseline)
	{
		_sidecar = sidecar;
		_managedBaseline = managedBaseline;
		_sidecarBaseline = sidecarBaseline;
	}

	public long ManagedGrowth { get; private set; }

	public long SidecarGrowth { get; private set; }

	/// <summary>A sampler for the next phase, with its own peaks from this one's baselines.</summary>
	public MemorySampler Next() => new(_sidecar, _managedBaseline, _sidecarBaseline);

	public void Sample()
	{
		if (++_calls % Every != 0)
			return;
		ManagedGrowth = Math.Max(ManagedGrowth, GC.GetTotalMemory(forceFullCollection: true) - _managedBaseline);
		SidecarGrowth = Math.Max(SidecarGrowth, WorkingSet(_sidecar) - _sidecarBaseline);
	}

	private static long WorkingSet(Process process)
	{
		process.Refresh();
		return process.WorkingSet64;
	}
}

/// <summary>A read-only stream of <c>length</c> pseudo-random bytes, made as they are read and hashed on the way.</summary>
internal sealed class GeneratedStream(long length, MemorySampler? memory) : Stream
{
	private readonly IncrementalHash _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
	private long _position;
	private ulong _state = 0x9E3779B97F4A7C15;

	public override bool CanRead => true;

	public override bool CanSeek => false;

	public override bool CanWrite => false;

	public override long Length => length;

	public override long Position
	{
		get => _position;
		set => throw new NotSupportedException();
	}

	public byte[] Hash() => _hash.GetHashAndReset();

	public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

	public override int Read(Span<byte> buffer)
	{
		var n = (int)Math.Min(buffer.Length, length - _position);
		for (var i = 0; i < n; i++)
		{
			// xorshift64: cheap, and no two 64 KiB chunks alike.
			_state ^= _state << 13;
			_state ^= _state >> 7;
			_state ^= _state << 17;
			buffer[i] = (byte)_state;
		}

		_hash.AppendData(buffer[..n]);
		_position += n;
		memory?.Sample();
		return n;
	}

	public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => ValueTask.FromResult(Read(buffer.Span));

	public override void Flush() { }

	public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

	public override void SetLength(long value) => throw new NotSupportedException();

	public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

	protected override void Dispose(bool disposing)
	{
		if (disposing)
			_hash.Dispose();
		base.Dispose(disposing);
	}
}

/// <summary>A write-only stream that keeps only the SHA-256 and the length of what was written.</summary>
internal sealed class HashingStream(MemorySampler? memory) : Stream
{
	private readonly IncrementalHash _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
	private long _length;

	public override bool CanRead => false;

	public override bool CanSeek => false;

	public override bool CanWrite => true;

	public override long Length => _length;

	public override long Position
	{
		get => _length;
		set => throw new NotSupportedException();
	}

	public byte[] Hash() => _hash.GetHashAndReset();

	public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

	public override void Write(ReadOnlySpan<byte> buffer)
	{
		_hash.AppendData(buffer);
		_length += buffer.Length;
		memory?.Sample();
	}

	public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
	{
		Write(buffer.Span);
		return ValueTask.CompletedTask;
	}

	public override void Flush() { }

	public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

	public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

	public override void SetLength(long value) => throw new NotSupportedException();

	protected override void Dispose(bool disposing)
	{
		if (disposing)
			_hash.Dispose();
		base.Dispose(disposing);
	}
}
