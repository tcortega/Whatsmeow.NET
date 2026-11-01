using System.Diagnostics;
using System.Reflection;
using System.Text.RegularExpressions;

[assembly: AssemblyFixture(typeof(Whatsmeow.IntegrationTests.SidecarBinaries))]

namespace Whatsmeow.IntegrationTests;

/// <summary>
/// Builds the binaries the suite starts, once per run, with the Go toolchain on PATH (mise, AGENTS.md): the real
/// <c>whatsmeow-net</c> at the client's version and at another version, and the two stub sidecars of
/// <c>tools/stub-sidecar</c>. It also computes the <see cref="SidecarInfo"/> the real one must report, from sources
/// other than the sidecar itself.
/// </summary>
public sealed partial class SidecarBinaries : IAsyncLifetime
{
	private readonly List<string> _directories = [];

	/// <summary>The repository root.</summary>
	public string Root { get; } = FindRoot();

	/// <summary>The real sidecar, at the client's version: a release build, without the fake WhatsApp.</summary>
	public string Sidecar { get; private set; } = "";

	/// <summary>The real sidecar, built with another version.</summary>
	public string OtherVersionSidecar { get; private set; } = "";

	/// <summary>A stub that never prints its ready line.</summary>
	public string HangingStub { get; private set; } = "";

	/// <summary>A stub whose ready line echoes another nonce.</summary>
	public string WrongNonceStub { get; private set; } = "";

	/// <summary>A stub with a correct ready line whose endpoint serves nothing, and one more stdout line.</summary>
	public string ReadyStub { get; private set; } = "";

	/// <summary>What <see cref="Sidecar"/> must report.</summary>
	public SidecarInfo Expected { get; private set; } = null!;

	/// <summary>The client version, read here from the assembly attribute rather than through the client.</summary>
	public static string ClientVersion { get; } =
		typeof(WhatsmeowSidecar).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion.Split('+')[0];

	public static string FileName { get; } = OperatingSystem.IsWindows() ? "whatsmeow-net.exe" : "whatsmeow-net";

	public async ValueTask InitializeAsync()
	{
		var ct = TestContext.Current.CancellationToken;
		var built = NewDirectory();

		var builds = new[]
		{
			BuildAsync("sidecar", "./cmd/whatsmeow-net", $"-X main.version={ClientVersion}", "", Path.Combine(built, "sidecar"), ct),
			BuildAsync("sidecar", "./cmd/whatsmeow-net", "-X main.version=0.0.0-other", "", Path.Combine(built, "other-version"), ct),
			BuildAsync("tools", "./stub-sidecar", "-X main.mode=hang", "", Path.Combine(built, "hang"), ct),
			BuildAsync("tools", "./stub-sidecar", "-X main.mode=wrong-nonce", "", Path.Combine(built, "wrong-nonce"), ct),
			BuildAsync("tools", "./stub-sidecar", "-X main.mode=ready", "", Path.Combine(built, "ready"), ct),
		};
		var paths = await Task.WhenAll(builds).ConfigureAwait(false);
		(Sidecar, OtherVersionSidecar, HangingStub, WrongNonceStub, ReadyStub) = (paths[0], paths[1], paths[2], paths[3], paths[4]);

		Expected = new SidecarInfo
		{
			Version = ClientVersion,
			Protocol = 1,
			WhatsmeowVersion = (await File.ReadAllTextAsync(Path.Combine(Root, "third_party", "whatsmeow", "VERSION"), ct).ConfigureAwait(false)).Split(' ')[1],
			WhatsAppWebVersion = await WhatsAppWebVersionAsync(ct).ConfigureAwait(false),
			GoVersion = (await RunAsync("go", Path.Combine(Root, "sidecar"), ["env", "GOVERSION"], ct).ConfigureAwait(false)).Trim(),
			Commit = (await RunAsync("git", Root, ["rev-parse", "HEAD"], ct).ConfigureAwait(false)).Trim(),
		};
	}

