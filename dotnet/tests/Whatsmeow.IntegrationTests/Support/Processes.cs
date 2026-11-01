using System.Diagnostics;

namespace Whatsmeow.IntegrationTests.Support;

public static class Processes
{
	/// <summary>Fails unless no process has this id any more.</summary>
	public static void AssertGone(int processId) => Assert.Throws<ArgumentException>(() => Process.GetProcessById(processId).Dispose());

	/// <summary>Waits until the process with this id exited, also when it is not this process's child.</summary>
	public static async Task WaitUntilGoneAsync(int processId, CancellationToken cancellationToken)
	{
		Process process;
		try
		{
			process = Process.GetProcessById(processId);
		}
		catch (ArgumentException)
		{
			return;
		}

		using (process)
			await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
	}

	/// <summary>The process id a stub sidecar wrote next to itself.</summary>
	public static int StubProcessId(string stub) =>
		int.Parse(File.ReadAllText(Path.Combine(Path.GetDirectoryName(stub)!, "pid")), System.Globalization.CultureInfo.InvariantCulture);

	/// <summary>Whether a stub sidecar was ever started.</summary>
	public static bool StubStarted(string stub) => File.Exists(Path.Combine(Path.GetDirectoryName(stub)!, "pid"));
}
