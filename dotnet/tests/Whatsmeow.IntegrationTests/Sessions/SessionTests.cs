using Whatsmeow.IntegrationTests.Fake;
using Whatsmeow.IntegrationTests.Support;
using Whatsmeow.Sessions;

namespace Whatsmeow.IntegrationTests.Sessions;

/// <summary>The Sessions slice through the public API on the shared fake-mode sidecar; each test owns its session ids.</summary>
public sealed class SessionTests(FakeSidecar fixture)
{
	private const int Timeout = 120_000;

	private WhatsmeowSessions Sessions => fixture.Sidecar.Sessions;

	[Fact(Timeout = Timeout)]
	public async Task FullCycle_CreateLinkConnectDisconnectLogoutDelete()
	{
		var ct = TestContext.Current.CancellationToken;
		var id = SessionId.From("fullcycle");

		var client = await Sessions.GetOrCreateAsync(id, ct);
		await using var watch = StateWatch.Start(client, ct);
		Assert.Equal(new SessionState.Created(), await watch.NextAsync());

		_ = await fixture.Fake.LinkDeviceAsync(
			new LinkDeviceRequest { SessionId = "fullcycle", Jid = SessionSetup.Jid("fullcycle"), Lid = "fullcycle:1@lid" },
			cancellationToken: ct
		);
		Assert.Equal(new SessionState.Disconnected(), await watch.NextAsync());

		// This device's JIDs, with the device part, as WhatsApp assigned them at the link.
		var paired = Assert.Single(await Sessions.ListAsync(ct), s => s.Id == id);
		Assert.Equal(
			new SessionInfo
			{
				Id = id,
				State = new SessionState.Disconnected(),
				Jid = Jid.Parse("fullcycle:1@s.whatsapp.net"),
				Lid = Jid.Parse("fullcycle:1@lid"),
			},
			paired
		);

		await client.ConnectAsync(ct);
		await watch.ReachesAsync(new SessionState.Connected(), new SessionState.Connecting());

		await client.DisconnectAsync(ct);
		Assert.Equal(new SessionState.Disconnected(), await watch.NextAsync());

		await client.ConnectAsync(ct);
		await watch.ReachesAsync(new SessionState.Connected(), new SessionState.Connecting());

		await client.LogoutAsync(ct);
		Assert.Equal(new SessionState.LoggedOut { Code = 0 }, await watch.NextAsync());
		Assert.Equal([new SessionInfo { Id = id, State = new SessionState.LoggedOut { Code = 0 } }], (await Sessions.ListAsync(ct)).Where(s => s.Id == id));

		await Sessions.DeleteAsync(id, ct);
		_ = await watch.EndsWithAsync<SessionNotFoundException>();
		Assert.DoesNotContain(await Sessions.ListAsync(ct), s => s.Id == id);
	}

	[Fact(Timeout = Timeout)]
	public async Task GetOrCreate_Twice_ReturnsTheSameSession()
	{
		var ct = TestContext.Current.CancellationToken;
		var first = await SessionSetup.LinkedAsync(fixture, "twice", ct);

		var second = await Sessions.GetOrCreateAsync(SessionId.From("twice"), ct);

		Assert.Same(first, second);
		Assert.Equal(new SessionState.Disconnected(), await SessionSetup.StateAsync(second, ct));
	}

	[Fact(Timeout = Timeout)]
	public async Task List_ReturnsEverySession()
	{
		var ct = TestContext.Current.CancellationToken;
		_ = await Sessions.GetOrCreateAsync(SessionId.From("list-b"), ct);
		_ = await SessionSetup.LinkedAsync(fixture, "list-a", ct);
		_ = await Sessions.GetOrCreateAsync(SessionId.From("list-c"), ct);

		var listed = (await Sessions.ListAsync(ct)).Where(s => s.Id.Value.StartsWith("list-", StringComparison.Ordinal));

		Assert.Equal(
			[
				new SessionInfo { Id = SessionId.From("list-a"), State = new SessionState.Disconnected(), Jid = Jid.Parse(SessionSetup.Jid("list-a")) },
				new SessionInfo { Id = SessionId.From("list-b"), State = new SessionState.Created() },
				new SessionInfo { Id = SessionId.From("list-c"), State = new SessionState.Created() },
			],
			listed
		);
	}

