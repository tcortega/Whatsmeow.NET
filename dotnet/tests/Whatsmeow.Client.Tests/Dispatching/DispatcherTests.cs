using System.Globalization;
using System.Threading.Channels;
using Microsoft.Extensions.Logging.Testing;
using Microsoft.Extensions.Time.Testing;
using Whatsmeow.Core.Dispatching;

namespace Whatsmeow.Client.Tests.Dispatching;

/// <summary>
/// Per-chat ordering and the ack watermark (ARCHITECTURE §8.4, invariant 3), on a fake clock: every handler is
/// released by the test, and every wait ends on a condition.
/// </summary>
public sealed class DispatcherTests : IAsyncDisposable
{
	private readonly FakeTimeProvider _time = new();
	private readonly Channel<Work> _input = Channel.CreateUnbounded<Work>();
	private readonly Channel<(ulong Seq, DateTimeOffset At)> _acks = Channel.CreateUnbounded<(ulong, DateTimeOffset)>();
	private readonly Channel<string> _log = Channel.CreateUnbounded<string>();
	private readonly Dictionary<ulong, TaskCompletionSource> _release = [];
	private readonly Channel<Exception> _failedAcks = Channel.CreateUnbounded<Exception>();
	private readonly Channel<int> _depth = Channel.CreateUnbounded<int>();
	private readonly Dispatcher _dispatcher;
	private readonly Logs _logs = new();
	private int _acksToFail;

	public DispatcherTests()
	{
		_dispatcher = new Dispatcher(
			_input.Reader,
			(seq, token) =>
			{
				if (Interlocked.Decrement(ref _acksToFail) >= 0)
				{
					// As a call that the transport ends without a status does.
					var failure = new OperationCanceledException("the call ended without a status");
					_ = _failedAcks.Writer.TryWrite(failure);
					throw failure;
				}

				_ = _acks.Writer.TryWrite((seq, _time.GetUtcNow()));
				return Task.CompletedTask;
			},
			_time,
			new FakeLogger(_logs.Collector, "Whatsmeow.Client"),
			delta => _depth.Writer.TryWrite(delta)
		);
		_dispatcher.Start();
	}

	public async ValueTask DisposeAsync() => await _dispatcher.DisposeAsync().ConfigureAwait(false);

	[Fact(Timeout = 10_000)]
	public async Task OneLane_TheNextEventWaitsUntilThePreviousFinished()
	{
		var ct = TestContext.Current.CancellationToken;
		Post(1, "a", blocked: true);
		Post(2, "a");
		Post(3, "b");

		// 3 ran, so 2 was dispatched too; lanes start off the read loop, so 1 and 3 start in either order.
		string[] first = [.. (await NextAsync(3, ct)).Order(StringComparer.Ordinal)];
		Assert.Equal(["end 3", "start 1", "start 3"], first);
		Release(1);
		Assert.Equal(["end 1", "start 2", "end 2"], await NextAsync(3, ct));
	}

	[Fact(Timeout = 10_000)]
	public async Task AHandlerThatBlocksItsThread_DoesNotStopTheOtherChats()
	{
		var ct = TestContext.Current.CancellationToken;
		using var gate = new ManualResetEventSlim();
		_ = _input.Writer.TryWrite(
			new Work
			{
				Seq = 1,
				Lane = "a",
				RunAsync = _ =>
				{
					Log("start 1");
					gate.Wait(ct);
					return ValueTask.CompletedTask;
				},
			}
		);
		Post(2, "b");

		string[] started = [.. (await NextAsync(3, ct)).Order(StringComparer.Ordinal)];
		Assert.Equal(["end 2", "start 1", "start 2"], started);
		gate.Set();
	}

