using System.Collections.Immutable;
using System.Runtime.ExceptionServices;
using Whatsmeow.Sessions;

namespace Whatsmeow.IntegrationTests.Support;

/// <summary>
/// Safety checks a live test runs after its body, whatever the body did (ADR 0062): each check runs on a bound of its
/// own, whatever the body or another check threw, and its verdict goes to the log; the body's failure is thrown first,
/// with every check's, so a failed body never hides a failed check. A check that ignores its token is ended by the bound.
/// </summary>
internal static class AfterChecks
{
	public static async Task RunAsync(Func<Task> body, TimeSpan bound, TimeProvider time, Action<string> log, params (string Name, Func<CancellationToken, Task> Check)[] checks)
	{
		var failures = new List<ExceptionDispatchInfo>();
		try
		{
			await body().ConfigureAwait(false);
		}
#pragma warning disable CA1031 // Justification: thrown first at the end, after every check ran.
		catch (Exception e)
#pragma warning restore CA1031
		{
			failures.Add(ExceptionDispatchInfo.Capture(e));
		}

		foreach (var (name, check) in checks)
		{
			using var bounded = new CancellationTokenSource(bound, time);
			try
			{
				await check(bounded.Token).WaitAsync(bound, time).ConfigureAwait(false);
				log($"{name}: passed");
			}
#pragma warning disable CA1031 // Justification: thrown at the end, with the body's, after the other checks ran.
			catch (Exception e)
#pragma warning restore CA1031
			{
				log($"{name}: failed, {e.GetType().Name}");
				failures.Add(ExceptionDispatchInfo.Capture(e));
			}
		}

		if (failures.Count == 1)
			failures[0].Throw();
		if (failures.Count > 1)
			throw new AggregateException(failures.Select(f => f.SourceException));
	}

	/// <summary>
	/// Checks that <paramref name="database"/> lists the same sessions as before, each with the same id, account and
	/// device (a <see cref="SessionInfo.Jid"/> carries the device), and none logged out; its state may differ, which a
	/// connection changes. The message gives counts, never a JID.
	/// </summary>
	public static void SameSessions(string database, ImmutableArray<SessionInfo> before, ImmutableArray<SessionInfo> after)
	{
		var was = Identities(before);
		var now = Identities(after);
		Assert.True(
			was.SequenceEqual(now),
			$"postflight_identity_changed: the {database} database lists {now.Length} sessions where it listed {was.Length}, or a session's id, account or device changed"
		);
		var loggedOut = after.Count(s => s.State is SessionState.LoggedOut);
		Assert.True(loggedOut == 0, $"postflight_logged_out: the {database} database has {loggedOut} logged out sessions");
	}

	private static ImmutableArray<(SessionId Id, Jid? Jid, Jid? Lid)> Identities(ImmutableArray<SessionInfo> sessions) =>
		[.. sessions.Select(s => (s.Id, s.Jid, s.Lid)).OrderBy(s => s.Id.Value, StringComparer.Ordinal)];
}
