using Whatsmeow.IntegrationTests.Sidecar;

namespace Whatsmeow.IntegrationTests.Sessions;

/// <summary>One sidecar per database file (ARCHITECTURE §7.4); these start sidecars of their own, because starting is the behaviour.</summary>
public sealed class DatabaseTests(FakeSidecar fixture, SidecarBinaries binaries)
{
	private const int Timeout = 120_000;

	[Fact(Timeout = Timeout)]
	public async Task Start_ADatabaseAnotherSidecarUses_FailsClearly()
	{
		var ct = TestContext.Current.CancellationToken;
		var options = new WhatsmeowSidecarOptions { SidecarPath = binaries.Stage(binaries.Sidecar), DatabasePath = fixture.DatabasePath };

		var error = await Assert.ThrowsAsync<SidecarUnavailableException>(() => WhatsmeowSidecar.StartAsync(options, ct));

		Assert.Contains($"the database {fixture.DatabasePath}: lock {fixture.DatabasePath}.lock: in use by another whatsmeow-net", error.Message, StringComparison.Ordinal);
	}
}

/// <summary>The default database file; it changes the current directory, so it never runs in parallel.</summary>
[Collection(nameof(ProcessEnvironment))]
public sealed class DefaultDatabaseTests(SidecarBinaries binaries)
{
	[Fact(Timeout = 120_000)]
	public async Task Start_WithoutADatabasePath_UsesWhatsmeowNetDbInTheCurrentDirectory()
	{
		var ct = TestContext.Current.CancellationToken;
		var directory = binaries.NewDirectory();
		var previous = Environment.CurrentDirectory;
		Environment.CurrentDirectory = directory;
		try
		{
			await using var sidecar = await WhatsmeowSidecar.StartAsync(new WhatsmeowSidecarOptions { SidecarPath = binaries.Stage(binaries.Sidecar) }, ct);

			// The full path is resolved at start: a later change of directory does not move it.
			Environment.CurrentDirectory = previous;
			_ = await sidecar.Sessions.GetOrCreateAsync(SessionId.From("default-database"), ct);
			Assert.True(File.Exists(Path.Combine(directory, "whatsmeow-net.db.lock")), "no whatsmeow-net.db.lock in the current directory");
			Assert.True(File.Exists(Path.Combine(directory, "whatsmeow-net.db")), "no whatsmeow-net.db in the current directory");
		}
		finally
		{
			Environment.CurrentDirectory = previous;
		}
	}
}