	[Fact(Timeout = 10_000)]
	public async Task Dispose_FromInsideAHandler_ReturnsWithoutWaitingForThatHandler()
	{
		var ct = TestContext.Current.CancellationToken;
		Post(1, "a");
		_ = _input.Writer.TryWrite(
			new Work
			{
				Seq = 2,
				Lane = "a",
				RunAsync = async _ =>
				{
					// A handler that deletes its own session or disposes the sidecar.
					await _dispatcher.DisposeAsync().ConfigureAwait(false);
					Log("disposed from inside");
				},
			}
		);

		Assert.Equal(["start 1", "end 1", "disposed from inside"], await NextAsync(3, ct));
		Assert.Equal(1UL, (await _acks.Reader.ReadAsync(ct)).Seq);
	}

	[Fact(Timeout = 10_000)]
	public async Task Stop_AHandlerThatThrowsOnceTheStopBegan_HasNotHandledItsEvent()
	{
		var ct = TestContext.Current.CancellationToken;
		Post(1, "a");
		_ = _input.Writer.TryWrite(
			new Work
			{
				Seq = 2,
				Lane = "a",
				RunAsync = async token =>
				{
					Log("start 2");
					try
					{
						await Task.Delay(Timeout.InfiniteTimeSpan, _time, token).ConfigureAwait(false);
					}
					catch (OperationCanceledException e)
					{
						// A handler that turns its cancellation into another error.
						throw new InvalidOperationException("the handler failed while stopping", e);
					}
				},
			}
		);
		Assert.Equal(["start 1", "end 1", "start 2"], await NextAsync(3, ct));

		await _dispatcher.DisposeAsync();

		Assert.Equal(1UL, (await _acks.Reader.ReadAsync(ct)).Seq);
		Assert.False(_acks.Reader.TryRead(out _));
	}

	[Fact(Timeout = 10_000)]
	public async Task Stop_AHandlerThatReturnsOnceTheStopBegan_HasNotHandledItsEvent()
	{
		var ct = TestContext.Current.CancellationToken;
		Post(1, "a");
		_ = _input.Writer.TryWrite(
			new Work
			{
				Seq = 2,
				Lane = "a",
				RunAsync = async token =>
				{
					Log("start 2");
					try
					{
						await Task.Delay(Timeout.InfiniteTimeSpan, _time, token).ConfigureAwait(false);
					}
					catch (OperationCanceledException)
					{
						// A handler that swallows its own cancellation and returns.
					}
				},
			}
		);
		Assert.Equal(["start 1", "end 1", "start 2"], await NextAsync(3, ct));

		await _dispatcher.DisposeAsync();

		Assert.Equal(1UL, (await _acks.Reader.ReadAsync(ct)).Seq);
		Assert.False(_acks.Reader.TryRead(out _));
	}

	[Fact(Timeout = 10_000)]
	public async Task Ack_ThatFailsWithAnyException_IsTriedAgainOnTheNextTickAndAtTheStop()
	{
		var ct = TestContext.Current.CancellationToken;
		_acksToFail = 1;
		Post(1, "a");
		Post(2, "a", blocked: true);
		Assert.Equal(["start 1", "end 1", "start 2"], await NextAsync(3, ct));

		_time.Advance(Dispatcher.AckInterval);
		_ = await _failedAcks.Reader.ReadAsync(ct);
		_time.Advance(Dispatcher.AckInterval);
		Assert.Equal(1UL, (await _acks.Reader.ReadAsync(ct)).Seq);

		Release(2);
		Assert.Equal(["end 2"], await NextAsync(1, ct));
		Post(3, "a", blocked: true);
		Assert.Equal(["start 3"], await NextAsync(1, ct));
		await _dispatcher.DisposeAsync();
		Assert.Equal(2UL, (await _acks.Reader.ReadAsync(ct)).Seq);
	}

