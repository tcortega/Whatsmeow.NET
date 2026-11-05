using Whatsmeow.IntegrationTests.Pairing;
using Whatsmeow.IntegrationTests.Sessions;
using Whatsmeow.IntegrationTests.Support;
using Whatsmeow.Messaging;
using Whatsmeow.Sessions;

namespace Whatsmeow.LiveTests;

/// <summary>
/// The peer session, <see cref="SessionIdValue"/>, on a second account: the other end of the live tests' traffic, so
/// they need no person. It sends the bursts to the live account and receives what the live session sends. Like the live
/// session it is persistent, paired once by the QR test on the database <see cref="LiveSettings.PeerDatabaseVariable"/>
/// names, never logged out or deleted, and copied only into the backup each run takes before its first start. It runs
/// in a sidecar process of its own, so killing the live sidecar never touches it; its log goes to <c>peer.log</c>,
/// redacted, beside the live one's.
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

	/// <summary>
	/// The peer session on its sidecar, paired or not, for the QR test (<c>LivePairingTests</c>), which pairs it the
	/// first time; it creates the session when the peer database has none.
	/// </summary>
	public async Task<IWhatsAppClient> SessionAsync(LiveSidecar live, CancellationToken cancellationToken)
	{
		var sidecar = await StartAsync(live, cancellationToken).ConfigureAwait(false);
		return await sidecar.Sessions.GetOrCreateAsync(SessionId.From(SessionIdValue), cancellationToken).ConfigureAwait(false);
	}

	/// <summary>The peer session, connected and CaughtUp. It must be paired already: the QR test pairs it.</summary>
	public async Task<IWhatsAppClient> ConnectedAsync(LiveSidecar live, CancellationToken cancellationToken)
	{
		if (_client is not null)
			return _client;

		// Checked before GetOrCreate, which would add an unpaired session to the peer's database.
		var sidecar = await StartAsync(live, cancellationToken).ConfigureAwait(false);
		_ = await LiveSidecar.AccountOfAsync(sidecar, SessionId.From(SessionIdValue), cancellationToken).ConfigureAwait(false);
		var client = await sidecar.Sessions.GetOrCreateAsync(SessionId.From(SessionIdValue), cancellationToken).ConfigureAwait(false);
		if (await SessionSetup.StateAsync(client, cancellationToken).ConfigureAwait(false) is not SessionState.CaughtUp)
		{
			PairingChecks.ValidateUsable(await LiveSidecar.ConnectAsync(client, cancellationToken).ConfigureAwait(false));
			_ = await LiveSidecar.StayUntilCaughtUpAsync(client, live.Write, cancellationToken).ConfigureAwait(false);
		}

		_client = client;
		return client;
	}

	/// <summary>
	/// Starts the peer's sidecar once, after the run's backup of its database, the one sanctioned copy of it
	/// (<see cref="StoreBackup"/>).
	/// </summary>
	private async Task<WhatsmeowSidecar> StartAsync(LiveSidecar live, CancellationToken cancellationToken)
	{
		await _starting.WaitAsync(cancellationToken).ConfigureAwait(false);
		try
		{
			if (_sidecar is not null)
				return _sidecar;

			// Never against a database by accident: only with the opt-in set (LiveFactAttribute.NeedsPeer).
			if (LiveSettings.PeerDatabase.Length == 0)
				throw new InvalidOperationException($"The peer session runs only with {LiveSettings.PeerDatabaseVariable} set.");

			var binary = await live.BinaryAsync(cancellationToken).ConfigureAwait(false);
			_log = new LiveLog(Path.Combine(live.OutputDirectory, "peer.log"));
			// The account's keys: owner-only, outside the repository (the sidecar creates the file 0600).
			var directory = Path.GetDirectoryName(Path.GetFullPath(LiveSettings.PeerDatabase))!;
			_ = OperatingSystem.IsWindows()
				? Directory.CreateDirectory(directory)
				: Directory.CreateDirectory(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
			if (File.Exists(LiveSettings.PeerDatabase))
				_log.Write(LiveClock.Stamp($"live: backed up the peer database as {Path.GetFileName(StoreBackup.Take(LiveSettings.PeerDatabase, LiveSettings.BackupDirectory, "peer", LiveClock.Time.GetUtcNow()))}"));

			var options = new WhatsmeowSidecarOptions { SidecarPath = binary, DatabasePath = LiveSettings.PeerDatabase };
			_sidecar = await WhatsmeowSidecar.StartAsync(options, _log.Capture.Factory, cancellationToken).ConfigureAwait(false);
			return _sidecar;
		}
		finally
		{
			_ = _starting.Release();
		}
	}

	/// <summary>The peer's account (<see cref="LiveSidecar.AccountOfAsync"/>), once <see cref="ConnectedAsync"/> started it.</summary>
	public Task<(Jid Chat, Jid? Lid)> AccountAsync(CancellationToken cancellationToken) =>
		LiveSidecar.AccountOfAsync(_sidecar ?? throw new InvalidOperationException("The peer is not started."), SessionId.From(SessionIdValue), cancellationToken);

	/// <summary>
	/// Sends each message to <paramref name="chat"/>, in order, through the paced send queue, and returns each result with
	/// the <see cref="LiveClock"/> timestamp of when its send returned.
	/// </summary>
	public static async Task<IReadOnlyList<(SendResult Result, long SentAt)>> SendAsync(
		IWhatsAppClient sender,
		Jid chat,
		IEnumerable<(string Text, MessageId Id)> messages,
		CancellationToken cancellationToken
	)
	{
		var results = new List<(SendResult, long)>();
		foreach (var (text, id) in messages)
			results.Add((await sender.SendTextAsync(chat, text, id, cancellationToken: cancellationToken).ConfigureAwait(false), LiveClock.Time.GetTimestamp()));
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
