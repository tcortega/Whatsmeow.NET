namespace Whatsmeow.Sidecar;

/// <summary>
/// Restarts the sidecar when it exits on its own, with a deterministic backoff: 1 s, doubling to at most 30 s, back
/// to 1 s after a run that stayed up 60 s (ADR 0017). Every wait is on the injected <see cref="TimeProvider"/>.
/// </summary>
/// <param name="launchAsync">Starts a sidecar and returns its process id and a task that completes with its exit code.</param>
/// <param name="maxDelay">The backoff's cap, <see cref="MaxDelay"/> outside the integration tests' shared fixture.</param>
/// <param name="external">
/// The address of an external sidecar, which <paramref name="launchAsync"/> connects to instead (ADR 0046): its task
/// completes when the sidecar went away. Null for one the client starts.
/// </param>
internal sealed class SidecarSupervisor(
	Func<CancellationToken, Task<(int ProcessId, Task<int> Exited)>> launchAsync,
	TimeProvider timeProvider,
	TimeSpan maxDelay,
	ILogger logger,
	string? external = null
) : IAsyncDisposable
{
	public static TimeSpan InitialDelay { get; } = TimeSpan.FromSeconds(1);

	public static TimeSpan MaxDelay { get; } = TimeSpan.FromSeconds(30);

	public static TimeSpan HealthyPeriod { get; } = TimeSpan.FromSeconds(60);

	private readonly CancellationTokenSource _stopping = new();
	private Task _loop = Task.CompletedTask;

	/// <summary>Starts watching the sidecar that was launched first.</summary>
	public void Start(int processId, Task<int> exited) => _loop = SuperviseAsync(processId, exited, _stopping.Token);

	/// <summary>
	/// Stops supervising and waits for the loop; a launch in progress is cancelled, and its failure is not thrown here.
	/// Calling it again does nothing.
	/// </summary>
	public async ValueTask DisposeAsync()
	{
		if (!_stopping.IsCancellationRequested)
			await _stopping.CancelAsync().ConfigureAwait(false);

		await _loop.ConfigureAwait(false);
		_stopping.Dispose();
	}

	private async Task SuperviseAsync(int processId, Task<int> exited, CancellationToken cancellationToken)
	{
		var delay = InitialDelay;
		var attempt = 0;
		var startedAt = timeProvider.GetTimestamp();

		try
		{
			while (true)
			{
				var exitCode = await exited.WaitAsync(cancellationToken).ConfigureAwait(false);
				if (external is null)
					SidecarLog.SidecarExited(logger, processId, exitCode);
				if (external is not null)
					SidecarLog.ExternalSidecarLost(logger, external);

				if (timeProvider.GetElapsedTime(startedAt) >= HealthyPeriod)
					(delay, attempt) = (InitialDelay, 0);

				while (true)
				{
					attempt++;
					await Task.Delay(delay, timeProvider, cancellationToken).ConfigureAwait(false);
					var waited = delay;
					delay = Next(delay);
					try
					{
						(processId, exited) = await launchAsync(cancellationToken).ConfigureAwait(false);
						startedAt = timeProvider.GetTimestamp();
						if (external is null)
							SidecarLog.SidecarRestarted(logger, processId, waited, attempt);
						if (external is not null)
							SidecarLog.ExternalSidecarBack(logger, external, waited, attempt);
						break;
					}
					catch (Exception e) when (!cancellationToken.IsCancellationRequested)
					{
						SidecarLog.SidecarRestartFailed(logger, e, attempt, delay);
					}
				}
			}
		}
		catch (Exception e) when (cancellationToken.IsCancellationRequested)
		{
			// Disposed: a launch's late failure, a refusal already on the wire, is not the dispose's (#132).
			if (e is not OperationCanceledException)
				SidecarLog.SidecarLaunchFailedAfterStopBegan(logger, e);
		}
	}

	private TimeSpan Next(TimeSpan delay) => delay * 2 > maxDelay ? maxDelay : delay * 2;
}
