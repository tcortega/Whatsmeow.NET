using System.ComponentModel;
using System.Runtime.InteropServices;
using Whatsmeow.IntegrationTests.Support;

namespace Whatsmeow.IntegrationTests.Sidecar;

/// <summary>
/// The copy fallback for a binary that cannot run in place (ARCHITECTURE §8.5). The single declared platform
/// condition (tests.md): only root can mount noexec, so build/ci.sh provides the mount on linux-x64 CI, and there
/// these tests must run. They set XDG_DATA_HOME, so they share the non-parallel collection.
/// </summary>
[Collection(nameof(ProcessEnvironment))]
public sealed class NoexecTests(SidecarBinaries binaries)
{
	private const string CacheVariable = "XDG_DATA_HOME";

	[Fact(Timeout = 120_000)]
	public async Task Start_ABinaryOnANoexecMount_RunsACopy()
	{
		var ct = TestContext.Current.CancellationToken;
		using var logs = new LogCapture();
		var binary = OnNoexecMount();

		await using var sidecar = await WhatsmeowSidecar.StartAsync(new WhatsmeowSidecarOptions { SidecarPath = binary }, logs.Factory, ct);

		var copied = Assert.Single(logs.Named("SidecarCopied"));
		Assert.Equal(binary, copied.GetStructuredStateValue("Path"));
		Assert.Equal(binaries.Expected, await sidecar.GetInfoAsync(ct));
	}

	[Fact(Timeout = 120_000)]
	public async Task Start_ABinaryOnANoexecMountWithNoWritableCache_FailsWithTheInPlaceError()
	{
		var ct = TestContext.Current.CancellationToken;
		var binary = OnNoexecMount();

		// A file where the cache directory would go: creating the cache fails.
		var cache = Path.Combine(binaries.NewDirectory(), "not-a-directory");
		await File.WriteAllTextAsync(cache, "", ct);
		var previous = Environment.GetEnvironmentVariable(CacheVariable);
		Environment.SetEnvironmentVariable(CacheVariable, cache);
		try
		{
			var error = await Assert.ThrowsAsync<SidecarUnavailableException>(() => WhatsmeowSidecar.StartAsync(new WhatsmeowSidecarOptions { SidecarPath = binary }, ct));

			_ = Assert.IsType<Win32Exception>(Assert.IsType<SidecarUnavailableException>(error.InnerException).InnerException);
		}
		finally
		{
			Environment.SetEnvironmentVariable(CacheVariable, previous);
		}
	}

	/// <summary>A copy of the real sidecar on the noexec mount, executable but not runnable.</summary>
	private string OnNoexecMount()
	{
		var noexec = Environment.GetEnvironmentVariable("WHATSMEOW_NET_TEST_NOEXEC_DIR");
		if (noexec is null)
		{
			Assert.False(
				Environment.GetEnvironmentVariable("CI") == "true" && OperatingSystem.IsLinux() && RuntimeInformation.OSArchitecture is Architecture.X64,
				"WHATSMEOW_NET_TEST_NOEXEC_DIR must name a noexec mount on linux-x64 CI (build/ci.sh)"
			);
			Assert.Skip("needs a noexec mount in WHATSMEOW_NET_TEST_NOEXEC_DIR (build/ci.sh provides one on linux-x64 CI)");
		}

		var binary = Path.Combine(Directory.CreateDirectory(Path.Combine(noexec, Guid.NewGuid().ToString("N"))).FullName, SidecarBinaries.FileName);
		File.Copy(binaries.Sidecar, binary);
		return binary;
	}
}
