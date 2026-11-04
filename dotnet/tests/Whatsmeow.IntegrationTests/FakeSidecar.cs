using System.Diagnostics;
using System.Diagnostics.Metrics;
using Microsoft.Extensions.DependencyInjection;
using Whatsmeow.IntegrationTests.Fake;
using Whatsmeow.IntegrationTests.Support;

[assembly: AssemblyFixture(typeof(Whatsmeow.IntegrationTests.FakeSidecar))]

namespace Whatsmeow.IntegrationTests;

/// <summary>
/// The suite's one shared sidecar: a test build of <c>whatsmeow-net</c> (<c>-tags fakewhatsapp</c>) started with
/// <c>--fake-whatsapp</c>, for the whole run (ADR 0019). Tests talk to it through the public <see cref="Sidecar"/>,
/// and script and observe the fake WhatsApp through <see cref="Fake"/>. Nothing here waits on a duration.
/// The sidecar is shared, so each test uses session ids of its own.
/// With <c>WHATSMEOW_NET_TEST_SIDECAR=external</c> (<c>build/ci.sh external</c>) the fixture runs that sidecar standalone
/// over TCP itself, as an orchestrator would, and the client connects to it in External mode (ADR 0046).
/// </summary>
public sealed class FakeSidecar : IAsyncLifetime
{
	/// <summary>The fixture's mode, read here only: <c>external</c>, or embedded when unset.</summary>
	private const string ModeVariable = "WHATSMEOW_NET_TEST_SIDECAR";

	private string? _directory;
	private ServiceProvider? _metrics;
	private StandaloneSidecar? _standalone;
	private string _listen = "";
	private string _tokenPath = "";

	/// <summary>Whether the run is the External-mode pass.</summary>
	internal static bool IsExternal { get; } = Environment.GetEnvironmentVariable(ModeVariable) switch
	{
		null or "" => false,
		"external" => true,
		var mode => throw new InvalidOperationException($"{ModeVariable}={mode} is neither empty nor external."),
	};

	/// <summary>The test build of <c>whatsmeow-net</c>, for a test that starts a fake-mode sidecar of its own.</summary>
	internal string Binary { get; private set; } = "";

	/// <summary>The sidecar's real SQLite file.</summary>
	internal string DatabasePath { get; private set; } = "";

	/// <summary>The sidecar's media root: a test hands over the files it puts under a directory of its own here.</summary>
	internal string MediaRoot { get; private set; } = "";

	/// <summary>The process-wide history options the sidecar starts with (History/HistoryOptionsTests.cs).</summary>
	internal static Whatsmeow.History.HistorySyncOptions HistoryOptions =>
		new()
		{
			RequestFullSync = true,
			FullSyncDaysLimit = 30,
			FullSyncSizeLimitMegabytes = 512,
			StorageQuotaMegabytes = 2048,
		};

	/// <summary>
	/// The options the sidecar was started with, the caller's instance: a test may change it to show the sidecar kept a
	/// copy, and must put it back.
	/// </summary>
	internal WhatsmeowSidecarOptions Options { get; private set; } = null!;

	/// <summary>
	/// The client's log, Debug level included, also written to <c>dotnet/artifacts/TestResults/fake-sidecar.log</c>,
	/// which CI keeps when the job fails.
	/// </summary>
	internal LogCapture Logs { get; } = new(Path.Combine(SidecarBinaries.FindRoot(), "dotnet", "artifacts", "TestResults", "fake-sidecar.log"));

	/// <summary>
	/// The sidecar's meter's factory: a collector on it sees this sidecar's measurements only, not those of a test's
	/// own sidecar; tests tell their sessions apart by the session id tag.
	/// </summary>
	internal IMeterFactory MeterFactory { get; private set; } = null!;

	/// <summary>The public client.</summary>
	/// <exception cref="InvalidOperationException">Another test crashed the sidecar, and it is not back.</exception>
	public WhatsmeowSidecar Sidecar
	{
		get
		{
			ThrowIfNotBack();
			return field;
		}
		private set;
	} = null!;

	/// <summary>The fake WhatsApp's control, on the sidecar's own channel: the same socket and token, across restarts.</summary>
	/// <exception cref="InvalidOperationException">Another test crashed the sidecar, and it is not back.</exception>
	internal FakeWhatsAppService.FakeWhatsAppServiceClient Fake
	{
		get
		{
			ThrowIfNotBack();
			return field;
		}
		private set;
	} = null!;

	/// <summary>The crash whose restart has not been seen yet; null once the sidecar is back.</summary>
	private volatile Crash? _crash;

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
		_metrics = new ServiceCollection().AddMetrics().BuildServiceProvider();
		MeterFactory = _metrics.GetRequiredService<IMeterFactory>();
		if (IsExternal)
		{
			await StartExternalAsync(ct).ConfigureAwait(false);
			return;
		}

