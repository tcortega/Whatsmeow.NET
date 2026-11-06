using Google.Protobuf.WellKnownTypes;
using Whatsmeow.IntegrationTests.Support;
using Whatsmeow.Sessions;
using Raise = Whatsmeow.IntegrationTests.Fake.RaisePermanentDisconnectRequest;

namespace Whatsmeow.IntegrationTests.Sessions;

/// <summary>Each whatsmeow PermanentDisconnect at the pin reaches its state and the application (ARCHITECTURE §13 invariant 4).</summary>
public sealed class PermanentDisconnectTests(FakeSidecar fixture)
{
	private const int Timeout = 120_000;

#pragma warning disable RS0030 // Justification: the sidecar stamps a ban's end with its own wall clock, so the test bounds it with the real clock, not a fake one
	private static readonly TimeProvider s_wallClock = TimeProvider.System;
#pragma warning restore RS0030

	public static TheoryData<string> Kinds => ["logged-out", "replaced", "outdated", "cat-refresh", "banned", "connect-failure"];

	private static (Raise Request, SessionState State) Case(string kind) =>
		kind switch
		{
			"logged-out" => (new Raise { LoggedOut = new Raise.Types.LoggedOut { Reason = 401 } }, new SessionState.LoggedOut { Code = 401 }),
			"replaced" => (new Raise { StreamReplaced = new Raise.Types.StreamReplaced() }, new SessionState.Replaced()),
			"outdated" => (new Raise { ClientOutdated = new Raise.Types.ClientOutdated() }, new SessionState.Outdated()),
			"cat-refresh" => (new Raise { CatRefreshError = new Raise.Types.CatRefreshError { Message = "token expired" } }, new SessionState.Error { Code = 0, Message = "token expired" }),
			"banned" => (new Raise { TemporaryBan = new Raise.Types.TemporaryBan { Code = 101 } }, new SessionState.TemporarilyBanned { Code = 101, Until = null }),
			"connect-failure" => (new Raise { ConnectFailure = new Raise.Types.ConnectFailure { Reason = 418, Message = "unknown" } }, new SessionState.Error { Code = 418, Message = "unknown" }),
			_ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "not a case"),
		};

	[Theory(Timeout = Timeout)]
	[MemberData(nameof(Kinds))]
	public async Task PermanentDisconnect_ReachesItsState(string kind)
	{
		var ct = TestContext.Current.CancellationToken;
		var id = $"permanent-{kind}";
		var client = await SessionSetup.ConnectedAsync(fixture, id, ct);
		await using var watch = StateWatch.Start(client, ct);
		Assert.Equal(new SessionState.Connected(), await watch.NextAsync());
		var (request, state) = Case(kind);
		request.SessionId = id;

		_ = await fixture.Fake.RaisePermanentDisconnectAsync(request, cancellationToken: ct);

		Assert.Equal(state, await watch.NextAsync());
	}

	[Fact(Timeout = Timeout)]
	public async Task TemporaryBan_WithAnEnd_ReportsItAsADateTimeOffset()
	{
		var ct = TestContext.Current.CancellationToken;
		var client = await SessionSetup.ConnectedAsync(fixture, "ban-end", ct);
		await using var watch = StateWatch.Start(client, ct);
		Assert.Equal(new SessionState.Connected(), await watch.NextAsync());
		var ban = TimeSpan.FromHours(1);

		var before = s_wallClock.GetUtcNow();
		_ = await fixture.Fake.RaisePermanentDisconnectAsync(
			new Raise { SessionId = "ban-end", TemporaryBan = new Raise.Types.TemporaryBan { Code = 102, Expire = Duration.FromTimeSpan(ban) } },
			cancellationToken: ct
		);
		var state = Assert.IsType<SessionState.TemporarilyBanned>(await watch.NextAsync());
		var after = s_wallClock.GetUtcNow();

		// The sidecar's receive time plus the ban, kept to the second.
		Assert.Equal(102, state.Code);
		Assert.InRange(state.Until!.Value, before + ban - TimeSpan.FromSeconds(1), after + ban);
		Assert.Equal(TimeSpan.Zero, state.Until.Value.Offset);
	}

	[Fact(Timeout = Timeout)]
	public async Task Connect_DuringABanWithAnEnd_ThrowsTemporarilyBannedUntilThatEnd()
	{
		var ct = TestContext.Current.CancellationToken;
		var client = await SessionSetup.ConnectedAsync(fixture, "ban-connect", ct);
		await using var watch = StateWatch.Start(client, ct);
		Assert.Equal(new SessionState.Connected(), await watch.NextAsync());
		_ = await fixture.Fake.RaisePermanentDisconnectAsync(
			new Raise { SessionId = "ban-connect", TemporaryBan = new Raise.Types.TemporaryBan { Code = 101, Expire = Duration.FromTimeSpan(TimeSpan.FromHours(1)) } },
			cancellationToken: ct
		);
		var state = Assert.IsType<SessionState.TemporarilyBanned>(await watch.NextAsync());

		var error = await Assert.ThrowsAsync<TemporarilyBannedException>(() => client.ConnectAsync(ct));

		Assert.Equal((WhatsmeowErrorCode.TemporarilyBanned, state.Until), (error.ErrorCode, error.Until));
	}
}
