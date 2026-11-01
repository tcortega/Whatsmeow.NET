using Microsoft.Extensions.Options;
using Whatsmeow.IntegrationTests.Support;

namespace Whatsmeow.IntegrationTests.Sidecar;

/// <summary>Invalid options fail the start before anything is spawned (docs/guidelines/options.md).</summary>
public sealed class OptionsTests(SidecarBinaries binaries)
{
	private static readonly Dictionary<string, Action<WhatsmeowSidecarOptions>> s_invalid = new(StringComparer.Ordinal)
	{
		["zero startup timeout"] = o => o.StartupTimeout = TimeSpan.Zero,
		["negative startup timeout"] = o => o.StartupTimeout = TimeSpan.FromSeconds(-1),
		["startup timeout above an hour"] = o => o.StartupTimeout = TimeSpan.FromHours(1) + TimeSpan.FromTicks(1),
		["largest startup timeout"] = o => o.StartupTimeout = TimeSpan.MaxValue,
		["white space sidecar path"] = o => o.SidecarPath = "  ",
		["white space database path"] = o => o.DatabasePath = "  ",
		["undefined transport"] = o => o.Transport = (SidecarTransport)7,
		["named pipe off Windows"] = o => o.Transport = SidecarTransport.NamedPipe,
	};

	public static TheoryData<string> Invalid =>
		[.. s_invalid.Keys.Where(name => name != "named pipe off Windows" || !OperatingSystem.IsWindows())];

	[Theory(Timeout = 60_000)]
	[MemberData(nameof(Invalid))]
	public async Task Start_InvalidOptions_FailsBeforeSpawningAnything(string invalid)
	{
		var stub = binaries.Stage(binaries.HangingStub);
		var options = new WhatsmeowSidecarOptions { SidecarPath = stub };
		s_invalid[invalid](options);

		_ = await Assert.ThrowsAsync<OptionsValidationException>(() => WhatsmeowSidecar.StartAsync(options, TestContext.Current.CancellationToken));

		Assert.False(Processes.StubStarted(stub));
	}
}