	[Fact(Timeout = 10_000)]
	public async Task NoAck_BehindAHandlerThatNeverReturns_KeepsNothingOfTheEventsThatFinished()
	{
		var ct = TestContext.Current.CancellationToken;
		const int Finishing = 1_000;
		var input = Channel.CreateUnbounded<Work>();
		var unacked = new Dispatcher(input.Reader, ackAsync: null, _time, new FakeLogger(_logs.Collector, "Whatsmeow.Client"));
		await using var disposeUnacked = unacked.ConfigureAwait(false);
		unacked.Start();
		var finished = 0;
		var allFinished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

		_ = input.Writer.TryWrite(new Work { Seq = 1, Lane = "a", RunAsync = async token => await Task.Delay(Timeout.InfiniteTimeSpan, _time, token).ConfigureAwait(false) });
		for (ulong seq = 2; seq <= Finishing + 1; seq++)
		{
			_ = input.Writer.TryWrite(
				new Work
				{
					Seq = seq,
					Lane = "b",
					RunAsync = _ =>
					{
						if (Interlocked.Increment(ref finished) == Finishing)
							allFinished.SetResult();
						return ValueTask.CompletedTask;
					},
				}
			);
		}

		await allFinished.Task.WaitAsync(ct);

		// Nothing acks them, so no watermark waits for the stuck one: none of them is kept.
		Assert.Equal(0, unacked.Unacked);
	}

	[Fact(Timeout = 10_000)]
	public async Task Start_AfterTheStop_StartsNothing()
	{
		_ = TestContext.Current.CancellationToken;
		var time = new CountingTimeProvider();
		var stopped = new Dispatcher(Channel.CreateUnbounded<Work>().Reader, (_, _) => Task.CompletedTask, time, new FakeLogger(_logs.Collector, "Whatsmeow.Client"));
		await stopped.DisposeAsync();
		var before = time.Timers;

		stopped.Start();

		// No ack timer: the stop's own timeout timer is the only one.
		Assert.Equal(before, time.Timers);
	}

	[Fact(Timeout = 10_000)]
	public async Task Watermark_StillWhileEventsWait_IsLoggedOnce()
	{
		var ct = TestContext.Current.CancellationToken;
		Post(1, "a", blocked: true);
		Post(2, "b");
		Post(3, "b", blocked: true);
		_ = await NextAsync(4, ct);

		_time.Advance(Dispatcher.StuckAfter);

		var stuck = await _logs.WaitForAsync(record => record.Id.Name == "WatermarkStuck", ct);
		Assert.Equal(("1", "3"), (stuck.GetStructuredStateValue("Seq"), stuck.GetStructuredStateValue("Waiting")));
		Release(1);
		Release(3);
	}

	[Fact(Timeout = 10_000)]
	public async Task DifferentLanes_RunConcurrently()
	{
		Post(1, "a", blocked: true);
		Post(2, "b", blocked: true);

		// Both run before either finished; lanes start off the read loop, in either order.
		string[] started = [.. (await NextAsync(2, TestContext.Current.CancellationToken)).Order(StringComparer.Ordinal)];
		Assert.Equal(["start 1", "start 2"], started);
		Release(2);
		Release(1);
	}

	[Fact(Timeout = 10_000)]
	public async Task Watermark_NeverPassesAnEventWhoseHandlersHaveNotFinished()
	{
		Post(1, "a", blocked: true);
		Post(2, "b");
		Post(3, "b", blocked: true);

		// 3 started, so 2 is done; 1 is not. The two lanes interleave in any order.
		string[] lines = [.. (await NextAsync(4, TestContext.Current.CancellationToken)).Order(StringComparer.Ordinal)];
		Assert.Equal(["end 2", "start 1", "start 2", "start 3"], lines);
		await _dispatcher.FlushAsync(TestContext.Current.CancellationToken);
		Assert.False(_acks.Reader.TryRead(out _));

		Release(1);
		Post(4, "a", blocked: true);
		Assert.Equal(["end 1", "start 4"], await NextAsync(2, TestContext.Current.CancellationToken));
		await _dispatcher.FlushAsync(TestContext.Current.CancellationToken);
		Assert.Equal(2UL, (await _acks.Reader.ReadAsync(TestContext.Current.CancellationToken)).Seq);

		Release(3);
		Release(4);
	}

