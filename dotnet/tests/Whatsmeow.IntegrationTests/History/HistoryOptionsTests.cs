using Microsoft.Extensions.Options;
using Whatsmeow.IntegrationTests.Fake;
using Whatsmeow.IntegrationTests.Sessions;
using Whatsmeow.IntegrationTests.Support;

namespace Whatsmeow.IntegrationTests.History;

/// <summary>
/// The process-wide history options: the shared sidecar starts with <see cref="FakeSidecar.HistoryOptions"/>, and every
/// session paired on it sends them (ADR 0035).
/// </summary>
public sealed class HistoryOptionsTests(FakeSidecar fixture, SidecarBinaries binaries)
{
	private const int Timeout = 120_000;

	[Fact(Timeout = Timeout)]
	public async Task HistoryOptions_ApplyToEverySessionPairedAfterTheStart()
	{
		var ct = TestContext.Current.CancellationToken;
		var want = new GetPairingDevicePropsResponse
		{
			RequireFullSync = true,
			FullSyncDaysLimit = 30,
			FullSyncSizeMbLimit = 512,
			StorageQuotaMb = 2048,
		};

		foreach (var session in new[] { "history-options-1", "history-options-2" })
		{
			_ = await SessionSetup.LinkedAsync(fixture, session, ct);
			Assert.Equal(want, await fixture.Fake.GetPairingDevicePropsAsync(new GetPairingDevicePropsRequest { SessionId = session }, cancellationToken: ct));
		}
	}

	private static readonly Dictionary<string, Action<WhatsmeowSidecarOptions>> s_invalid = new(StringComparer.Ordinal)
	{
		["zero days"] = o => o.History.FullSyncDaysLimit = 0,
		["negative size"] = o => o.History.FullSyncSizeLimitMegabytes = -1,
		["zero quota"] = o => o.History.StorageQuotaMegabytes = 0,
		["no history options"] = o => o.History = null!,
	};

	public static TheoryData<string> Invalid => [.. s_invalid.Keys];

	[Theory(Timeout = 60_000)]
	[MemberData(nameof(Invalid))]
	public async Task Start_InvalidHistoryOptions_FailsBeforeSpawningAnything(string invalid)
	{
		var stub = binaries.Stage(binaries.HangingStub);
		var options = new WhatsmeowSidecarOptions { SidecarPath = stub };
		s_invalid[invalid](options);

		_ = await Assert.ThrowsAsync<OptionsValidationException>(() => WhatsmeowSidecar.StartAsync(options, TestContext.Current.CancellationToken));

		Assert.False(Processes.StubStarted(stub));
	}
}
