using System.Globalization;

namespace Whatsmeow.IntegrationTests.Support;

/// <summary>
/// A copy of a sidecar's store, taken while no sidecar can write it: the live tests back up the live database before
/// the first start of a run (ADR 0032). It holds the store's lock (<c>&lt;database&gt;.lock</c>, which the sidecar takes
/// exclusively, ADR 0020) while it copies the database and its <c>-wal</c> and <c>-shm</c> as they are, so nothing is
/// checkpointed into the database, and releases it after.
/// </summary>
internal static class StoreBackup
{
	private const UnixFileMode OwnerOnlyDirectory = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
	private const UnixFileMode OwnerOnlyFile = UnixFileMode.UserRead | UnixFileMode.UserWrite;

	/// <summary>
	/// Copies the store into <c>&lt;backups&gt;/&lt;name&gt;-&lt;yyyyMMddHHmmss&gt;</c> (0700, its files 0600), creating
	/// <paramref name="backups"/> 0700 if it is missing, and returns that directory.
	/// </summary>
	/// <exception cref="FileNotFoundException">The database does not exist.</exception>
	/// <exception cref="IOException">A sidecar holds the store, or that backup directory exists already; nothing was copied.</exception>
	public static string Take(string database, string backups, string name, DateTimeOffset now)
	{
		if (!File.Exists(database))
			throw new FileNotFoundException("There is no store to back up.", database);

		// FileShare.None conflicts with the sidecar's lock: flock on Unix, an open handle on Windows (ADR 0020).
		using var held = new FileStream(database + ".lock", OwnerOnly(new FileStreamOptions { Mode = FileMode.OpenOrCreate, Access = FileAccess.ReadWrite, Share = FileShare.None }));

		var target = Path.Combine(backups, $"{name}-{now.ToOffset(TimeSpan.Zero).ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture)}");
		if (Directory.Exists(target))
			throw new IOException($"The backup {target} exists already.");
		CreateOwnerOnly(backups);
		CreateOwnerOnly(target);

		foreach (var file in new[] { database, database + "-wal", database + "-shm" }.Where(File.Exists))
		{
			using var source = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
			using var copy = new FileStream(Path.Combine(target, Path.GetFileName(file)), OwnerOnly(new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write }));
			source.CopyTo(copy);
			copy.Flush(flushToDisk: true);
		}

		return target;
	}

	/// <summary>A file these options create is 0600 on Unix.</summary>
	private static FileStreamOptions OwnerOnly(FileStreamOptions options)
	{
		if (!OperatingSystem.IsWindows())
			options.UnixCreateMode = OwnerOnlyFile;
		return options;
	}

	private static void CreateOwnerOnly(string directory)
	{
		if (OperatingSystem.IsWindows())
		{
			_ = Directory.CreateDirectory(directory);
			return;
		}

		_ = Directory.CreateDirectory(directory, OwnerOnlyDirectory);
		File.SetUnixFileMode(directory, OwnerOnlyDirectory);
	}
}
