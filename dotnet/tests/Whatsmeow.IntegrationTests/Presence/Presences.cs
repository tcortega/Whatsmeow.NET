using Google.Protobuf.WellKnownTypes;
using Whatsmeow.Events;
using Whatsmeow.IntegrationTests.Fake;
using Whatsmeow.IntegrationTests.Sessions;
using Whatsmeow.Presence;

namespace Whatsmeow.IntegrationTests.Presence;

/// <summary>What the Presence tests make the fake WhatsApp deliver, and the records the application then gets.</summary>
internal static class Presences
{
	/// <summary>How many ephemeral events the sidecar keeps per session: events.proto and the sidecar's ring size.</summary>
	public const int Kept = 256;

	/// <summary>The contact whose presence the tests see.</summary>
	public const string User = "5511888888888@s.whatsapp.net";

	/// <summary>Presence <paramref name="n"/> of <paramref name="user"/>: online, last seen n seconds after an instant, so each one is told apart.</summary>
	public static DeliverPresenceRequest Of(string session, int n, string user = User) =>
		new()
		{
			SessionId = session,
			From = user,
			LastSeen = Timestamp.FromDateTimeOffset(LastSeen(n)),
		};

	/// <summary>What the application gets for <see cref="Of"/>.</summary>
	public static PresenceChanged Changed(string session, int n, string user = User) =>
		new()
		{
			SessionId = SessionId.From(session),
			From = Jid.Parse(user),
			State = PresenceState.Available,
			LastSeen = LastSeen(n),
		};

	public static DateTimeOffset LastSeen(int n) => DateTimeOffset.FromUnixTimeSeconds(1_700_000_000 + n);

	/// <summary>A linked session with the push name that the app state sync sets, connected once WhatsApp accepted the login.</summary>
	public static async Task<IWhatsAppClient> OnlineAsync(FakeSidecar fixture, string id, CancellationToken cancellationToken)
	{
		var client = await SessionSetup.ConnectedAsync(fixture, id, cancellationToken).ConfigureAwait(false);
		_ = await fixture.Fake.SetPushNameAsync(new SetPushNameRequest { SessionId = id, Name = "Ana" }, cancellationToken: cancellationToken).ConfigureAwait(false);
		return client;
	}

	public static async Task DeliverAsync(FakeWhatsAppService.FakeWhatsAppServiceClient fake, CancellationToken cancellationToken, params DeliverPresenceRequest[] presences)
	{
		foreach (var presence in presences)
			_ = await fake.DeliverPresenceAsync(presence, cancellationToken: cancellationToken).ConfigureAwait(false);
	}
}
