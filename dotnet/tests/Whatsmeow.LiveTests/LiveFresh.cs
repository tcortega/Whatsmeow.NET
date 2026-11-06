using Whatsmeow.History;
using Whatsmeow.Sessions;

namespace Whatsmeow.LiveTests;

/// <summary>
/// A companion of the peer's account paired for one run (ADR 0060): a new database the run creates itself
/// (<see cref="LiveSettings.FreshDatabaseVariable"/>), in a sidecar of its own, with the history options #42 measures,
/// and a session id of its own. Its session is logged out and deleted only once <see cref="ProveAsync"/> showed the
/// account is the peer's, the device is neither the live nor the peer database's, and the database and session are this
/// run's; otherwise only its sidecar stops, and the database stays, for a person to look at. The database file is kept
/// either way: delete it by hand.
/// </summary>
internal sealed class LiveFresh : IAsyncDisposable
{
	/// <summary>What the logout, the delete, their checks and the record's close may take, each restore on its own bound.</summary>
	public static readonly TimeSpan CleanupBound = TimeSpan.FromMinutes(5);

	/// <summary>#42's options for this pairing, as both pairings of 2026-11-06: a full sync limited to seven days.</summary>
	public static HistorySyncOptions History => new() { RequestFullSync = true, FullSyncDaysLimit = 7 };

	/// <summary>What the sidecar and SQLite keep beside a database.</summary>
	private static readonly string[] s_beside = ["-wal", "-shm", ".lock", ".tmp"];

	private readonly LiveLog _log;
	private readonly WhatsmeowSidecar _sidecar;
	private bool _proven;

	private LiveFresh(LiveLog log, WhatsmeowSidecar sidecar, IWhatsAppClient client)
	{
		_log = log;
		_sidecar = sidecar;
		Client = client;
	}

	/// <summary>The fresh session's client, unpaired until the scan.</summary>
	public IWhatsAppClient Client { get; }

	/// <summary>
	/// Creates the database file, which must not exist, starts its sidecar with <paramref name="bounds"/> as a logger
	/// provider, records the binary and the options in it, and creates the session, on a database that has none.
	/// </summary>
	public static async Task<LiveFresh> StartAsync(LiveSidecar live, LiveHistoryBounds bounds, CancellationToken cancellationToken)
	{
		var database = Path.GetFullPath(LiveSettings.FreshDatabase);
		CreateExclusive(database);
		var binary = await live.BinaryAsync(cancellationToken).ConfigureAwait(false);
		var log = new LiveLog(Path.Combine(live.OutputDirectory, "fresh.log"));
		// The record's own provider, which the factory does not own: the test summarizes and closes the record after the
		// log, and the sidecar with it, are disposed.
		log.Capture.Factory.AddProvider(bounds.LoggerProvider);
		var options = new WhatsmeowSidecarOptions { SidecarPath = binary, DatabasePath = database, History = History };
		WhatsmeowSidecar sidecar;
		try
		{
			sidecar = await WhatsmeowSidecar.StartAsync(options, log.Capture.Factory, cancellationToken).ConfigureAwait(false);
		}
		catch
		{
			log.Dispose();
			throw;
		}

		try
		{
			var info = await sidecar.GetInfoAsync(cancellationToken).ConfigureAwait(false);
			bounds.Provenance(info, binary, "livetest");
			bounds.Options(options.History);
			live.Write($"live: fresh companion sidecar {info.Version}, whatsmeow {info.WhatsmeowVersion}, commit {info.Commit}, binary sha256 {Sha256(binary)}");

			// A database this run created has no session, so the id is the run's own.
			var before = await sidecar.Sessions.ListAsync(cancellationToken).ConfigureAwait(false);
			LiveChecks.Redacting(() => Assert.Empty(before));
			var id = SessionId.From($"peer-fresh-{LiveClock.Time.GetUtcNow():yyyyMMddHHmmss}");
			var client = await sidecar.Sessions.GetOrCreateAsync(id, cancellationToken).ConfigureAwait(false);
			return new LiveFresh(log, sidecar, client);
		}
		catch
		{
			await sidecar.DisposeAsync().ConfigureAwait(false);
			log.Dispose();
			throw;
		}
	}