	[Fact(Timeout = Timeout)]
	public async Task Delete_RemovesTheSessionAndItsData()
	{
		var ct = TestContext.Current.CancellationToken;
		var id = SessionId.From("delete");
		_ = await SessionSetup.LinkedAsync(fixture, "delete", ct);

		await Sessions.DeleteAsync(id, ct);

		Assert.DoesNotContain(await Sessions.ListAsync(ct), s => s.Id == id);

		// The same id is a new, unpaired session. That the device rows are deleted is checked in the sidecar's tests
		// (TestDeleteSessionDisconnectsDeletesTheDeviceAndEndsWatches).
		var again = await Sessions.GetOrCreateAsync(id, ct);
		Assert.Equal(new SessionState.Created(), await SessionSetup.StateAsync(again, ct));
	}

	[Fact(Timeout = Timeout)]
	public async Task Delete_AConnectedSession_DisconnectsItFirst()
	{
		var ct = TestContext.Current.CancellationToken;
		_ = await SessionSetup.ConnectedAsync(fixture, "delete-connected", ct);

		await Sessions.DeleteAsync(SessionId.From("delete-connected"), ct);

		var sent = await fixture.Fake.WaitForSentAsync(new WaitForSentRequest { SessionId = "delete-connected" }, cancellationToken: ct);
		Assert.Equal(new Sent { Index = sent.Sent.Index, SessionId = "delete-connected", Disconnect = new Disconnect() }, sent.Sent);
	}

	[Fact(Timeout = Timeout)]
	public async Task Watch_ADeletedSession_ThrowsSessionNotFound()
	{
		var ct = TestContext.Current.CancellationToken;
		var client = await Sessions.GetOrCreateAsync(SessionId.From("watch-deleted"), ct);
		await Sessions.DeleteAsync(SessionId.From("watch-deleted"), ct);

		await using var watch = StateWatch.Start(client, ct);

		var error = await watch.EndsWithAsync<SessionNotFoundException>();
		Assert.Equal(WhatsmeowErrorCode.SessionNotFound, error.ErrorCode);
	}

	[Fact(Timeout = Timeout)]
	public async Task Watch_TheSessionIsDeleted_EndsWithSessionNotFound()
	{
		var ct = TestContext.Current.CancellationToken;
		var client = await Sessions.GetOrCreateAsync(SessionId.From("watch-deleting"), ct);
		await using var watch = StateWatch.Start(client, ct);
		Assert.Equal(new SessionState.Created(), await watch.NextAsync());

		await Sessions.DeleteAsync(SessionId.From("watch-deleting"), ct);

		_ = await watch.EndsWithAsync<SessionNotFoundException>();
	}

	[Fact(Timeout = Timeout)]
	public async Task Connect_NeverPaired_ThrowsNotLoggedIn()
	{
		var ct = TestContext.Current.CancellationToken;
		var client = await Sessions.GetOrCreateAsync(SessionId.From("connect-unpaired"), ct);

		var error = await Assert.ThrowsAsync<NotLoggedInException>(() => client.ConnectAsync(ct));

		Assert.Equal(WhatsmeowErrorCode.NotLoggedIn, error.ErrorCode);
	}

	[Fact(Timeout = Timeout)]
	public async Task Logout_NeverPaired_ThrowsNotLoggedIn()
	{
		var ct = TestContext.Current.CancellationToken;
		var client = await Sessions.GetOrCreateAsync(SessionId.From("logout-unpaired"), ct);

		var error = await Assert.ThrowsAsync<NotLoggedInException>(() => client.LogoutAsync(ct));

		Assert.Equal(WhatsmeowErrorCode.NotLoggedIn, error.ErrorCode);
	}

