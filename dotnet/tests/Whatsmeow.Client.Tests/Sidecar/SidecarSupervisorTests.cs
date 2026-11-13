using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Testing;
using Microsoft.Extensions.Time.Testing;
using Whatsmeow.Sidecar;

namespace Whatsmeow.Client.Tests.Sidecar;

/// <summary>
/// The restart and backoff state machine (ADR 0017), on a fake clock: no real process, no real wait.
/// </summary>
public sealed class SidecarSupervisorTests : IAsyncDisposable
{
	private static readonly TimeSpan s_tick = TimeSpan.FromTicks(1);

	private readonly TimerObservingTimeProvider _time = new();
	private readonly FakeLogCollector _logs = new();
	private readonly Channel<Launch> _launches = Channel.CreateUnbounded<Launch>();
	private readonly SidecarSupervisor _supervisor;

	public SidecarSupervisorTests() =>
		_supervisor = new SidecarSupervisor(LaunchAsync, _time, SidecarSupervisor.MaxDelay, new FakeLogger(_logs, "Whatsmeow.Client"));

	public async ValueTask DisposeAsync() => await _supervisor.DisposeAsync().ConfigureAwait(false);

	[Fact(Timeout = 10_000)]
	public async Task Restart_AfterTheSidecarExits_WaitsOneSecond()
	{
		var ct = TestContext.Current.CancellationToken;
		var first = Started(1);
		var start = _time.GetUtcNow();
		first.Exit.SetResult(1);

		Assert.Equal(TimeSpan.FromSeconds(1), await _time.Timers.Reader.ReadAsync(ct));
		_time.Advance(TimeSpan.FromSeconds(1) - s_tick);
		Assert.False(_launches.Reader.TryRead(out _));

		_time.Advance(s_tick);
		_ = await _launches.Reader.ReadAsync(ct);
		Assert.Equal(TimeSpan.FromSeconds(1), _time.GetUtcNow() - start);
	}

	[Fact(Timeout = 10_000)]
	public async Task Restart_FastExitsInARow_DoubleTheDelayUpToThirtySeconds()
	{
		var ct = TestContext.Current.CancellationToken;
		var running = Started(1);
		var delays = new List<TimeSpan>();

		for (var pid = 2; pid <= 8; pid++)
		{
			running.Exit.SetResult(1);
			var delay = await _time.Timers.Reader.ReadAsync(ct);
			delays.Add(delay);
			_time.Advance(delay);
			running = (await _launches.Reader.ReadAsync(ct)).Succeed(pid);
		}

		Assert.Equal([1, 2, 4, 8, 16, 30, 30], delays.Select(d => d.TotalSeconds));
	}

	[Fact(Timeout = 10_000)]
	public async Task Restart_AfterARunOfSixtySeconds_WaitsOneSecondAgain()
	{
		var ct = TestContext.Current.CancellationToken;
		var first = Started(1);
		first.Exit.SetResult(1);
		_time.Advance(await _time.Timers.Reader.ReadAsync(ct));
		var second = (await _launches.Reader.ReadAsync(ct)).Succeed(2);
		await second.Logged;

		_time.Advance(TimeSpan.FromSeconds(60));
		second.Exit.SetResult(1);

		Assert.Equal(TimeSpan.FromSeconds(1), await _time.Timers.Reader.ReadAsync(ct));
	}

	[Fact(Timeout = 10_000)]
	public async Task Restart_AfterARunShorterThanSixtySeconds_KeepsBackingOff()
	{
		var ct = TestContext.Current.CancellationToken;
		var first = Started(1);
		first.Exit.SetResult(1);
		_time.Advance(await _time.Timers.Reader.ReadAsync(ct));
		var second = (await _launches.Reader.ReadAsync(ct)).Succeed(2);
		await second.Logged;

		_time.Advance(TimeSpan.FromSeconds(60) - s_tick);
		second.Exit.SetResult(1);

		Assert.Equal(TimeSpan.FromSeconds(2), await _time.Timers.Reader.ReadAsync(ct));
	}

	[Fact(Timeout = 10_000)]
	public async Task Restart_LaunchFails_BacksOffAndTriesAgain()
	{
		var ct = TestContext.Current.CancellationToken;
		var first = Started(1);
		first.Exit.SetResult(1);
		_time.Advance(await _time.Timers.Reader.ReadAsync(ct));
		(await _launches.Reader.ReadAsync(ct)).Fail(new SidecarUnavailableException("did not start"));

		var delay = await _time.Timers.Reader.ReadAsync(ct);
		_time.Advance(delay);
		_ = (await _launches.Reader.ReadAsync(ct)).Succeed(2);

		Assert.Equal(TimeSpan.FromSeconds(2), delay);
		_ = Assert.Single(_logs.GetSnapshot(), r => r.Id.Name == "SidecarRestartFailed" && r.Exception is SidecarUnavailableException);
	}

