using Whatsmeow.History;
using Whatsmeow.IntegrationTests.Support;

namespace Whatsmeow.LiveTests;

/// <summary>
/// The live run's fresh companion of the peer's account (ADR 0060): a new database the run creates itself
/// (<see cref="LiveSettings.FreshDatabaseVariable"/>), in a sidecar of its own with the history options #42 records,
/// and the shared <see cref="FreshCompanion"/>, which proves, steps and logs out exactly as its fake-mode tests check.
/// <para>
/// The database is kept whatever happens: it holds the device's keys while it is linked, and the contacts, push names,
/// history notifications and outbox rows the session saw; SQLite's free pages and its WAL may keep rows after they were
/// deleted [inferred]. It is private (0600 in a 0700 directory) and deleted by hand.
/// </para>
/// </summary>
internal sealed class LiveFresh : IAsyncDisposable
{
	private readonly LiveLog _log;
	private readonly WhatsmeowSidecar _sidecar;

	private LiveFresh(LiveLog log, WhatsmeowSidecar sidecar, FreshCompanion companion)
	{
		_log = log;
		_sidecar = sidecar;
		Companion = companion;
	}

	/// <summary>#42's options for this pairing, as both pairings of 2026-11-06: a full sync limited to seven days.</summary>
	public static HistorySyncOptions History => new() { RequestFullSync = true, FullSyncDaysLimit = 7 };

	/// <summary>The fresh session, unpaired until the scan.</summary>
	public FreshCompanion Companion { get; }

	/// <summary>
	/// Creates the database, which must not exist, starts its sidecar with the record's logger provider, records the
	/// binary and the options, and opens the session on a database that has none.
	/// </summary>
	public static async Task<LiveFresh> StartAsync(LiveSidecar live, LiveHistoryBounds bounds, CancellationToken cancellationToken)
	{
		var database = Path.GetFullPath(LiveSettings.FreshDatabase);
		FreshCompanion.CreateExclusive(database);
		var binary = await live.BinaryAsync(cancellationToken).ConfigureAwait(false);
		var log = new LiveLog(Path.Combine(live.OutputDirectory, "fresh.log"));
		// The record's own provider, which the factory does not own: the record is closed after the sidecar stops.
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
			var id = SessionId.From($"peer-fresh-{LiveClock.Time.GetUtcNow():yyyyMMddHHmmss}");
			var companion = await FreshCompanion.OpenAsync(sidecar, id, LiveClock.Time, live.Write, cancellationToken).ConfigureAwait(false);
			return new LiveFresh(log, sidecar, companion);
		}
		catch
		{
			await sidecar.DisposeAsync().ConfigureAwait(false);
			log.Dispose();
			throw;
		}
	}

	/// <summary>Stops the sidecar: the session stays as the companion left it, and the database stays.</summary>
	public async ValueTask DisposeAsync()
	{
		Companion.Dispose();
		await _sidecar.DisposeAsync().ConfigureAwait(false);
		_log.Dispose();
	}

	private static string Sha256(string path)
	{
		using var stream = File.OpenRead(path);
		return Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(stream));
	}
}
