using Whatsmeow.IntegrationTests.Fake;
using Whatsmeow.IntegrationTests.Support;
using Whatsmeow.Sessions;
using Raise = Whatsmeow.IntegrationTests.Fake.RaisePermanentDisconnectRequest;

namespace Whatsmeow.IntegrationTests.Sessions;

/// <summary>Sessions across a crash of the shared sidecar (ARCHITECTURE §7.4, §8.5).</summary>
[Collection(nameof(SidecarCrashes))]
public sealed class SessionRestartTests(FakeSidecar fixture)
{
	private const int Timeout = 120_000;

	[Fact(Timeout = Timeout)]
	public async Task SidecarRestart_KeepsTheStatesAndConnectsAgainOnlyTheDisconnectedOnesTheApplicationConnected()
	{
		var ct = TestContext.Current.CancellationToken;
		var created = await fixture.Sidecar.Sessions.GetOrCreateAsync(SessionId.From("restart-created"), ct);
		var linked = await SessionSetup.LinkedAsync(fixture, "restart-linked", ct);
		var connected = await SessionSetup.ConnectedAsync(fixture, "restart-connected", ct);
		var loggedOut = await SessionSetup.ConnectedAsync(fixture, "restart-loggedout", ct);
		var replaced = await SessionSetup.ConnectedAsync(fixture, "restart-replaced", ct);
		await RaiseAsync("restart-loggedout", new Raise { LoggedOut = new Raise.Types.LoggedOut { Reason = 401 } }, loggedOut, new SessionState.LoggedOut { Code = 401 });
		await RaiseAsync("restart-replaced", new Raise { StreamReplaced = new Raise.Types.StreamReplaced() }, replaced, new SessionState.Replaced());

		await fixture.CrashAsync(ct);

		// CrashAsync returns after the client's reconnects: a session connected again is Connecting or Connected by now.
		Assert.Equal(new SessionState.Created(), await SessionSetup.StateAsync(created, ct));
		Assert.Equal(new SessionState.Disconnected(), await SessionSetup.StateAsync(linked, ct));
		Assert.Equal(new SessionState.LoggedOut { Code = 401 }, await SessionSetup.StateAsync(loggedOut, ct));
		Assert.Equal(new SessionState.Replaced(), await SessionSetup.StateAsync(replaced, ct));
		await using var watch = StateWatch.Start(connected, ct);
		await watch.ReachesAsync(new SessionState.Connected(), new SessionState.Connecting(), new SessionState.Disconnected());
	}

	[Fact(Timeout = Timeout)]
	public async Task SidecarRestart_ConnectsAgainEverySessionTheApplicationConnected()
	{
		var ct = TestContext.Current.CancellationToken;
		string[] ids = ["restart-many-1", "restart-many-2", "restart-many-3"];
		var clients = new List<IWhatsAppClient>();
		foreach (var id in ids)
			clients.Add(await SessionSetup.ConnectedAsync(fixture, id, ct));

		await fixture.CrashAsync(ct);

		// One ConnectSessions call for all of them (#77): each is connected again, and none failed.
		foreach (var client in clients)
		{
			await using var watch = StateWatch.Start(client, ct);
			await watch.ReachesAsync(new SessionState.Connected(), new SessionState.Connecting());
		}

		Assert.DoesNotContain(fixture.Logs.Named("ReconnectRefused").Concat(fixture.Logs.Named("ReconnectFailed")), r => ids.Contains(r.GetStructuredStateValue("SessionId")));
	}

	[Fact(Timeout = Timeout)]
	public async Task SidecarRestart_AfterALogoutWithNoAnswer_ConnectsTheSessionAgain()
	{
		var ct = TestContext.Current.CancellationToken;
		var client = await SessionSetup.ConnectedAsync(fixture, "restart-logout-unknown", ct);
		_ = await fixture.Fake.FailNextLogoutAsync(new FailNextLogoutRequest { SessionId = "restart-logout-unknown" }, cancellationToken: ct);

		var error = await Assert.ThrowsAsync<OutcomeUnknownException>(() => client.LogoutAsync(ct));
		Assert.Equal(WhatsmeowErrorCode.OutcomeUnknown, error.ErrorCode);

		await fixture.CrashAsync(ct);

		// The logout did not happen, so the session is still one the application connected.
		await using var watch = StateWatch.Start(client, ct);
		await watch.ReachesAsync(new SessionState.Connected(), new SessionState.Connecting());
	}

	[Fact(Timeout = Timeout)]
	public async Task Watch_TheSidecarRestarts_GoesOnWithTheCurrentStateFirst()
	{
		var ct = TestContext.Current.CancellationToken;
		var client = await SessionSetup.LinkedAsync(fixture, "restart-watch", ct);
		await using var watch = StateWatch.Start(client, ct);
		Assert.Equal(new SessionState.Disconnected(), await watch.NextAsync());

		await fixture.CrashAsync(ct);

		// The restarted sidecar's current state, then its changes on the same watch.
		Assert.Equal(new SessionState.Disconnected(), await watch.NextAsync());
		await client.ConnectAsync(ct);
		await watch.ReachesAsync(new SessionState.Connected(), new SessionState.Connecting());
	}

	private async Task RaiseAsync(string id, Raise request, IWhatsAppClient client, SessionState state)
	{
		var ct = TestContext.Current.CancellationToken;
		var watch = StateWatch.Start(client, ct);
		await using (watch.ConfigureAwait(false))
		{
			Assert.Equal(new SessionState.Connected(), await watch.NextAsync().ConfigureAwait(false));
			request.SessionId = id;
			_ = await fixture.Fake.RaisePermanentDisconnectAsync(request, cancellationToken: ct).ResponseAsync.ConfigureAwait(false);
			Assert.Equal(state, await watch.NextAsync().ConfigureAwait(false));
		}
	}
}
