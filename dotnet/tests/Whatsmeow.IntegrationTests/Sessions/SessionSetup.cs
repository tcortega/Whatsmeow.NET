using Whatsmeow.IntegrationTests.Fake;
using Whatsmeow.IntegrationTests.Support;
using Whatsmeow.Sessions;

namespace Whatsmeow.IntegrationTests.Sessions;

/// <summary>The paired sessions the Sessions tests start from, made through the public API and the fake WhatsApp.</summary>
internal static class SessionSetup
{
	/// <summary>A session the phone linked (<see cref="LinkDeviceRequest"/> stands in for pairing until #7): paired, Disconnected.</summary>
	public static Task<IWhatsAppClient> LinkedAsync(FakeSidecar fixture, string id, CancellationToken cancellationToken) =>
		LinkedAsync(fixture, id, lid: "", cancellationToken);

	/// <summary>
	/// A session the phone linked with <paramref name="lid"/>, a device LID, as WhatsApp links every device; empty for none.
	/// whatsmeow names the account by it in a LID chat, as when it stores a sent poll's secret.
	/// </summary>
	public static async Task<IWhatsAppClient> LinkedAsync(FakeSidecar fixture, string id, string lid, CancellationToken cancellationToken)
	{
		var client = await fixture.Sidecar.Sessions.GetOrCreateAsync(SessionId.From(id), cancellationToken).ConfigureAwait(false);
		_ = await fixture.Fake.LinkDeviceAsync(new LinkDeviceRequest { SessionId = id, Jid = Jid(id), Lid = lid }, cancellationToken: cancellationToken).ConfigureAwait(false);
		return client;
	}

	/// <summary>A linked session the application connected, once WhatsApp accepted the login.</summary>
	public static Task<IWhatsAppClient> ConnectedAsync(FakeSidecar fixture, string id, CancellationToken cancellationToken) =>
		ConnectedAsync(fixture, id, lid: "", cancellationToken);

	/// <summary>A session linked with <paramref name="lid"/> (<see cref="LinkedAsync(FakeSidecar, string, string, CancellationToken)"/>) the application connected.</summary>
	public static async Task<IWhatsAppClient> ConnectedAsync(FakeSidecar fixture, string id, string lid, CancellationToken cancellationToken)
	{
		var client = await LinkedAsync(fixture, id, lid, cancellationToken).ConfigureAwait(false);
		await client.ConnectAsync(cancellationToken).ConfigureAwait(false);
		var watch = StateWatch.Start(client, cancellationToken);
		await using (watch.ConfigureAwait(false))
			await watch.ReachesAsync(new SessionState.Connected(), new SessionState.Connecting()).ConfigureAwait(false);
		return client;
	}

	/// <summary>The session's state now: the watch's first item.</summary>
	public static async Task<SessionState> StateAsync(IWhatsAppClient client, CancellationToken cancellationToken)
	{
		var watch = StateWatch.Start(client, cancellationToken);
		await using (watch.ConfigureAwait(false))
			return await watch.NextAsync().ConfigureAwait(false);
	}

	/// <summary>A device JID of its own for each session: whatsmeow keys devices by it.</summary>
	public static string Jid(string id) => $"{id}:1@s.whatsapp.net";
}
