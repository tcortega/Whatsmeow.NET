using System.Runtime.ExceptionServices;

namespace Whatsmeow.LiveTests;

/// <summary>
/// What a live test changed on a real account, put back after its body whatever the body did. Each restore runs on a
/// bound of its own (<see cref="Bound"/>), so none outlives the test by much, and whatever another threw, so one that
/// fails cannot keep the rest from running. A failed restore goes to the log; the body's own failure stays the test's,
/// and a restore's failure fails a test whose body passed.
/// </summary>
internal static class LiveRestore
{
	/// <summary>
	/// How long one restore may take. WhatsApp answered every live call within seconds so far [inferred: no live run
	/// timed one]; whatsmeow itself waits up to 75 s for an answer (request.go:150) [observed: source].
	/// </summary>
	public static readonly TimeSpan Bound = TimeSpan.FromSeconds(30);

	/// <summary>
	/// Runs <paramref name="body"/>, then each of <paramref name="restores"/>, in order, whatever happened. Each restore's
	/// <c>What</c> completes "could not ...", for the log: "set the lock back".
	/// </summary>
	public static async Task AfterAsync(LiveSidecar live, Func<Task> body, params (string What, Func<CancellationToken, Task> Run)[] restores)
	{
		ExceptionDispatchInfo? failed = null;
		try
		{
			await body().ConfigureAwait(false);
		}
#pragma warning disable CA1031 // Justification: rethrown below, after the restores ran.
		catch (Exception e)
#pragma warning restore CA1031
		{
			failed = ExceptionDispatchInfo.Capture(e);
		}

		var unrestored = await RunAsync(live, restores).ConfigureAwait(false);
		failed?.Throw();
		if (unrestored.Count > 0)
			Assert.Fail(LiveRedaction.Redact(string.Join("; ", unrestored)));
	}

	/// <summary>Runs each restore, bounded, whatever the others threw; returns a "could not ..." line for each that failed.</summary>
	public static async Task<IReadOnlyList<string>> RunAsync(LiveSidecar live, params (string What, Func<CancellationToken, Task> Run)[] restores)
	{
		var unrestored = new List<string>();
		foreach (var (what, run) in restores)
		{
			using var bound = LiveClock.Bound(Bound, CancellationToken.None);
			try
			{
				await run(bound.Token).ConfigureAwait(false);
			}
#pragma warning disable CA1031 // Justification: one restore that fails must not keep the others from running; each failure is reported.
			catch (Exception e)
#pragma warning restore CA1031
			{
				var line = $"could not {what}: {Describe(e)}";
				live.Write($"live: {line}");
				unrestored.Add(line);
			}
		}

		return unrestored;
	}

	/// <summary>A failure, for the log: its type, and the error code and WhatsApp's code when it has them; never its message.</summary>
	public static string Describe(Exception e) =>
		e switch
		{
			WhatsAppErrorException w => $"{w.GetType().Name} {w.ErrorCode} {w.Code}",
			WhatsmeowException w => $"{w.GetType().Name} {w.ErrorCode}",
			OperationCanceledException => $"not done within {Bound.TotalSeconds:F0} s",
			_ => e.GetType().Name,
		};
}
