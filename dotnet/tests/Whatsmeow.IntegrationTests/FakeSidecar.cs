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
	private readonly LogCapture _logs = new();
	private string? _directory;

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

		Sidecar = await WhatsmeowSidecar.StartFakeWhatsAppAsync(new WhatsmeowSidecarOptions { SidecarPath = binary }, _logs.Factory, ct).ConfigureAwait(false);
		Fake = new FakeWhatsAppService.FakeWhatsAppServiceClient(Sidecar.CallInvoker);
	}

	/// <summary>
	/// Kills the sidecar and returns once the supervisor restarted it, so the next test finds it running. Only tests
	/// in the <see cref="SidecarCrashes"/> call it.
	/// </summary>
	public async Task CrashAsync(CancellationToken cancellationToken)
	{
		var restarts = _logs.Named("SidecarRestarted").Count;
		var (processId, _) = await _logs.StartedAsync(_logs.Named("SidecarStarted").Count, cancellationToken).ConfigureAwait(false);

		using (var process = Process.GetProcessById(processId))
			process.Kill();

		_ = await _logs.WaitForAsync(_ => _logs.Named("SidecarRestarted").Count > restarts, cancellationToken).ConfigureAwait(false);
	}

	public async ValueTask DisposeAsync()
	{
		if (Sidecar is not null)
			await Sidecar.DisposeAsync().ConfigureAwait(false);
		_logs.Dispose();
		if (_directory is not null)
			Directory.Delete(_directory, recursive: true);
	}
}

/// <summary>The tests that crash the shared sidecar: never in parallel with another test.</summary>
[CollectionDefinition(nameof(SidecarCrashes), DisableParallelization = true)]
public sealed class SidecarCrashes;
