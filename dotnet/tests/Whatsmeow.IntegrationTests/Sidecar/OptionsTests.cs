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

	private static readonly Dictionary<string, Action<WhatsmeowSidecarOptions>> s_invalidExternal = new(StringComparer.Ordinal)
	{
		["no address"] = o => o.External!.Address = null,
		["address without a scheme"] = o => o.External!.Address = "127.0.0.1:7000",
		["relative socket path"] = o => o.External!.Address = "unix:relative/s",
		["tcp without a port"] = o => o.External!.Address = "tcp:sidecar",
		["tcp port zero"] = o => o.External!.Address = "tcp:sidecar:0",
		["unbracketed IPv6"] = o => o.External!.Address = "tcp:::1:7000",
		["named pipe"] = o => o.External!.Address = "pipe:whatsmeow-net",
		["no token file"] = o => o.External!.TokenPath = null,
		["empty consumer"] = o => o.External!.Consumer = "",
		["consumer with a slash"] = o => o.External!.Consumer = "a/b",
		["consumer of 65 characters"] = o => o.External!.Consumer = new string('c', 65),
		["a sidecar path"] = o => o.SidecarPath = "whatsmeow-net",
		["a database path"] = o => o.DatabasePath = "whatsmeow-net.db",
		["a transport"] = o => o.Transport = SidecarTransport.NamedPipe,
		["history options"] = o => o.History.FullSyncDaysLimit = 7,
		["send pacing"] = o => o.SendPacing.Burst = 2,
	};

	public static TheoryData<string> InvalidExternal => [.. s_invalidExternal.Keys];

	/// <summary>External mode (ADR 0046): each case breaks one rule of options valid in every other part.</summary>
	[Theory(Timeout = 60_000)]
	[MemberData(nameof(InvalidExternal))]
	public async Task Start_InvalidExternalOptions_FailsWithThatOneRule(string invalid)
	{
		var options = new WhatsmeowSidecarOptions { External = External("tcp:sidecar:7000", "token", "client") };
		s_invalidExternal[invalid](options);

		var error = await Assert.ThrowsAsync<OptionsValidationException>(() => WhatsmeowSidecar.StartAsync(options, TestContext.Current.CancellationToken));

		_ = Assert.Single(error.Failures);
	}

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

	public static TheoryData<string> ValidExternal => ["unix:" + Path.Combine(Path.GetTempPath(), "s"), "tcp:sidecar:7000", "tcp:10.0.0.7:65535", "tcp:[::1]:7000"];

	[Theory(Timeout = 60_000)]
	[MemberData(nameof(ValidExternal))]
	public async Task Start_ExternalAddress_IsValidAndTheClientTriesToConnect(string address)
	{
		// Valid options get to the connect, which finds nothing there; an invalid one would fail before it.
		var options = new WhatsmeowSidecarOptions
		{
			StartupTimeout = TimeSpan.FromMilliseconds(1),
			External = External(address, StandaloneSidecar.NewTokenFile(binaries.NewDirectory()), "Consumer_1.a-b"),
		};

		var error = await Assert.ThrowsAsync<SidecarUnavailableException>(() => WhatsmeowSidecar.StartAsync(options, TestContext.Current.CancellationToken));

		Assert.StartsWith($"The sidecar at {address} did not answer within", error.Message, StringComparison.Ordinal);
	}

	private static ExternalSidecarOptions External(string address, string tokenPath, string consumer) =>
		new() { Address = address, TokenPath = tokenPath, Consumer = consumer };
}