	[Fact(Timeout = 10_000)]
	public async Task NoMoreThanMaxPending_AreReadWhileNoneFinishes()
	{
		var ct = TestContext.Current.CancellationToken;
		for (ulong seq = 1; seq <= Dispatcher.MaxPending; seq++)
			Post(seq, $"lane {seq}", blocked: true);
		var ran = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		const ulong Over = Dispatcher.MaxPending + 1;
		_ = _input.Writer.TryWrite(
			new Work
			{
				Seq = Over,
				Lane = $"lane {Over}",
				RunAsync = _ =>
				{
					ran.SetResult();
					return ValueTask.CompletedTask;
				},
			}
		);

		// Every one read, and as many running as may: none finished, so the one over the bound stays unread.
		await ReceivedAsync(Dispatcher.MaxPending, ct);
		var started = await NextAsync(Dispatcher.MaxConcurrentLanes, ct);
		Assert.Equal(1, _input.Reader.Count);

		var first = StartedSeq(started[0]);
		Release(first);
		await ReceivedAsync(1, ct);
		Assert.Equal(0, _input.Reader.Count);

		for (ulong seq = 1; seq <= Dispatcher.MaxPending; seq++)
		{
			if (seq != first)
				Release(seq);
		}

		await ran.Task.WaitAsync(ct);
	}

	[Fact(Timeout = 10_000)]
	public async Task NoMoreThanMaxConcurrentLanes_RunAtOnce()
	{
		var ct = TestContext.Current.CancellationToken;
		const int Lanes = Dispatcher.MaxConcurrentLanes + 1;
		for (ulong seq = 1; seq <= Lanes; seq++)
			Post(seq, $"lane {seq}", blocked: true);

		// Every lane has its event, and as many started as may: the last one waits for a lane to finish.
		await ReceivedAsync(Lanes, ct);
		var started = (await NextAsync(Dispatcher.MaxConcurrentLanes, ct)).Select(StartedSeq).ToHashSet();
		Assert.False(_log.Reader.TryRead(out _));
		var waiting = Enumerable.Range(1, Lanes).Select(seq => (ulong)seq).Single(seq => !started.Contains(seq));

		var first = started.First();
		Release(first);
		Assert.Equal([$"end {first}", $"start {waiting}"], await NextAsync(2, ct));

		foreach (var seq in started.Where(seq => seq != first).Append(waiting))
			Release(seq);
	}

	[Fact(Timeout = 10_000)]
	public async Task Watermark_AnEventWhoseHandlerThrowsCountsAsHandled()
	{
		_ = _input.Writer.TryWrite(new Work { Seq = 1, Lane = "a", RunAsync = _ => throw new InvalidOperationException("the handler failed") });
		Post(2, "a", blocked: true);

		Assert.Equal(["start 2"], await NextAsync(1, TestContext.Current.CancellationToken));
		await _dispatcher.FlushAsync(TestContext.Current.CancellationToken);
		Assert.Equal(1UL, (await _acks.Reader.ReadAsync(TestContext.Current.CancellationToken)).Seq);
		Release(2);
	}

	[Fact(Timeout = 10_000)]
	public async Task Ack_GoesOutOnTheTimer()
	{
		var start = _time.GetUtcNow();
		Post(1, "a");
		Post(2, "a", blocked: true);
		Assert.Equal(["start 1", "end 1", "start 2"], await NextAsync(3, TestContext.Current.CancellationToken));

		_time.Advance(Dispatcher.AckInterval);

		Assert.Equal((1UL, start + Dispatcher.AckInterval), await _acks.Reader.ReadAsync(TestContext.Current.CancellationToken));
		Release(2);
	}

	[Fact(Timeout = 10_000)]
	public async Task Stop_FlushesTheWatermark_AndAHandlerItCancelledIsNotHandled()
	{
		Post(1, "a");
		_ = _input.Writer.TryWrite(
			new Work
			{
				Seq = 2,
				Lane = "a",
				RunAsync = async ct =>
				{
					Log("start 2");
					await Task.Delay(Timeout.InfiniteTimeSpan, _time, ct).ConfigureAwait(false);
				},
			}
		);
		Assert.Equal(["start 1", "end 1", "start 2"], await NextAsync(3, TestContext.Current.CancellationToken));

		await _dispatcher.DisposeAsync();

		Assert.Equal(1UL, (await _acks.Reader.ReadAsync(TestContext.Current.CancellationToken)).Seq);
		Assert.False(_acks.Reader.TryRead(out _));
	}