		Options = new WhatsmeowSidecarOptions
		{
			SidecarPath = binary,
			DatabasePath = DatabasePath,
			MediaRoot = MediaRoot,
			History = HistoryOptions,
		};
		Sidecar = await WhatsmeowSidecar.StartFakeWhatsAppWithMeterFactoryAsync(Options, Logs.Factory, MeterFactory, ct).ConfigureAwait(false);
		Fake = new FakeWhatsAppService.FakeWhatsAppServiceClient(Sidecar.CallInvoker);
	}

	/// <summary>
	/// Kills the sidecar and returns once the supervisor restarted it, so the next test finds it running. Only a test
	/// in the <see cref="SidecarCrashes"/> collection may call it: one that runs in parallel would break other tests.
	/// Until the restart, another test that reaches for <see cref="Sidecar"/> or <see cref="Fake"/> fails with this
	/// crash, not with a socket nobody listens on (#77).
	/// </summary>
	/// <exception cref="InvalidOperationException">
	/// The calling test is not in the <see cref="SidecarCrashes"/> collection, or a restart attempt failed.
	/// </exception>
	public async Task CrashAsync(CancellationToken cancellationToken)
	{
		if (TestContext.Current.TestCollection?.TestCollectionClassName != typeof(SidecarCrashes).FullName)
			throw new InvalidOperationException($"CrashAsync runs only in the {nameof(SidecarCrashes)} collection, which never runs in parallel.");

		ThrowIfNotBack();
		var restarts = Logs.Named("SidecarRestarted").Count;
		var failures = Logs.Named("SidecarRestartFailed").Count;
		var crash = new Crash { TestId = TestContext.Current.Test?.UniqueID, Test = TestContext.Current.Test?.TestDisplayName ?? "a test", Restarts = restarts };
		_crash = crash;
		if (_standalone is { } standalone)
		{
			// The orchestrator's part: the same database, address and token.
			Logs.Note($"{crash.Test} restarts the standalone sidecar");
			await standalone.DisposeAsync().ConfigureAwait(false);
			_standalone = await StandaloneSidecar.StartAsync(Binary, DatabasePath, _listen, _tokenPath, StandaloneArguments(), cancellationToken).ConfigureAwait(false);
		}

		if (_standalone is null)
		{
			var (processId, _) = await Logs.StartedAsync(Logs.Named("SidecarStarted").Count, cancellationToken).ConfigureAwait(false);
			Logs.Note($"{crash.Test} kills sidecar {processId}");
			using var process = Process.GetProcessById(processId);
			process.Kill();
		}

		// A failed attempt fails the test now, with its reason: the next attempt is a backoff away (ADR 0017).
		_ = await Logs
			.WaitForAsync(_ => Logs.Named("SidecarRestarted").Count > restarts || Logs.Named("SidecarRestartFailed").Count > failures, cancellationToken)
			.ConfigureAwait(false);
		if (Logs.Named("SidecarRestarted").Count > restarts)
		{
			_crash = null;
			return;
		}

		var failure = Logs.Named("SidecarRestartFailed")[failures];
		throw new InvalidOperationException($"The shared sidecar did not come back after {crash.Test} crashed it: {failure.Exception?.Message}", failure.Exception);
	}

	/// <summary>Fails a test other than the one that crashed the sidecar, while the sidecar is not back.</summary>
	private void ThrowIfNotBack()
	{
		if (_crash is not { } crash || crash.TestId == TestContext.Current.Test?.UniqueID)
			return;

		if (Logs.Named("SidecarRestarted").Count > crash.Restarts)
		{
			_crash = null;
			return;
		}

		var failures = Logs.Named("SidecarRestartFailed");
		var reason = failures.Count == 0 ? "no restart attempt failed yet, so it is still waiting or starting" : $"its last restart attempt failed: {failures[^1].Exception?.Message}";
		throw new InvalidOperationException($"The shared sidecar is not back since {crash.Test} crashed it; {reason}");
	}

	/// <summary>One CrashAsync: the test that called it, and the restarts logged before its kill.</summary>
	private sealed class Crash
	{
		public required string? TestId { get; init; }

		public required string Test { get; init; }

		public required int Restarts { get; init; }
	}

	/// <summary>
	/// The External-mode pass: the sidecar standalone over TCP, with the flags the embedded fixture's options give it,
	/// and a client of it. A restart reuses the port the first start bound.
	/// </summary>
	private async Task StartExternalAsync(CancellationToken cancellationToken)
	{
		_tokenPath = StandaloneSidecar.NewTokenFile(_directory!);
		_standalone = await StandaloneSidecar.StartAsync(Binary, DatabasePath, "tcp:127.0.0.1:0", _tokenPath, StandaloneArguments(), cancellationToken).ConfigureAwait(false);
		_listen = _standalone.Address;
		Options = new WhatsmeowSidecarOptions
		{
			MediaRoot = MediaRoot,
			External = new ExternalSidecarOptions { Address = _listen, TokenPath = _tokenPath },
		};
		Sidecar = await WhatsmeowSidecar.StartAsync(Options, Logs.Factory, MeterFactory, cancellationToken).ConfigureAwait(false);
		Fake = new FakeWhatsAppService.FakeWhatsAppServiceClient(Sidecar.CallInvoker);
	}

	private string[] StandaloneArguments()
	{
		var history = HistoryOptions;
		return
		[
			"--fake-whatsapp",
			"--media-root", MediaRoot,
			"--history-full-sync",
			"--history-days-limit", $"{history.FullSyncDaysLimit}",
			"--history-size-limit-mb", $"{history.FullSyncSizeLimitMegabytes}",
			"--history-storage-quota-mb", $"{history.StorageQuotaMegabytes}",
		];
	}

	public async ValueTask DisposeAsync()
	{
		_crash = null;
		if (Sidecar is not null)
			await Sidecar.DisposeAsync().ConfigureAwait(false);
		if (_metrics is not null)
			await _metrics.DisposeAsync().ConfigureAwait(false);
		if (_standalone is not null)
			await _standalone.DisposeAsync().ConfigureAwait(false);
		Logs.Dispose();
		if (_directory is not null)
			Directory.Delete(_directory, recursive: true);
	}
}

/// <summary>The tests that crash the shared sidecar: never in parallel with another test.</summary>
[CollectionDefinition(nameof(SidecarCrashes), DisableParallelization = true)]
public sealed class SidecarCrashes;
