namespace Whatsmeow.IntegrationTests.Support;

/// <summary>
/// The live tests' backup of the live database (<see cref="StoreBackup"/>), which no live run can check for itself:
/// it runs here on fake-mode sidecars of its own, because the store's lock is the behaviour under test.
/// </summary>
public sealed class StoreBackupTests(FakeSidecar fixture, SidecarBinaries binaries)
{
	private const int Timeout = 120_000;

	private static readonly string[] s_suffixes = ["", "-wal", "-shm"];

	private static readonly DateTimeOffset s_at = new(2026, 11, 2, 12, 34, 56, TimeSpan.Zero);

	[Fact(Timeout = Timeout)]
	public async Task Take_WhileASidecarHoldsTheStore_RefusesAndCopiesNothing()
	{
		var ct = TestContext.Current.CancellationToken;
		using var logs = new LogCapture();
		var options = new WhatsmeowSidecarOptions { SidecarPath = fixture.Binary, DatabasePath = binaries.NewDatabase() };
		var backups = Path.Combine(binaries.NewDirectory(), "backup");

		await using var sidecar = await WhatsmeowSidecar.StartFakeWhatsAppAsync(options, logs.Factory, ct);

		_ = Assert.Throws<IOException>(() => StoreBackup.Take(options.DatabasePath, backups, "live-before", s_at));
		Assert.False(Directory.Exists(backups));
	}

	[Fact(Timeout = Timeout)]
	public async Task Take_AfterTheSidecarStopped_CopiesTheStoreOwnerOnly_AndLeavesItAsItWas()
	{
		var ct = TestContext.Current.CancellationToken;
		using var logs = new LogCapture();
		var options = new WhatsmeowSidecarOptions { SidecarPath = fixture.Binary, DatabasePath = binaries.NewDatabase() };
		var backups = Path.Combine(binaries.NewDirectory(), "backup");
		await using (var first = await WhatsmeowSidecar.StartFakeWhatsAppAsync(options, logs.Factory, ct))
			_ = await first.Sessions.GetOrCreateAsync(SessionId.From("backup"), ct);
		var files = s_suffixes.Select(suffix => options.DatabasePath + suffix).Where(File.Exists).ToList();
		var before = files.ToDictionary(file => file, File.ReadAllBytes, StringComparer.Ordinal);

		var backup = StoreBackup.Take(options.DatabasePath, backups, "live-before", s_at);

		Assert.Equal(Path.Combine(backups, "live-before-20261102123456"), backup);
		Assert.Contains(files, file => string.Equals(file, options.DatabasePath, StringComparison.Ordinal));
		foreach (var file in files)
		{
			var copy = Path.Combine(backup, Path.GetFileName(file));
			Assert.Equal(before[file], await File.ReadAllBytesAsync(copy, ct));
			Assert.Equal(before[file], await File.ReadAllBytesAsync(file, ct));
			if (!OperatingSystem.IsWindows())
				Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(copy));
		}

		if (!OperatingSystem.IsWindows())
		{
			Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(backups));
			Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(backup));
		}

		// The lock is released: the store opens again, and a second backup the same second is refused.
		await using (var second = await WhatsmeowSidecar.StartFakeWhatsAppAsync(options, logs.Factory, ct))
			Assert.Contains(await second.Sessions.ListAsync(ct), session => session.Id == SessionId.From("backup"));
		_ = Assert.Throws<IOException>(() => StoreBackup.Take(options.DatabasePath, backups, "live-before", s_at));
	}

	[Fact]
	public void Take_AMissingStore_IsRefused()
	{
		var database = binaries.NewDatabase();

		_ = Assert.Throws<FileNotFoundException>(() => StoreBackup.Take(database, Path.Combine(binaries.NewDirectory(), "backup"), "live-before", s_at));
	}
}