	[Fact(Timeout = Timeout)]
	public async Task Connect_LoggedOut_ThrowsSessionLoggedOut()
	{
		var ct = TestContext.Current.CancellationToken;
		var client = await SessionSetup.ConnectedAsync(fixture, "connect-loggedout", ct);
		await client.LogoutAsync(ct);

		var error = await Assert.ThrowsAsync<SessionLoggedOutException>(() => client.ConnectAsync(ct));

		Assert.Equal(WhatsmeowErrorCode.LoggedOut, error.ErrorCode);
	}

	[Fact(Timeout = Timeout)]
	public async Task Logout_NotConnected_ThrowsNotConnected()
	{
		var ct = TestContext.Current.CancellationToken;
		var client = await SessionSetup.LinkedAsync(fixture, "logout-disconnected", ct);

		var error = await Assert.ThrowsAsync<NotConnectedException>(() => client.LogoutAsync(ct));

		Assert.Equal(WhatsmeowErrorCode.NotConnected, error.ErrorCode);
	}

	[Fact(Timeout = Timeout)]
	public async Task Connect_TheConnectionFails_ThrowsNotConnectedAndKeepsTheState()
	{
		var ct = TestContext.Current.CancellationToken;
		var client = await SessionSetup.LinkedAsync(fixture, "connect-fails", ct);
		_ = await fixture.Fake.FailNextConnectAsync(new FailNextConnectRequest { SessionId = "connect-fails" }, cancellationToken: ct);

		var error = await Assert.ThrowsAsync<NotConnectedException>(() => client.ConnectAsync(ct));

		Assert.Equal(WhatsmeowErrorCode.ConnectionFailed, error.ErrorCode);
		Assert.Equal(new SessionState.Disconnected(), await SessionSetup.StateAsync(client, ct));
	}

	[Fact(Timeout = Timeout)]
	public async Task Disconnect_Connected_BecomesDisconnected()
	{
		var ct = TestContext.Current.CancellationToken;
		var client = await SessionSetup.ConnectedAsync(fixture, "disconnect", ct);
		await using var watch = StateWatch.Start(client, ct);
		Assert.Equal(new SessionState.Connected(), await watch.NextAsync());

		await client.DisconnectAsync(ct);

		Assert.Equal(new SessionState.Disconnected(), await watch.NextAsync());
		var sent = await fixture.Fake.WaitForSentAsync(new WaitForSentRequest { SessionId = "disconnect" }, cancellationToken: ct);
		Assert.Equal(new Sent { Index = sent.Sent.Index, SessionId = "disconnect", Disconnect = new Disconnect() }, sent.Sent);
	}

	[Fact(Timeout = Timeout)]
	public async Task Logout_Connected_UnlinksOnWhatsAppAndBecomesLoggedOut()
	{
		var ct = TestContext.Current.CancellationToken;
		var client = await SessionSetup.ConnectedAsync(fixture, "logout", ct);
		await using var watch = StateWatch.Start(client, ct);
		Assert.Equal(new SessionState.Connected(), await watch.NextAsync());

		await client.LogoutAsync(ct);

		Assert.Equal(new SessionState.LoggedOut { Code = 0 }, await watch.NextAsync());
		var sent = await fixture.Fake.WaitForSentAsync(new WaitForSentRequest { SessionId = "logout" }, cancellationToken: ct);
		Assert.Equal(new Sent { Index = sent.Sent.Index, SessionId = "logout", RemoveCompanionDevice = new RemoveCompanionDevice() }, sent.Sent);
	}

	public static TheoryData<string> InvalidIds => ["", "-starts-with-a-dash", ".dot", "has space", "slash/", "é", new string('a', 65)];

	[Theory]
	[MemberData(nameof(InvalidIds))]
	public void SessionId_BreaksTheRule_ThrowsValidation(string value) =>
		_ = Assert.Throws<WhatsmeowValidationException>(() => SessionId.From(value));

	[Fact]
	public void SessionId_TheLongestAndEveryAllowedCharacter_IsValid() =>
		Assert.Equal(
			(new string('a', 64), "0Az.9_-"),
			(SessionId.From(new string('a', 64)).Value, SessionId.From("0Az.9_-").Value)
		);
}
