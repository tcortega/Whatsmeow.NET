using Whatsmeow.IntegrationTests.Pairing;
using Whatsmeow.IntegrationTests.Sessions;
using Whatsmeow.Messaging;
using Whatsmeow.Sessions;

namespace Whatsmeow.LiveTests;

/// <summary>
/// The peer session, <see cref="SessionIdValue"/>, on a second account: the other end of the live tests' traffic, so
/// they need no person. It sends the bursts to the live account and receives what the live session sends. Like the live
/// session it is persistent, paired once on the database <see cref="LiveSettings.PeerDatabaseVariable"/> names, and
/// never logged out, deleted or copied. It runs in a sidecar process of its own, so killing the live sidecar never
/// touches it; its log goes to <c>peer.log</c>, redacted, beside the live one's.
/// </summary>
public sealed class LivePeer : IAsyncLifetime
{
	/// <summary>The peer session's id.</summary>
	public const string SessionIdValue = "peer";

	private readonly SemaphoreSlim _starting = new(1, 1);
	private LiveLog? _log;
	private WhatsmeowSidecar? _sidecar;
	private IWhatsAppClient? _client;

	public ValueTask InitializeAsync() => ValueTask.CompletedTask;

	/// <summary>The peer session, connected and CaughtUp. It must be paired already.</summary>
	public async Task<IWhatsAppClient> ConnectedAsync(LiveSidecar live, CancellationToken cancellationToken)
	{
		await _starting.WaitAsync(cancellationToken).ConfigureAwait(false);
		try
		{
			if (_client is not null)
				return _client;

			// Never against a database by accident: only with the opt-in set (LiveFactAttribute.NeedsPeer).
			if (LiveSettings.PeerDatabase.Length == 0)
				throw new InvalidOperationException($"The peer session runs only with {LiveSettings.PeerDatabaseVariable} set.");

			var binary = await live.BinaryAsync(cancellationToken).ConfigureAwait(false);
			_log = new LiveLog(Path.Combine(live.OutputDirectory, "peer.log"));
			var options = new WhatsmeowSidecarOptions { SidecarPath = binary, DatabasePath = LiveSettings.PeerDatabase };
			_sidecar = await WhatsmeowSidecar.StartAsync(options, _log.Capture.Factory, cancellationToken).ConfigureAwait(false);

			// Checked before GetOrCreate, which would add an unpaired session to the peer's database.
			_ = await LiveSidecar.AccountOfAsync(_sidecar, SessionId.From(SessionIdValue), cancellationToken).ConfigureAwait(false);
			var client = await _sidecar.Sessions.GetOrCreateAsync(SessionId.From(SessionIdValue), cancellationToken).ConfigureAwait(false);
			if (await SessionSetup.StateAsync(client, cancellationToken).ConfigureAwait(false) is not SessionState.CaughtUp)
			{
				PairingChecks.ValidateUsable(await LiveSidecar.ConnectAsync(client, cancellationToken).ConfigureAwait(false));
				_ = await LiveSidecar.StayUntilCaughtUpAsync(client, live.Write, cancellationToken).ConfigureAwait(false);
			}

			_client = client;
			return client;
		}
		finally
		{
			_ = _starting.Release();
		}
	}

	/// <summary>The peer's account (<see cref="LiveSidecar.AccountOfAsync"/>), once <see cref="ConnectedAsync"/> started it.</summary>
	public Task<(Jid Chat, Jid? Lid)> AccountAsync(CancellationToken cancellationToken) =>
		LiveSidecar.AccountOfAsync(_sidecar ?? throw new InvalidOperationException("The peer is not started."), SessionId.From(SessionIdValue), cancellationToken);

	/// <summary>Sends each message of the burst to <paramref name="chat"/>, in order, through the paced send queue.</summary>
	public static async Task<IReadOnlyList<SendResult>> SendAsync(IWhatsAppClient sender, Jid chat, Burst burst, CancellationToken cancellationToken)
	{
		var results = new List<SendResult>(burst.Messages.Count);
		foreach (var (text, id) in burst.Messages)
			results.Add(await sender.SendTextAsync(chat, text, id, cancellationToken).ConfigureAwait(false));
		return results;
	}

	public async ValueTask DisposeAsync()
	{
		// The session stays paired: only its sidecar stops.
		if (_sidecar is not null)
			await _sidecar.DisposeAsync().ConfigureAwait(false);
		_log?.Dispose();
		_starting.Dispose();
	}
}
