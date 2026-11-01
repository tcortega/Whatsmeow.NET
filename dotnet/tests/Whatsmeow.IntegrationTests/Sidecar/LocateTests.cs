using System.Runtime.InteropServices;

namespace Whatsmeow.IntegrationTests.Sidecar;

[CollectionDefinition(nameof(SidecarPathVariable), DisableParallelization = true)]
public sealed class SidecarPathVariable;

/// <summary>
/// Where the client looks for the binary (ARCHITECTURE §8.5). These tests set or need an unset
/// <c>WHATSMEOW_NET_SIDECAR_PATH</c>, so they never run in parallel with anything.
/// </summary>
[Collection(nameof(SidecarPathVariable))]
public sealed class LocateTests(SidecarBinaries binaries)
{
	private const string Variable = "WHATSMEOW_NET_SIDECAR_PATH";

	[Fact(Timeout = 60_000)]
	public async Task Start_TheConfiguredPathIsMissing_NamesIt()
	{
		var ct = TestContext.Current.CancellationToken;
		var path = Path.Combine(binaries.NewDirectory(), SidecarBinaries.FileName);

		var error = await Assert.ThrowsAsync<SidecarUnavailableException>(() => WhatsmeowSidecar.StartAsync(new WhatsmeowSidecarOptions { SidecarPath = path }, ct));

		Assert.Contains($"Paths tried: {path}.", error.Message, StringComparison.Ordinal);
	}

	[Fact(Timeout = 60_000)]
	public async Task Start_ThePathInTheEnvironmentIsMissing_NamesIt()
	{
		var ct = TestContext.Current.CancellationToken;
		var path = Path.Combine(binaries.NewDirectory(), SidecarBinaries.FileName);
		Environment.SetEnvironmentVariable(Variable, path);
		try
		{
			var error = await Assert.ThrowsAsync<SidecarUnavailableException>(() => WhatsmeowSidecar.StartAsync(new WhatsmeowSidecarOptions(), ct));

			Assert.Contains($"Paths tried: {path}.", error.Message, StringComparison.Ordinal);
		}
		finally
		{
			Environment.SetEnvironmentVariable(Variable, value: null);
		}
	}

	[Fact(Timeout = 60_000)]
	public async Task Start_NoPathAndNoBundledBinary_NamesBothBundledLocations()
	{
		var ct = TestContext.Current.CancellationToken;
		Assert.Null(Environment.GetEnvironmentVariable(Variable));
		var nextToTheApp = Path.Combine(AppContext.BaseDirectory, SidecarBinaries.FileName);
		var native = Path.Combine(AppContext.BaseDirectory, "runtimes", RuntimeInformation.RuntimeIdentifier, "native", SidecarBinaries.FileName);

		var error = await Assert.ThrowsAsync<SidecarUnavailableException>(() => WhatsmeowSidecar.StartAsync(new WhatsmeowSidecarOptions(), ct));

		Assert.Contains($"Paths tried: {nextToTheApp}, {native}.", error.Message, StringComparison.Ordinal);
	}
}
