// Runs the product path under Native AOT; exit code 0 means it worked (ADR 0056).
//   AotSmoke <version> <rid> [<whatsmeow-net>]
// <version> is the version the sidecar must report, and <rid> the RID the app was published for, which it must run
// as, natively: an app emulated on another architecture (Rosetta, Prism) fails. With <whatsmeow-net>, the repository's
// build/ci.sh dotnet, it starts that binary; without it, build/ci.sh aot on a feed's packages, the client finds the
// packaged one beside the app.
// Two paths, each on a database of its own: without DI it starts the sidecar, calls GetInfo, creates a session and
// lists it, and stops it; with the generic host it binds the options from configuration, declares a keyed session and
// registers a handler, starts the host, checks the bound options and the keyed session, calls GetInfo, and stops it.
// Then it round-trips one upstream WhatsApp message.
using System.Runtime.InteropServices;
using AotSmoke;
using Google.Protobuf;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Whatsmeow;
using Whatsmeow.Events;
using Whatsmeow.Extensions.Hosting;
using Whatsmeow.Proto.WAWebProtobufsE2E;
using Whatsmeow.Sessions;

if (args is not [var version, var rid, ..] || args.Length > 3)
{
	await Console.Error.WriteLineAsync("usage: AotSmoke <version> <rid> [<whatsmeow-net>]").ConfigureAwait(false);
	return 2;
}

var sidecarPath = args.Length == 3 ? args[2] : null;
var failures = new List<string>();

await Console.Out.WriteLineAsync($"runtime {RuntimeInformation.RuntimeIdentifier}, process {RuntimeInformation.ProcessArchitecture}, OS {RuntimeInformation.OSArchitecture}").ConfigureAwait(false);
if (RuntimeInformation.RuntimeIdentifier != rid)
	failures.Add($"published for {RuntimeInformation.RuntimeIdentifier}, expected {rid}");
if (RuntimeInformation.ProcessArchitecture != RuntimeInformation.OSArchitecture)
	failures.Add($"the {RuntimeInformation.ProcessArchitecture} process runs emulated on {RuntimeInformation.OSArchitecture}");

failures.AddRange(await WithoutDiAsync(version, sidecarPath).ConfigureAwait(false));
failures.AddRange(await HostedAsync(version, sidecarPath).ConfigureAwait(false));

var message = new Message { Conversation = "aot smoke" };
if (!Message.Parser.ParseFrom(message.ToByteArray()).Equals(message))
	failures.Add("the upstream WhatsApp message did not round-trip");

foreach (var failure in failures)
	await Console.Error.WriteLineAsync($"AotSmoke: {failure}").ConfigureAwait(false);
return failures.Count == 0 ? 0 : 1;

static async Task<List<string>> WithoutDiAsync(string version, string? sidecarPath)
{
	var failures = new List<string>();
	var directory = Directory.CreateTempSubdirectory("wmn-aot-");
	try
	{
		var options = new WhatsmeowSidecarOptions { SidecarPath = sidecarPath, DatabasePath = Path.Combine(directory.FullName, "whatsmeow-net.db") };
		var sidecar = await WhatsmeowSidecar.StartAsync(options, CancellationToken.None).ConfigureAwait(false);
		await using (sidecar.ConfigureAwait(false))
		{
			failures.AddRange(await InfoAsync("without DI", sidecar, version).ConfigureAwait(false));
			_ = await sidecar.Sessions.GetOrCreateAsync(SessionId.From("aot-smoke"), CancellationToken.None).ConfigureAwait(false);
			var sessions = await sidecar.Sessions.ListAsync(CancellationToken.None).ConfigureAwait(false);
			if (sessions is not [{ State: SessionState.Created } session] || session.Id != SessionId.From("aot-smoke"))
				failures.Add($"without DI: the sessions are [{string.Join(", ", sessions.Select(static s => $"{s.Id} {s.State}"))}], expected aot-smoke Created");
		}
	}
	finally
	{
		directory.Delete(recursive: true);
	}

	return failures;
}

static async Task<List<string>> HostedAsync(string version, string? sidecarPath)
{
	var failures = new List<string>();
	var directory = Directory.CreateTempSubdirectory("wmn-aot-hosted-");
	try
	{
		var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { ContentRootPath = directory.FullName });
		var configuration = new Dictionary<string, string?>(StringComparer.Ordinal)
		{
			// Relative: the hosting package puts it in the content root (ADR 0034).
			["Whatsmeow:DatabasePath"] = "hosted.db",
			["Whatsmeow:StartupTimeout"] = "00:00:45",
			["Whatsmeow:Transport"] = nameof(SidecarTransport.UnixDomainSocket),
		};
		if (sidecarPath is not null)
			configuration["Whatsmeow:SidecarPath"] = sidecarPath;
		_ = builder.Configuration.AddInMemoryCollection(configuration);
		_ = builder.Services.AddWhatsmeow().AddSession("aot-hosted").AddHandler<MessageReceived, SmokeHandler>();

		using var host = builder.Build();
		await host.StartAsync(CancellationToken.None).ConfigureAwait(false);
		try
		{
			var options = host.Services.GetRequiredService<IOptions<WhatsmeowSidecarOptions>>().Value;
			if (options.StartupTimeout != TimeSpan.FromSeconds(45) || options.DatabasePath != Path.Combine(directory.FullName, "hosted.db"))
				failures.Add($"hosted: the options bound StartupTimeout {options.StartupTimeout} and DatabasePath {options.DatabasePath}, expected 00:00:45 and hosted.db in the content root");

			var client = host.Services.GetRequiredKeyedService<IWhatsAppClient>("aot-hosted");
			var sidecar = host.Services.GetRequiredService<WhatsmeowSidecar>();
			var sessions = await sidecar.Sessions.ListAsync(CancellationToken.None).ConfigureAwait(false);
			if (client.SessionId != SessionId.From("aot-hosted") || sessions is not [{ State: SessionState.Created } session] || session.Id != client.SessionId)
				failures.Add($"hosted: the keyed client is {client.SessionId} and the sessions are [{string.Join(", ", sessions.Select(static s => $"{s.Id} {s.State}"))}], expected aot-hosted Created");

			failures.AddRange(await InfoAsync("hosted", sidecar, version).ConfigureAwait(false));
		}
		finally
		{
			await host.StopAsync(CancellationToken.None).ConfigureAwait(false);
		}
	}
	finally
	{
		directory.Delete(recursive: true);
	}

	return failures;
}

static async Task<List<string>> InfoAsync(string path, WhatsmeowSidecar sidecar, string version)
{
	var info = await sidecar.GetInfoAsync(CancellationToken.None).ConfigureAwait(false);
	await Console.Out.WriteLineAsync($"{path}: sidecar {info.Version}, protocol {info.Protocol}, whatsmeow {info.WhatsmeowVersion}, app in {AppContext.BaseDirectory}").ConfigureAwait(false);
	return info.Version == version && info.Protocol == 1 ? [] : [$"{path}: the sidecar reports version {info.Version} and protocol {info.Protocol}, expected {version} and 1"];
}