	/// <summary>
	/// Null when the paired session is the peer account's, on a device that neither the live nor the peer database
	/// holds, and alone on this run's database; else which of these failed, with no identifier.
	/// </summary>
	public async Task<string?> ProveAsync(LiveSidecar live, LivePeer peer, CancellationToken cancellationToken)
	{
		var mine = await _sidecar.Sessions.ListAsync(cancellationToken).ConfigureAwait(false);
		if (mine is not [{ Jid: { } device } info] || info.Id != Client.SessionId)
			return "not_the_only_paired_session";

		LiveRedaction.Add(device);
		LiveRedaction.Add(info.Lid);
		var peerAccount = await peer.AccountAsync(cancellationToken).ConfigureAwait(false);
		var liveAccount = await live.AccountAsync(cancellationToken).ConfigureAwait(false);
		if (!LiveChecks.Names(peerAccount, device) || LiveChecks.Names(liveAccount, device))
			return "not_the_peer_account";

		var others = (await live.SessionsAsync(cancellationToken).ConfigureAwait(false)).AddRange(await peer.SessionsAsync(live, cancellationToken).ConfigureAwait(false));
		if (others.Any(o => o.Jid == device || (info.Lid is { } lid && o.Lid == lid)))
			return "not_a_new_device";

		_proven = true;
		return null;
	}

	/// <summary>
	/// Logs the proven session out and deletes it, then checks it is gone and that the live and peer sessions are still
	/// paired and not logged out. Refused unless <see cref="ProveAsync"/> proved the session.
	/// </summary>
	public async Task LogoutAsync(LiveSidecar live, LivePeer peer, CancellationToken cancellationToken)
	{
		if (!_proven)
			throw new InvalidOperationException("The fresh session is logged out only once it is proven the run's own (ADR 0060).");

		await LiveSidecar.DiscardAsync(Client, _sidecar, cancellationToken).ConfigureAwait(false);
		_ = await live.AccountAsync(cancellationToken).ConfigureAwait(false);
		_ = await peer.AccountAsync(cancellationToken).ConfigureAwait(false);
		var stillPaired = (await live.SessionsAsync(cancellationToken).ConfigureAwait(false))
			.Concat(await peer.SessionsAsync(live, cancellationToken).ConfigureAwait(false))
			.Where(s => s.Id.Value is LiveSidecar.LiveSessionId or LivePeer.SessionIdValue)
			.Select(s => s.State is SessionState.LoggedOut)
			.ToList();
		Assert.Equal([false, false], stillPaired);
	}

	/// <summary>Stops the sidecar: the session stays as it is, logged out or not, and the database stays.</summary>
	public async ValueTask DisposeAsync()
	{
		await _sidecar.DisposeAsync().ConfigureAwait(false);
		_log.Dispose();
	}

	/// <summary>
	/// Creates <paramref name="path"/> empty and owner-only in a single exclusive create, so it can be no existing file,
	/// store or link; its directory must be the owner's alone and no link, and nothing the sidecar keeps beside a
	/// database may exist yet (its <c>-wal</c>, <c>-shm</c>, <c>.lock</c> and <c>.tmp</c>).
	/// </summary>
	public static void CreateExclusive(string path)
	{
		var directory = new DirectoryInfo(Path.GetDirectoryName(path)!);
		if (!directory.Exists || directory.LinkTarget is not null)
			throw new InvalidOperationException($"{LiveSettings.FreshDatabaseVariable}'s directory must exist and not be a link.");
		if (!OperatingSystem.IsWindows() && (directory.UnixFileMode & ~(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute)) != UnixFileMode.None)
			throw new InvalidOperationException($"{LiveSettings.FreshDatabaseVariable}'s directory must be the owner's alone (0700).");
		if (s_beside.Any(suffix => Path.Exists(path + suffix)))
			throw new InvalidOperationException($"{LiveSettings.FreshDatabaseVariable} has a store's files beside it already.");

		var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
		if (!OperatingSystem.IsWindows())
			options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
		using var created = new FileStream(path, options);
	}

	private static string Sha256(string path)
	{
		using var stream = File.OpenRead(path);
		return Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(stream));
	}
}
