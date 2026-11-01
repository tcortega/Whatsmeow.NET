// The consumer that build/ci.sh install builds from the local feed: a new console app whose only change is
// `dotnet add package Whatsmeow.Client` (ADR 0023). It never names a sidecar path, so the client must find the
// packaged binary itself.
//   start <version>  starts the sidecar with default options, calls GetInfo, and exits 0 only if it reports <version>.
//   missing <rid>    expects the start to fail because no binary ships for <rid>, naming both bundled paths.
using Whatsmeow;

switch (args)
{
	case ["start", var version]:
		return await StartAsync(version).ConfigureAwait(false);
	case ["missing", var rid]:
		return await MissingAsync(rid).ConfigureAwait(false);
	default:
		await Console.Error.WriteLineAsync("usage: Consumer start <version> | missing <rid>").ConfigureAwait(false);
		return 2;
}

static async Task<int> StartAsync(string version)
{
	var directory = Directory.CreateTempSubdirectory("wmn-consumer-");
	try
	{
		var options = new WhatsmeowSidecarOptions { DatabasePath = Path.Combine(directory.FullName, "whatsmeow-net.db") };
		var sidecar = await WhatsmeowSidecar.StartAsync(options, CancellationToken.None).ConfigureAwait(false);
		await using (sidecar.ConfigureAwait(false))
		{
			var info = await sidecar.GetInfoAsync(CancellationToken.None).ConfigureAwait(false);
			await Console.Out.WriteLineAsync($"sidecar {info.Version}, protocol {info.Protocol}, beside {AppContext.BaseDirectory}").ConfigureAwait(false);
			if (info.Version == version)
				return 0;

			await Console.Error.WriteLineAsync($"the sidecar reports version {info.Version}, the packages are {version}").ConfigureAwait(false);
			return 1;
		}
	}
	finally
	{
		directory.Delete(recursive: true);
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
