namespace Whatsmeow.Extensions.Hosting;

/// <summary>
/// The runs of a host's handlers on the fakes that replaced its sessions (ADR 0036). The host's stop cancels the ones
/// running and waits for them, as a sidecar's stop does for its clients' handlers (ARCHITECTURE §8.4, ADR 0034); none
/// starts after it. The fake is the test's and outlives the host, so its own stop is not the host's.
/// </summary>
[SuppressMessage("Design", "CA1001:Types that own disposable fields should be disposable", Justification = "The token source is never disposed: a handler still running once the stop stopped waiting reads its token afterwards; it holds no timer and no unmanaged resource.")]
internal sealed class HandlerRuns
{
	private readonly CancellationTokenSource _stopping = new();
	private readonly Lock _lock = new();
	private readonly HashSet<Task> _running = [];
	private bool _stopped;

	/// <summary>Runs <paramref name="run"/> with a token the host's stop cancels too; nothing once the stop began.</summary>
	public async ValueTask RunAsync(Func<CancellationToken, ValueTask> run, CancellationToken cancellationToken)
	{
		var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		lock (_lock)
		{
			if (_stopped)
				return;
			_ = _running.Add(done.Task);
		}

		try
		{
			using var stopping = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _stopping.Token);
			await run(stopping.Token).ConfigureAwait(false);
		}
#pragma warning disable CA1031 // Justification: whatever a handler throws once the host stopped it means only that it was stopped
		catch (Exception) when (_stopping.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
#pragma warning restore CA1031
		{
			// The host stopped it: the event is not handled, as on a real session, and the fake's test sees no handler error.
		}
		finally
		{
			lock (_lock)
				_ = _running.Remove(done.Task);
			done.SetResult();
		}
	}

	/// <summary>
	/// Cancels the running handlers and waits for them; once <paramref name="stopWaiting"/> is cancelled, it no longer
	/// waits for handlers that ignore their cancellation (ADR 0034).
	/// </summary>
	public async Task StopAsync(CancellationToken stopWaiting)
	{
		Task[] running;
		lock (_lock)
		{
			_stopped = true;
			running = [.. _running];
		}

		await _stopping.CancelAsync().ConfigureAwait(false);
		await Task.WhenAll(running).WaitAsync(stopWaiting).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
	}
}