	public ValueTask DisposeAsync()
	{
		foreach (var directory in _directories)
			Directory.Delete(directory, recursive: true);
		return ValueTask.CompletedTask;
	}

	/// <summary>
	/// Copies <paramref name="binary"/> into a directory of its own, so a test owns its process and the stubs' pid
	/// file. On Unix the copy is not executable: the client must make it so (ARCHITECTURE §8.5).
	/// </summary>
	public string Stage(string binary)
	{
		var path = Path.Combine(NewDirectory(), FileName);
		File.Copy(binary, path);
		if (!OperatingSystem.IsWindows())
			File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
		return path;
	}

	/// <summary>A new directory, deleted after the run.</summary>
	public string NewDirectory()
	{
		var directory = Directory.CreateTempSubdirectory("wmn-test-").FullName;
		lock (_directories)
			_directories.Add(directory);
		return directory;
	}

	/// <summary>Runs a command to completion and returns its stdout; a non-zero exit fails with its stderr.</summary>
	public static async Task<string> RunAsync(string command, string directory, IEnumerable<string> arguments, CancellationToken cancellationToken)
	{
		var info = new ProcessStartInfo(command) { UseShellExecute = false, WorkingDirectory = directory, RedirectStandardOutput = true, RedirectStandardError = true };
		info.Environment["CGO_ENABLED"] = "0";
		foreach (var argument in arguments)
			info.ArgumentList.Add(argument);

		using var process = Process.Start(info)!;
		var stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
		var stderr = process.StandardError.ReadToEndAsync(cancellationToken);
		await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);

		Assert.True(process.ExitCode == 0, $"{command} {string.Join(' ', info.ArgumentList)} exited {process.ExitCode}: {await stderr.ConfigureAwait(false)}");
		return await stdout.ConfigureAwait(false);
	}

	/// <summary>Builds a Go program into <paramref name="directory"/> with the build tags <paramref name="tags"/>, and returns its path.</summary>
	internal static async Task<string> BuildAsync(string module, string package, string ldflags, string tags, string directory, CancellationToken cancellationToken)
	{
		var output = Path.Combine(directory, FileName);
		_ = await RunAsync("go", Path.Combine(FindRoot(), module), ["build", "-tags", tags, "-ldflags", ldflags, "-o", output, package], cancellationToken)
			.ConfigureAwait(false);
		return output;
	}

	/// <summary>whatsmeow's WhatsApp Web version, read from its source at the pinned module.</summary>
	private async Task<string> WhatsAppWebVersionAsync(CancellationToken cancellationToken)
	{
		var module = (await RunAsync("go", Path.Combine(Root, "sidecar"), ["list", "-m", "-f", "{{.Dir}}", "go.mau.fi/whatsmeow"], cancellationToken).ConfigureAwait(false)).Trim();
		var source = await File.ReadAllTextAsync(Path.Combine(module, "store", "clientpayload.go"), cancellationToken).ConfigureAwait(false);
		var match = WaVersion().Match(source);
		Assert.True(match.Success, "whatsmeow's store/clientpayload.go no longer declares waVersion the way this fixture reads it");
		return $"{match.Groups["major"].Value}.{match.Groups["minor"].Value}.{match.Groups["patch"].Value}";
	}

	private static string FindRoot()
	{
		for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
		{
			if (File.Exists(Path.Combine(directory.FullName, "sidecar", "go.mod")))
				return directory.FullName;
		}

		Assert.Fail($"No repository root above {AppContext.BaseDirectory}");
		return "";
	}

	[GeneratedRegex(@"var waVersion = WAVersionContainer\{(?<major>\d+), (?<minor>\d+), (?<patch>\d+)\}", RegexOptions.ExplicitCapture, matchTimeoutMilliseconds: 1000)]
	private static partial Regex WaVersion();
}
