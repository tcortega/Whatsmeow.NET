using System.Diagnostics;
using Whatsmeow.IntegrationTests.Fake;
using Whatsmeow.IntegrationTests.Support;

[assembly: AssemblyFixture(typeof(Whatsmeow.IntegrationTests.FakeSidecar))]

namespace Whatsmeow.IntegrationTests;

/// <summary>
/// The suite's one shared sidecar: a test build of <c>whatsmeow-net</c> (<c>-tags fakewhatsapp</c>) started with
/// <c>--fake-whatsapp</c>, for the whole run (ADR 0019). Tests talk to it through the public <see cref="Sidecar"/>,
/// and script and observe the fake WhatsApp through <see cref="Fake"/>. Nothing here waits on a duration.
/// The sidecar is shared, so each test uses session ids of its own.
/// </summary>
public sealed class FakeSidecar : IAsyncLifetime
{
	private string? _directory;

	/// <summary>The test build of <c>whatsmeow-net</c>, for a test that starts a fake-mode sidecar of its own.</summary>
	internal string Binary { get; private set; } = "";

	/// <summary>The sidecar's real SQLite file.</summary>
	internal string DatabasePath { get; private set; } = "";

	/// <summary>The sidecar's media root: a test hands over the files it puts under a directory of its own here.</summary>
	internal string MediaRoot { get; private set; } = "";

	/// <summary>The client's log, Debug level included.</summary>
	internal LogCapture Logs { get; } = new();

	/// <summary>The public client.</summary>
	public WhatsmeowSidecar Sidecar { get; private set; } = null!;

	/// <summary>The fake WhatsApp's control, on the sidecar's own channel: the same socket and token, across restarts.</summary>
	internal FakeWhatsAppService.FakeWhatsAppServiceClient Fake { get; private set; } = null!;

	public async ValueTask InitializeAsync()
	{
		var ct = TestContext.Current.CancellationToken;
		_directory = Directory.CreateTempSubdirectory("wmn-fake-").FullName;
		var binary = await SidecarBinaries
			.BuildAsync("sidecar", "./cmd/whatsmeow-net", $"-X main.version={SidecarBinaries.ClientVersion}", "fakewhatsapp", _directory, ct)
			.ConfigureAwait(false);
		Binary = binary;

		DatabasePath = Path.Combine(_directory, "whatsmeow-net.db");
		MediaRoot = Directory.CreateDirectory(Path.Combine(_directory, "media")).FullName;
		Sidecar = await WhatsmeowSidecar
			.StartFakeWhatsAppAsync(new WhatsmeowSidecarOptions { SidecarPath = binary, DatabasePath = DatabasePath, MediaRoot = MediaRoot }, Logs.Factory, ct)
			.ConfigureAwait(false);
		Fake = new FakeWhatsAppService.FakeWhatsAppServiceClient(Sidecar.CallInvoker);
	}

	/// <summary>
	/// Kills the sidecar and returns once the supervisor restarted it, so the next test finds it running. Only a test
	/// in the <see cref="SidecarCrashes"/> collection may call it: one that runs in parallel would break other tests.
	/// </summary>
	/// <exception cref="InvalidOperationException">The calling test is not in the <see cref="SidecarCrashes"/> collection.</exception>
	public async Task CrashAsync(CancellationToken cancellationToken)
	{
		if (TestContext.Current.TestCollection?.TestCollectionClassName != typeof(SidecarCrashes).FullName)
			throw new InvalidOperationException($"CrashAsync runs only in the {nameof(SidecarCrashes)} collection, which never runs in parallel.");

		var restarts = Logs.Named("SidecarRestarted").Count;
		var (processId, _) = await Logs.StartedAsync(Logs.Named("SidecarStarted").Count, cancellationToken).ConfigureAwait(false);

		using (var process = Process.GetProcessById(processId))
			process.Kill();

		_ = await Logs.WaitForAsync(_ => Logs.Named("SidecarRestarted").Count > restarts, cancellationToken).ConfigureAwait(false);
	}

	public async ValueTask DisposeAsync()
	{
		if (Sidecar is not null)
			await Sidecar.DisposeAsync().ConfigureAwait(false);
		Logs.Dispose();
		if (_directory is not null)
			Directory.Delete(_directory, recursive: true);
	}
}

/// <summary>The tests that crash the shared sidecar: never in parallel with another test.</summary>
[CollectionDefinition(nameof(SidecarCrashes), DisableParallelization = true)]
public sealed class SidecarCrashes;
