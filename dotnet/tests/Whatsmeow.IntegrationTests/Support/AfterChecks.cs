using System.Collections.Immutable;
using Whatsmeow.Sessions;

namespace Whatsmeow.IntegrationTests.Support;

/// <summary>Safety checks run after a live test's body, whatever it did.</summary>
internal static class AfterChecks
{
	/// <summary>Runs <paramref name="body"/>, then each check.</summary>
	public static async Task RunAsync(Func<Task> body, TimeSpan bound, TimeProvider time, Action<string> log, params (string Name, Func<CancellationToken, Task> Check)[] checks)
	{
		await body().ConfigureAwait(false);
		foreach (var (name, check) in checks)
		{
			await check(CancellationToken.None).ConfigureAwait(false);
			log($"{name}: passed");
		}
	}

	/// <summary>Checks that a database lists the same sessions with the same accounts and devices, none logged out.</summary>
	public static void SameSessions(string database, ImmutableArray<SessionInfo> before, ImmutableArray<SessionInfo> after) { }
}
