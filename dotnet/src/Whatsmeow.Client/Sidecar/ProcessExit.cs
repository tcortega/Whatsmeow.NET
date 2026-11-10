using System.Diagnostics;
using Microsoft.Win32.SafeHandles;

namespace Whatsmeow.Sidecar;

/// <summary>
/// Waits until a process is gone. On Windows <see cref="Process.WaitForExitAsync"/> returns once the exit code is set,
/// which a kill sets before the process terminated, while its image is still mapped, so its binary cannot be deleted
/// yet [observed: #179]; the process handle is signaled only once it terminated, so there this waits on the handle.
/// </summary>
internal static class ProcessExit
{
	/// <summary>Returns once <paramref name="process"/> terminated; the caller disposes it only after.</summary>
	public static async Task WaitAsync(Process process)
	{
		if (!OperatingSystem.IsWindows())
		{
			await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
			return;
		}

		var exited = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		using var handle = new ProcessHandle(process.SafeHandle);
#pragma warning disable RS0030 // Justification: no timeout here, only the one async wait a process handle has (#179)
		var wait = ThreadPool.RegisterWaitForSingleObject(handle, static (state, _) => ((TaskCompletionSource)state!).SetResult(), exited, Timeout.Infinite, executeOnlyOnce: true);
#pragma warning restore RS0030
		await exited.Task.ConfigureAwait(false);
		_ = wait.Unregister(waitObject: null);
	}

	/// <summary>The process handle as a <see cref="WaitHandle"/> that does not own it: the process still closes it.</summary>
	private sealed class ProcessHandle : WaitHandle
	{
		public ProcessHandle(SafeProcessHandle process) => SafeWaitHandle = new SafeWaitHandle(process.DangerousGetHandle(), ownsHandle: false);
	}
}