	[Fact(Timeout = 10_000)]
	public async Task Stop_OnceItsWaitIsCancelled_AcksWhatFinishedWithoutWaitingForAHandlerThatIgnoresItsToken()
	{
		var ct = TestContext.Current.CancellationToken;
		var never = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		Post(1, "a");
		_ = _input.Writer.TryWrite(
			new Work
			{
				Seq = 2,
				Lane = "b",
				RunAsync = async _ =>
				{
					Log("start 2");
					await never.Task.ConfigureAwait(false);
				},
			}
		);
		Post(3, "c");
		Assert.Equal(["end 1", "end 3", "start 1", "start 2", "start 3"], (await NextAsync(5, ct)).Order(StringComparer.Ordinal), StringComparer.Ordinal);
		// A handler logs its end before it returns, and its event finishes after: wait until 1 and 3 have.
		await FinishedAsync(2, ct);

		try
		{
			await _dispatcher.StopAsync(new CancellationToken(canceled: true));

			// 3 finished, but 2 never did: the watermark is 1, and 2 and 3 come again.
			Assert.Equal(1UL, (await _acks.Reader.ReadAsync(ct)).Seq);
			Assert.False(_acks.Reader.TryRead(out _));
		}
		finally
		{
			never.SetResult();
		}
	}

	private void Post(ulong seq, string lane, bool blocked = false)
	{
		var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		_release[seq] = release;
		if (!blocked)
			release.SetResult();

		_ = _input.Writer.TryWrite(
			new Work
			{
				Seq = seq,
				Lane = lane,
				RunAsync = async token =>
				{
					Log($"start {seq}");
					await release.Task.WaitAsync(token).ConfigureAwait(false);
					Log($"end {seq}");
				},
			}
		);
	}

	private void Release(ulong seq) => _release[seq].SetResult();

	/// <summary>Waits until <paramref name="count"/> more events finished: the dispatcher reports -1 once it recorded each.</summary>
	private async Task FinishedAsync(int count, CancellationToken cancellationToken)
	{
		while (count > 0)
		{
			if (await _depth.Reader.ReadAsync(cancellationToken).ConfigureAwait(false) < 0)
				count--;
		}
	}

	/// <summary>Waits until <paramref name="count"/> more events were read: the dispatcher reports +1 for each.</summary>
	private async Task ReceivedAsync(int count, CancellationToken cancellationToken)
	{
		while (count > 0)
		{
			if (await _depth.Reader.ReadAsync(cancellationToken).ConfigureAwait(false) > 0)
				count--;
		}
	}

	/// <summary>The seq of a <c>start N</c> line.</summary>
	private static ulong StartedSeq(string line) => ulong.Parse(line.AsSpan("start ".Length), CultureInfo.InvariantCulture);

	private void Log(string line) => _ = _log.Writer.TryWrite(line);

	private async Task<string[]> NextAsync(int count, CancellationToken cancellationToken)
	{
		var lines = new string[count];
		for (var i = 0; i < count; i++)
			lines[i] = await _log.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);
		return lines;
	}

	/// <summary>The dispatcher's log records, waited for by condition.</summary>
	private sealed class Logs
	{
		public FakeLogCollector Collector { get; } = new();

		public async Task<FakeLogRecord> WaitForAsync(Func<FakeLogRecord, bool> predicate, CancellationToken cancellationToken)
		{
			var records = Collector.GetLogsAsync(cancellationToken).GetAsyncEnumerator(cancellationToken);
			await using var disposeRecords = records.ConfigureAwait(false);
			while (true)
			{
				if (Collector.GetSnapshot().FirstOrDefault(predicate) is { } match)
					return match;
				_ = await records.MoveNextAsync().ConfigureAwait(false);
			}
		}
	}

	private sealed class CountingTimeProvider : FakeTimeProvider
	{
		public int Timers { get; private set; }

		public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
		{
			Timers++;
			return base.CreateTimer(callback, state, dueTime, period);
		}
	}
}
