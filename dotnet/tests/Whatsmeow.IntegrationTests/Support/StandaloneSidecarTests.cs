namespace Whatsmeow.IntegrationTests.Support;

/// <summary>
/// The standalone sidecar's teardown, which the shared fixture's runs before it deletes its directory (#179). It starts
/// a sidecar of its own, because the stop is the behaviour under test.
/// </summary>
public sealed class StandaloneSidecarTests(SidecarBinaries binaries)
{
	private const int Timeout = 120_000;

	[Fact(Timeout = Timeout)]
	public async Task Dispose_TheBinaryCanBeDeletedAtOnce()
	{
		var ct = TestContext.Current.CancellationToken;
		var binary = binaries.Stage(binaries.Sidecar);
		if (!OperatingSystem.IsWindows())
			File.SetUnixFileMode(binary, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
		var standalone = await StandaloneSidecar.StartAsync(binary, binaries.NewDatabase(), "tcp:127.0.0.1:0", StandaloneSidecar.NewTokenFile(binaries.NewDirectory()), [], ct);

		await standalone.DisposeAsync();

		// On Windows a process's image stays mapped until the process is gone, and deleting it fails until then (#179).
		await Deletion.DeleteFileAsync(binary);
		Assert.False(File.Exists(binary));
	}
}
