namespace Whatsmeow.IntegrationTests.Sidecar;

/// <summary>The Sidecar slice's FullCycle on the shared fake-mode sidecar, the fixture every slice's FullCycle uses.</summary>
public sealed class GetInfoTests(FakeSidecar fixture, SidecarBinaries binaries)
{
	private const int Timeout = 120_000;

	[Fact(Timeout = Timeout)]
	public async Task FullCycle_GetInfo()
	{
		var ct = TestContext.Current.CancellationToken;

		Assert.Equal(binaries.Expected, await fixture.Sidecar.GetInfoAsync(ct));
	}
}
