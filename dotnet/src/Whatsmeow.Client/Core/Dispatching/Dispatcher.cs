using System.Threading.Channels;

namespace Whatsmeow.Core.Dispatching;

/// <summary>
/// Runs one session's events (ARCHITECTURE §8.4): one lane at a time in seq order, different lanes concurrently, and
/// acks by watermark, the highest seq at or below which every received event finished all its handlers (invariant 3).
/// The ack goes out on a timer and when the dispatcher stops. At most <see cref="MaxPending"/> events are received and
/// not finished; past that it stops reading <c>input</c>, which backs up into the reader, gRPC flow control and the
/// sidecar's outbox, so nothing is dropped. Events that finished behind an unfinished one keep only their seq, and a
/// watermark that does not move for <see cref="StuckAfter"/> while events wait is logged.
/// </summary>
/// <param name="input">The events, in seq order.</param>
/// <param name="ackAsync">Sends the ack; a <see cref="WhatsmeowException"/> from it is logged and retried on the next tick.</param>
internal sealed class Dispatcher(ChannelReader<Work> input, Func<ulong, CancellationToken, Task> ackAsync, TimeProvider timeProvider, ILogger logger)
	: IAsyncDisposable
{
	/// <summary>Events received and not finished, at most.</summary>
	public const int MaxPending = 64;

	/// <summary>Lanes running a handler at once, at most.</summary>
	public const int MaxConcurrentLanes = 16;

	/// <summary>How often the watermark is acked when it moved.</summary>
	public static TimeSpan AckInterval { get; } = TimeSpan.FromSeconds(1);

	/// <summary>How long the watermark may stay still while events wait before it is logged.</summary>
	public static TimeSpan StuckAfter { get; } = TimeSpan.FromMinutes(1);

	/// <summary>How long the last ack may take when the dispatcher stops.</summary>
	private static readonly TimeSpan s_finalAckTimeout = TimeSpan.FromSeconds(5);

	/// <summary>
	/// The event whose handlers run on this flow, so a stop from inside them does not wait for their own lane. It is
	/// the entry, not the lane: work a handler started and that outlives it carries the same value, and by then its lane
	/// runs another entry, which that stop waits for.
	/// </summary>
	private static readonly AsyncLocal<Entry?> s_current = new();

	// The semaphores and the token source are never disposed: a handler that stopped the dispatcher from inside its
	// lane still releases them after the stop returned. None holds an unmanaged resource while unused.
#pragma warning disable CA2213 // Justification: see above; disposing them would fail the lane of a handler that stopped the dispatcher
	private readonly SemaphoreSlim _pending = new(MaxPending);
	private readonly SemaphoreSlim _running = new(MaxConcurrentLanes);
	private readonly SemaphoreSlim _flushing = new(1);
	private readonly CancellationTokenSource _stopping = new();
#pragma warning restore CA2213
	private readonly Lock _lock = new();

	/// <summary>Every received event not yet under the watermark, in seq order.</summary>
	private readonly Queue<Entry> _order = new();

	private readonly Dictionary<string, Lane> _lanes = new(StringComparer.Ordinal);
	private readonly List<Lane> _started = [];
	private ulong _watermark;
	private ulong _acked;
	private DateTimeOffset _movedAt;
	private bool _warnedStuck;
	private Task _loop = Task.CompletedTask;
	private Task _ackLoop = Task.CompletedTask;
	private PeriodicTimer? _timer;
	private int _disposed;

	/// <summary>Starts reading <c>input</c> and the ack timer.</summary>
	public void Start()
	{
		if (_disposed != 0)
			return;

		// Created here, not in the loop, so a tick of the clock from now on is never missed.
		_timer = new PeriodicTimer(AckInterval, timeProvider);
		_loop = ReadAsync(_stopping.Token);
		_ackLoop = AckLoopAsync(_timer, _stopping.Token);
	}

	/// <summary>Acks the watermark if it moved since the last ack.</summary>
	public async Task FlushAsync(CancellationToken cancellationToken)
	{
		await _flushing.WaitAsync(cancellationToken).ConfigureAwait(false);
		try
		{
			ulong watermark;
			lock (_lock)
				watermark = _watermark;
			if (watermark <= _acked)
				return;

			try
			{
				await ackAsync(watermark, cancellationToken).ConfigureAwait(false);
				_acked = watermark;
			}
			catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
			{
				throw;
			}
#pragma warning disable CA1031 // Justification: whatever the ack call throws, the next tick and the stop try again; a dead ack loop would ack nothing more
			catch (Exception e)
#pragma warning restore CA1031
			{
				DispatchLog.AckFailed(logger, e, watermark);
			}
		}
		finally
		{
			_ = _flushing.Release();
		}
	}

	/// <summary>
	/// Stops: no more events are read, running handlers see their token cancelled and are waited for, and the
	/// watermark is acked. A handler that ended while the token was cancelled has not handled its event, which comes
	/// again. Called from inside a handler (it deletes its session or disposes the sidecar), it does not wait for that
	/// handler's lane, which goes on once the handler returns.
	/// </summary>
	public async ValueTask DisposeAsync()
	{
		if (Interlocked.Exchange(ref _disposed, 1) != 0)
			return;

		await _stopping.CancelAsync().ConfigureAwait(false);
		try
		{
			await _loop.ConfigureAwait(false);
			await _ackLoop.ConfigureAwait(false);
		}
		finally
		{
			await StopLanesAndFlushAsync().ConfigureAwait(false);
		}
	}

	private async Task StopLanesAndFlushAsync()
	{
		var own = s_current.Value;
		Task[] lanes;
		lock (_lock)
			lanes = [.. _started.Where(lane => own is null || lane.Current != own).Select(lane => lane.Task)];
		await Task.WhenAll(lanes).ConfigureAwait(false);

		using (var last = new CancellationTokenSource(s_finalAckTimeout, timeProvider))
		{
			try
			{
				await FlushAsync(last.Token).ConfigureAwait(false);
			}
			catch (OperationCanceledException)
			{
				DispatchLog.FinalAckTimedOut(logger, s_finalAckTimeout);
			}
		}

		_timer?.Dispose();
	}

	private async Task ReadAsync(CancellationToken cancellationToken)
	{
		try
		{
			while (true)
			{
				await _pending.WaitAsync(cancellationToken).ConfigureAwait(false);
				var work = await input.ReadAsync(cancellationToken).ConfigureAwait(false);
				Dispatch(new Entry(work), cancellationToken);
			}
		}
		catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
		{
			// Stopping.
		}
		catch (ChannelClosedException)
		{
			// The reader ended.
		}
	}

	private void Dispatch(Entry entry, CancellationToken cancellationToken)
	{
		lock (_lock)
		{
			if (_order.Count == 0)
				_movedAt = timeProvider.GetUtcNow();
			_order.Enqueue(entry);

			var key = entry.Work!.Lane;
			if (_lanes.TryGetValue(key, out var running))
			{
				running.Queue.Enqueue(entry);
				return;
			}

			var lane = new Lane(key);
			lane.Queue.Enqueue(entry);
			_lanes[key] = lane;
			_ = _started.RemoveAll(static started => started.Task.IsCompleted);
			_started.Add(lane);

			// Off this loop: a handler that blocks its thread must not stop the other chats.
			lane.Task = Task.Run(() => RunLaneAsync(lane, cancellationToken), CancellationToken.None);
		}
	}

	private async Task RunLaneAsync(Lane lane, CancellationToken cancellationToken)
	{
		try
		{
			while (true)
			{
				Entry next;
				lock (_lock)
				{
					if (lane.Queue.Count == 0)
					{
						_ = _lanes.Remove(lane.Key);
						return;
					}

					next = lane.Queue.Peek();
					lane.Current = next;
				}

				s_current.Value = next;
				await _running.WaitAsync(cancellationToken).ConfigureAwait(false);
				try
				{
					await next.Work!.RunAsync(cancellationToken).ConfigureAwait(false);
				}
#pragma warning disable CA1031 // Justification: whatever a handler throws once the stop began, its event is not handled; it comes again
				catch (Exception) when (cancellationToken.IsCancellationRequested)
#pragma warning restore CA1031
				{
					return;
				}
#pragma warning disable CA1031 // Justification: a failing event counts as handled and never blocks its lane (ARCHITECTURE §8.4)
				catch (Exception e)
#pragma warning restore CA1031
				{
					DispatchLog.RunFailed(logger, e, next.Seq);
				}
				finally
				{
					_ = _running.Release();
				}

				Finish(lane, next);
				_ = _pending.Release();
			}
		}
		catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
		{
			// Stopping.
		}
	}

	private void Finish(Lane lane, Entry entry)
	{
		lock (_lock)
		{
			_ = lane.Queue.Dequeue();

			// Only the seq stays while an earlier event is unfinished: not the event and its handlers.
			entry.Work = null;
			entry.Done = true;
			var moved = false;
			while (_order.TryPeek(out var head) && head.Done)
			{
				_watermark = _order.Dequeue().Seq;
				moved = true;
			}

			if (moved)
				(_movedAt, _warnedStuck) = (timeProvider.GetUtcNow(), false);
		}
	}

	private async Task AckLoopAsync(PeriodicTimer timer, CancellationToken cancellationToken)
	{
		try
		{
			while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
			{
				WarnIfStuck();
				await FlushAsync(cancellationToken).ConfigureAwait(false);
			}
		}
		catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
		{
			// Stopping; the last ack is DisposeAsync's.
		}
	}

	/// <summary>
	/// Logs, once per stall, a watermark that has not moved for <see cref="StuckAfter"/> while events wait: a handler
	/// that never returns holds every later ack of the session, and the events after it come again in the next process.
	/// </summary>
	private void WarnIfStuck()
	{
		ulong head;
		int waiting;
		lock (_lock)
		{
			if (_warnedStuck || !_order.TryPeek(out var first) || timeProvider.GetUtcNow() - _movedAt < StuckAfter)
				return;
			(_warnedStuck, head, waiting) = (true, first.Seq, _order.Count);
		}

		DispatchLog.WatermarkStuck(logger, head, waiting, StuckAfter);
	}

	private sealed class Lane(string key)
	{
		public string Key { get; } = key;

		public Queue<Entry> Queue { get; } = new();

		/// <summary>Set under the dispatcher's lock as the lane starts.</summary>
		public Task Task { get; set; } = Task.CompletedTask;

		/// <summary>The entry the lane runs or ran last; guarded by the dispatcher's lock.</summary>
		public Entry? Current { get; set; }
	}

	private sealed class Entry(Work work)
	{
		public ulong Seq { get; } = work.Seq;

		/// <summary>The event and its handlers until they finished, then null; guarded by the dispatcher's lock.</summary>
		public Work? Work { get; set; } = work;

		/// <summary>Every handler finished; guarded by the dispatcher's lock.</summary>
		public bool Done { get; set; }
	}
}