	[Fact(Timeout = 10_000)]
	public async Task Restart_Succeeds_LogsSidecarRestarted()
	{
		var ct = TestContext.Current.CancellationToken;
		var first = Started(1);
		first.Exit.SetResult(1);
		_time.Advance(await _time.Timers.Reader.ReadAsync(ct));
		var restarted = (await _launches.Reader.ReadAsync(ct)).Succeed(2);
		await restarted.Logged;

		var record = Assert.Single(_logs.GetSnapshot(), r => r.Id.Name == "SidecarRestarted");
		Assert.Equal(("2", "00:00:01", "1"), (record.GetStructuredStateValue("ProcessId"), record.GetStructuredStateValue("Delay"), record.GetStructuredStateValue("Attempt")));
	}

	[Fact(Timeout = 10_000)]
	public async Task Dispose_DuringTheBackoff_NeverLaunchesAgain()
	{
		var ct = TestContext.Current.CancellationToken;
		var first = Started(1);
		first.Exit.SetResult(1);
		_ = await _time.Timers.Reader.ReadAsync(ct);

		await _supervisor.DisposeAsync();
		_time.Advance(TimeSpan.FromHours(1));

		Assert.False(_launches.Reader.TryRead(out _));
	}

	[Fact(Timeout = 10_000)]
	public async Task Dispose_ALaunchFailsAfterTheDisposeBegan_DisposesWithoutItsError()
	{
		var ct = TestContext.Current.CancellationToken;
		var launching = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		await using var supervisor = new SidecarSupervisor(
			async cancellationToken =>
			{
				// A reconnect whose refusal was on the wire when the dispose began (#132).
				var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
				using var registration = cancellationToken.Register(cancelled.SetResult);
				launching.SetResult();
				await cancelled.Task.ConfigureAwait(false);
				throw new SidecarUnavailableException("refused the token");
			},
			_time,
			SidecarSupervisor.MaxDelay,
			new FakeLogger(_logs, "Whatsmeow.Client")
		);
		var exit = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
		supervisor.Start(1, exit.Task);
		exit.SetResult(1);
		_time.Advance(await _time.Timers.Reader.ReadAsync(ct));
		await launching.Task.WaitAsync(ct);

		await supervisor.DisposeAsync();

		Assert.DoesNotContain(_logs.GetSnapshot(), r => r.Id.Name == "SidecarRestartFailed");
		_ = Assert.Single(_logs.GetSnapshot(), r => r.Id.Name == "SidecarLaunchFailedAfterStopBegan" && r.Level == LogLevel.Debug && r.Exception is SidecarUnavailableException);
	}

	private Running Started(int processId)
	{
		var running = new Running(_logs);
		_supervisor.Start(processId, running.Exit.Task);
		return running;
	}

	private async Task<(int ProcessId, Task<int> Exited)> LaunchAsync(CancellationToken cancellationToken)
	{
		var launch = new Launch(_logs);
		await _launches.Writer.WriteAsync(launch, cancellationToken).ConfigureAwait(false);
		return await launch.Result.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
	}

	/// <summary>A sidecar the supervisor watches: the test decides when it exits.</summary>
	private sealed class Running(FakeLogCollector logs)
	{
		private readonly int _logsBefore = logs.Count;

		public TaskCompletionSource<int> Exit { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

		/// <summary>Completes once the supervisor logged anything after this sidecar started.</summary>
		public Task Logged => WaitForLogAsync();

		private async Task WaitForLogAsync()
		{
			await foreach (var _ in logs.GetLogsAsync(TestContext.Current.CancellationToken).ConfigureAwait(false))
			{
				if (logs.Count > _logsBefore)
					return;
			}
		}
	}

	/// <summary>One call of the launch delegate: the test makes it succeed or fail.</summary>
	private sealed class Launch(FakeLogCollector logs)
	{
		public TaskCompletionSource<(int, Task<int>)> Result { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

		public Running Succeed(int processId)
		{
			var running = new Running(logs);
			Result.SetResult((processId, running.Exit.Task));
			return running;
		}

		public void Fail(Exception exception) => Result.SetException(exception);
	}

	/// <summary>A fake clock that reports the due time of every timer created on it, such as each Task.Delay.</summary>
	private sealed class TimerObservingTimeProvider : FakeTimeProvider
	{
		public Channel<TimeSpan> Timers { get; } = Channel.CreateUnbounded<TimeSpan>();

		public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
		{
			var timer = base.CreateTimer(callback, state, dueTime, period);
			_ = Timers.Writer.TryWrite(dueTime);
			return timer;
		}
	}
}
