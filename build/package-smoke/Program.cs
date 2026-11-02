// The consumer that build/ci.sh install builds from the local feed: a new console app whose only changes are
// `dotnet add package Whatsmeow.Client`, `Whatsmeow.Extensions.Hosting` and `Whatsmeow.Testing` (ADR 0023, ADR 0034,
// ADR 0036). It never names a sidecar path, so the client must find the packaged binary itself.
//   start <version>  starts the sidecar with default options, calls GetInfo, and exits 0 only if it reports <version>.
//   missing <rid>    expects the start to fail because no binary ships for <rid>, naming both bundled paths.
//   external <version> <address> <token file>
//                    connects to a sidecar started standalone (External mode, ADR 0046), calls GetInfo, and exits 0
//                    only if it reports <version>; the consumer that runs it has no sidecar binary of its own.
using Whatsmeow;

switch (args)
{
	case ["start", var version]:
		return await StartAsync(version).ConfigureAwait(false);
	case ["missing", var rid]:
		return await MissingAsync(rid).ConfigureAwait(false);
	case ["external", var version, var address, var tokenPath]:
		return await VersionIsAsync(version, new WhatsmeowSidecarOptions { External = new ExternalSidecarOptions { Address = address, TokenPath = tokenPath } }).ConfigureAwait(false);
	default:
		await Console.Error.WriteLineAsync("usage: Consumer start <version> | missing <rid> | external <version> <address> <token file>").ConfigureAwait(false);
		return 2;
}

static async Task<int> StartAsync(string version)
{
	var directory = Directory.CreateTempSubdirectory("wmn-consumer-");
	try
	{
		return await VersionIsAsync(version, new WhatsmeowSidecarOptions { DatabasePath = Path.Combine(directory.FullName, "whatsmeow-net.db") }).ConfigureAwait(false);
	}
	finally
	{
		directory.Delete(recursive: true);
	}
}

// Starts or connects to the sidecar with options, and checks that it reports version.
static async Task<int> VersionIsAsync(string version, WhatsmeowSidecarOptions options)
{
	var sidecar = await WhatsmeowSidecar.StartAsync(options, CancellationToken.None).ConfigureAwait(false);
	await using (sidecar.ConfigureAwait(false))
	{
		var info = await sidecar.GetInfoAsync(CancellationToken.None).ConfigureAwait(false);
		await Console.Out.WriteLineAsync($"sidecar {info.Version}, protocol {info.Protocol}, app in {AppContext.BaseDirectory}").ConfigureAwait(false);
		if (info.Version == version)
			return 0;

		await Console.Error.WriteLineAsync($"the sidecar reports version {info.Version}, the packages are {version}").ConfigureAwait(false);
		return 1;
	}
}

static async Task<int> MissingAsync(string rid)
{
	var name = OperatingSystem.IsWindows() ? "whatsmeow-net.exe" : "whatsmeow-net";
	var expected = $"Paths tried: {Path.Combine(AppContext.BaseDirectory, name)}, {Path.Combine(AppContext.BaseDirectory, "runtimes", rid, "native", name)}.";
	try
	{
		var sidecar = await WhatsmeowSidecar.StartAsync(new WhatsmeowSidecarOptions(), CancellationToken.None).ConfigureAwait(false);
		await sidecar.DisposeAsync().ConfigureAwait(false);
		await Console.Error.WriteLineAsync($"a sidecar started on {rid}, which ships no binary").ConfigureAwait(false);
		return 1;
	}
	catch (SidecarUnavailableException e) when (e.Message.Contains(expected, StringComparison.Ordinal))
	{
		await Console.Out.WriteLineAsync(e.Message).ConfigureAwait(false);
		return 0;
	}
}
