namespace Whatsmeow.IntegrationTests.Support;

/// <summary>
/// Deletes what a test made, waiting out another process's brief open of a file (#187). On the hosted Windows runner,
/// GitHub's <c>provjobd</c> opens the binary of a process that just exited, without delete sharing, and deleting it then
/// fails with a sharing violation for up to about 200 ms [observed: #187]. Only that error is waited out, for at most
/// 10 s; any other throws at once, such as the access denied of a binary its dead process still maps (#179).
/// </summary>
public static class Deletion
{
	private const int SharingViolation = unchecked((int)0x80070020);

	private static readonly TimeSpan s_step = TimeSpan.FromMilliseconds(10);
	private static readonly TimeSpan s_limit = TimeSpan.FromSeconds(10);

#pragma warning disable RS0030 // Justification: another process's handle is released on its own clock, so the wait is bounded with the real one
	private static readonly TimeProvider s_wallClock = TimeProvider.System;
#pragma warning restore RS0030

	public static Task DeleteFileAsync(string path) => DeleteAsync(() => File.Delete(path));

	public static Task DeleteDirectoryAsync(string path) => DeleteAsync(() => Directory.Delete(path, recursive: true));

	private static async Task DeleteAsync(Action delete)
	{
		var started = s_wallClock.GetTimestamp();
		while (true)
		{
			try
			{
				delete();
				return;
			}
			catch (IOException e) when (e.HResult == SharingViolation && s_wallClock.GetElapsedTime(started) < s_limit)
			{
				// Held by another process: deleted once it lets go.
			}

			await Task.Delay(s_step, s_wallClock, CancellationToken.None).ConfigureAwait(false);
		}
	}
}
