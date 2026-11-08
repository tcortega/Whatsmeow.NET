using Microsoft.Extensions.Time.Testing;
using Whatsmeow.Sessions;
using Xunit.Sdk;

namespace Whatsmeow.IntegrationTests.Support;

/// <summary>
/// The live tests' after-body safety checks (<see cref="AfterChecks"/>, ADR 0062): every check runs on its own bound
/// whatever the body and the other checks did, and the body's failure is thrown with theirs; a database's sessions must
/// keep their ids, accounts and devices, and none may be logged out.
/// </summary>
public sealed class AfterChecksTests
{
	private static readonly Jid s_device = Jid.Parse("5511986000001:7@s.whatsapp.net");
	private static readonly Jid s_lid = Jid.Parse("986000001:7@lid");

	[Fact]
	public async Task RunAsync_TheBodyAndACheckFail_RunsEveryCheck_AndThrowsBothWithTheBodyFirst()
	{
		var lines = new List<string>();
		var ran = new List<string>();

		var failed = await Assert.ThrowsAsync<AggregateException>(() =>
			AfterChecks.RunAsync(
				() => throw new InvalidOperationException("the body"),
				TimeSpan.FromSeconds(30),
				new FakeTimeProvider(),
				lines.Add,
				("first check", _ => throw new TimeoutException("the first check")),
				(
					"second check",
					_ =>
					{
						ran.Add("second");
						return Task.CompletedTask;
					}
		)
			)
		);

		Assert.Equal(["the body", "the first check"], failed.InnerExceptions.Select(e => e.Message), StringComparer.Ordinal);
		Assert.Equal(["second"], ran, StringComparer.Ordinal);
		Assert.Equal(["first check: failed, TimeoutException", "second check: passed"], lines, StringComparer.Ordinal);
	}

	[Fact(Timeout = 30_000)]
	public async Task RunAsync_ACheckNeverEnds_ItsBoundEndsIt_AndTheNextCheckStillRuns()
	{
		var ct = TestContext.Current.CancellationToken;
		var lines = new List<string>();

		var failed = await Assert.ThrowsAsync<TimeoutException>(() =>
			AfterChecks.RunAsync(
				() => Task.CompletedTask,
				TimeSpan.Zero,
				new FakeTimeProvider(),
				lines.Add,
				// Ignores its bounded token; only the test's ends it, should the bound not.
				("hanging check", _ => new TaskCompletionSource().Task.WaitAsync(ct)),
				("next check", _ => Task.CompletedTask)
			)
		);

		Assert.NotNull(failed);
		Assert.Equal(["hanging check: failed, TimeoutException", "next check: passed"], lines, StringComparer.Ordinal);
	}

	[Fact]
	public void SameSessions_OnlyTheStateChanged_Passes() =>
		AfterChecks.SameSessions("live", [Session(new SessionState.Disconnected(), s_device)], [Session(new SessionState.CaughtUp(), s_device)]);

	[Fact]
	public void SameSessions_TheDeviceChanged_Fails_NamingNoJid()
	{
		var failed = Assert.Throws<TrueException>(() =>
			AfterChecks.SameSessions("live", [Session(new SessionState.Disconnected(), s_device)], [Session(new SessionState.CaughtUp(), s_device with { Device = 8 })])
		);

		Assert.StartsWith("postflight_identity_changed: the live database", failed.Message, StringComparison.Ordinal);
		Assert.DoesNotContain("986000001", failed.Message, StringComparison.Ordinal);
	}

	[Fact]
	public void SameSessions_ASessionWasAdded_Fails() =>
		Assert.StartsWith(
			"postflight_identity_changed",
			Assert
				.Throws<TrueException>(() =>
					AfterChecks.SameSessions(
						"peer",
						[Session(new SessionState.Disconnected(), s_device)],
						[Session(new SessionState.Disconnected(), s_device), new SessionInfo { Id = SessionId.From("other"), State = new SessionState.Created() }]
					)
				)
				.Message,
			StringComparison.Ordinal
		);

	[Fact]
	public void SameSessions_TheSessionIsLoggedOut_Fails()
	{
		var failed = Assert.Throws<TrueException>(() =>
			AfterChecks.SameSessions("peer", [Session(new SessionState.Disconnected(), s_device)], [Session(new SessionState.LoggedOut { Code = 401 }, s_device)])
		);

		Assert.StartsWith("postflight_logged_out: the peer database", failed.Message, StringComparison.Ordinal);
	}

	private static SessionInfo Session(SessionState state, Jid device) =>
		new()
		{
			Id = SessionId.From("live"),
			State = state,
			Jid = device,
			Lid = s_lid,
		};
}
