using System.Diagnostics;
using Whatsmeow.IntegrationTests.Support;

namespace Whatsmeow.IntegrationTests.Sidecar;

/// <summary>
/// The Sidecar slice's start and stop through its public API against the real <c>whatsmeow-net</c>, and the stub
/// sidecars for the startup failures the real one never produces. Each test starts its own sidecar, because starting
/// and stopping is the behaviour under test; the GetInfo FullCycle runs on the shared fixture (GetInfoTests).
/// </summary>
public sealed class SidecarTests(SidecarBinaries binaries)
{
	private const int Timeout = 120_000;

	/// <summary>For the stub tests that wait out the timeout: generous, because Defender can slow a first launch on Windows.</summary>
	private static readonly TimeSpan s_stubStartupTimeout = TimeSpan.FromSeconds(10);

	/// <summary>AF_UNIX everywhere, and on Windows the named-pipe fallback too (ADR 0014).</summary>
	public static TheoryData<SidecarTransport> Transports =>
		OperatingSystem.IsWindows() ? [SidecarTransport.UnixDomainSocket, SidecarTransport.NamedPipe] : [SidecarTransport.UnixDomainSocket];

	[Theory(Timeout = Timeout)]
	[MemberData(nameof(Transports))]
	public async Task StartDispose_LeavesNoProcess(SidecarTransport transport)
	{
		var ct = TestContext.Current.CancellationToken;
		using var logs = new LogCapture();
		var options = new WhatsmeowSidecarOptions { SidecarPath = binaries.Stage(binaries.Sidecar), Transport = transport, DatabasePath = binaries.NewDatabase() };

		// The start's own GetInfo and version check go over this transport; the GetInfo FullCycle is GetInfoTests.
		await using var sidecar = await WhatsmeowSidecar.StartAsync(options, logs.Factory, ct);
		var (processId, _) = await logs.StartedAsync(1, ct);

		await sidecar.DisposeAsync();
		Processes.AssertGone(processId);
	}

	[Fact(Timeout = Timeout)]
	public async Task Start_ABinaryWithoutTheExecuteBit_RunsItInPlace()
	{
		var ct = TestContext.Current.CancellationToken;
		using var logs = new LogCapture();

		// Stage leaves the copy without its execute bit on Unix; the client sets it rather than copying the binary.
		var staged = binaries.Stage(binaries.Sidecar);
		await using var sidecar = await WhatsmeowSidecar.StartAsync(new WhatsmeowSidecarOptions { SidecarPath = staged, DatabasePath = binaries.NewDatabase() }, logs.Factory, ct);
		var (processId, _) = await logs.StartedAsync(1, ct);

		// The process runs the staged file itself, on every OS: no cached copy. The staged directory's name is unique;
		// the full path is not compared, because macOS reports /var as /private/var.
		using var process = Process.GetProcessById(processId);
		Assert.EndsWith(Path.Combine(Path.GetFileName(Path.GetDirectoryName(staged))!, SidecarBinaries.FileName), process.MainModule!.FileName, StringComparison.Ordinal);
	}

	[Fact(Timeout = Timeout)]
	public async Task Dispose_DeletesTheSocketDirectory()
	{
		var ct = TestContext.Current.CancellationToken;
		using var logs = new LogCapture();
		var sidecar = await WhatsmeowSidecar.StartAsync(new WhatsmeowSidecarOptions { SidecarPath = binaries.Stage(binaries.Sidecar), DatabasePath = binaries.NewDatabase() }, logs.Factory, ct);
		var (_, endpoint) = await logs.StartedAsync(1, ct);

		await sidecar.DisposeAsync();

		Assert.False(Directory.Exists(Path.GetDirectoryName(endpoint)));
	}

	[Fact(Timeout = Timeout)]
	public async Task Start_FakeWhatsAppOnAReleaseBuild_FailsWithTheRefusal()
	{
		var ct = TestContext.Current.CancellationToken;
		var options = new WhatsmeowSidecarOptions { SidecarPath = binaries.Stage(binaries.Sidecar) };

		// The fake never ships: the release build refuses --fake-whatsapp before it serves anything (ADR 0019).
		var error = await Assert.ThrowsAsync<SidecarUnavailableException>(() =>
			WhatsmeowSidecar.StartFakeWhatsAppAsync(options, Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance, ct)
		);

		Assert.Contains("--fake-whatsapp needs a test build (-tags fakewhatsapp); this build never fakes WhatsApp", error.Message, StringComparison.Ordinal);
	}

	[Fact(Timeout = Timeout)]
	public async Task Start_TheSidecarNeverPrintsItsReadyLine_FailsWithTheStderrTailAndLeavesNoProcess()
	{
		var ct = TestContext.Current.CancellationToken;
		var stub = binaries.Stage(binaries.HangingStub);
		var options = new WhatsmeowSidecarOptions { SidecarPath = stub, StartupTimeout = s_stubStartupTimeout };

		var error = await Assert.ThrowsAsync<SidecarUnavailableException>(() => WhatsmeowSidecar.StartAsync(options, ct));

		Assert.Contains("stub-sidecar: mode hang, this line is the stderr tail", error.Message, StringComparison.Ordinal);
		Processes.AssertGone(Processes.StubProcessId(stub));
	}

	[Fact(Timeout = Timeout)]
	public async Task Start_AnotherSidecarVersion_ThrowsAndStopsIt()
	{
		var ct = TestContext.Current.CancellationToken;
		using var logs = new LogCapture();
		var options = new WhatsmeowSidecarOptions { SidecarPath = binaries.Stage(binaries.OtherVersionSidecar), DatabasePath = binaries.NewDatabase() };

		_ = await Assert.ThrowsAsync<SidecarVersionMismatchException>(() => WhatsmeowSidecar.StartAsync(options, logs.Factory, ct));

		Processes.AssertGone((await logs.StartedAsync(1, ct)).ProcessId);
	}

	[Fact(Timeout = Timeout)]
	public async Task Start_AProtocolOutsideTheClientsRange_ThrowsAndStopsTheSidecar()
	{
		var ct = TestContext.Current.CancellationToken;
		using var logs = new LogCapture();
		var options = new WhatsmeowSidecarOptions { SidecarPath = binaries.Stage(binaries.Sidecar), DatabasePath = binaries.NewDatabase() };

		// The one internal seam: the real sidecar cannot be made to report another protocol (tests.md).
		_ = await Assert.ThrowsAsync<SidecarVersionMismatchException>(() => WhatsmeowSidecar.StartAsync(options, logs.Factory, minProtocol: 2, maxProtocol: 3, ct));

		Processes.AssertGone((await logs.StartedAsync(1, ct)).ProcessId);
	}

	[Fact(Timeout = Timeout)]
	public async Task Start_Fails_TheBinaryCanBeDeletedAtOnce()
	{
		var ct = TestContext.Current.CancellationToken;
		var stub = binaries.Stage(binaries.WrongNonceStub);
		var options = new WhatsmeowSidecarOptions { SidecarPath = stub, StartupTimeout = s_stubStartupTimeout };

		_ = await Assert.ThrowsAsync<SidecarUnavailableException>(() => WhatsmeowSidecar.StartAsync(options, ct));

		// The failed start killed the stub. On Windows its image stays mapped until it is gone, and deleting it fails
		// until then (#179).
		await Deletion.DeleteFileAsync(stub);
		Assert.False(File.Exists(stub));
	}

	[Fact(Timeout = Timeout)]
	public async Task Start_AReadyLineWithAnotherNonce_NeverUsesThatSidecar()
	{
		var ct = TestContext.Current.CancellationToken;
		using var logs = new LogCapture();
		var stub = binaries.Stage(binaries.WrongNonceStub);
		var options = new WhatsmeowSidecarOptions { SidecarPath = stub, StartupTimeout = s_stubStartupTimeout };

		_ = await Assert.ThrowsAsync<SidecarUnavailableException>(() => WhatsmeowSidecar.StartAsync(options, logs.Factory, ct));

		Assert.Empty(logs.Named("SidecarStarted"));
		Processes.AssertGone(Processes.StubProcessId(stub));
	}

	[Fact(Timeout = Timeout)]
	public async Task Start_Cancelled_KillsTheHalfStartedProcess()
	{
		var ct = TestContext.Current.CancellationToken;
		using var logs = new LogCapture();
		using var cancel = CancellationTokenSource.CreateLinkedTokenSource(ct);
		var stub = binaries.Stage(binaries.HangingStub);
		var options = new WhatsmeowSidecarOptions { SidecarPath = stub, StartupTimeout = TimeSpan.FromMinutes(10) };

		var start = WhatsmeowSidecar.StartAsync(options, logs.Factory, cancel.Token);
		_ = await logs.WaitForAsync(r => r.Category == "Whatsmeow.Sidecar", ct);
		await cancel.CancelAsync();

		_ = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => start);
		Processes.AssertGone(Processes.StubProcessId(stub));
	}

	[Fact(Timeout = Timeout)]
	public async Task Start_StderrBeforeTheReadyLine_IsLoggedUnderWhatsmeowSidecarAtItsLevel()
	{
		var ct = TestContext.Current.CancellationToken;
		using var logs = new LogCapture();
		var options = new WhatsmeowSidecarOptions { SidecarPath = binaries.Stage(binaries.HangingStub), StartupTimeout = s_stubStartupTimeout };

		// The hanging stub never prints its ready line, so whatever it writes to stderr comes before it.
		_ = await Assert.ThrowsAsync<SidecarUnavailableException>(() => WhatsmeowSidecar.StartAsync(options, logs.Factory, ct));

		var record = Assert.Single(logs.Collector.GetSnapshot(), r => r.Category == "Whatsmeow.Sidecar");
		Assert.Equal(LogLevel.Warning, record.Level);
	}

	[Fact(Timeout = Timeout)]
	public async Task Start_FirstGetInfoFails_FailsWithTheStderrTailAndLogsLaterStdout()
	{
		var ct = TestContext.Current.CancellationToken;
		using var logs = new LogCapture();
		var stub = binaries.Stage(binaries.ReadyStub);
		var options = new WhatsmeowSidecarOptions { SidecarPath = stub, StartupTimeout = s_stubStartupTimeout };

		// The stub's ready line is correct, but nothing serves its endpoint.
		var error = await Assert.ThrowsAsync<SidecarUnavailableException>(() => WhatsmeowSidecar.StartAsync(options, logs.Factory, ct));

		Assert.Contains("stub-sidecar: mode ready, this line is the stderr tail", error.Message, StringComparison.Ordinal);
		var stdout = Assert.Single(logs.Collector.GetSnapshot(), r => r.Message == "stub-sidecar: a stdout line after the ready line");
		Assert.Equal(("Whatsmeow.Sidecar", LogLevel.Information), (stdout.Category, stdout.Level));
		Processes.AssertGone(Processes.StubProcessId(stub));
	}

	[Fact(Timeout = Timeout)]
	public async Task GetInfo_TheSidecarWasKilled_ThrowsSidecarUnavailableBeforeTheRestart()
	{
		var ct = TestContext.Current.CancellationToken;
		using var logs = new LogCapture();
		await using var sidecar = await WhatsmeowSidecar.StartAsync(new WhatsmeowSidecarOptions { SidecarPath = binaries.Stage(binaries.Sidecar), DatabasePath = binaries.NewDatabase() }, logs.Factory, ct);
		var (processId, _) = await logs.StartedAsync(1, ct);

		using (var process = Process.GetProcessById(processId))
			process.Kill();
		_ = await logs.WaitForAsync(r => r.Id.Name == "SidecarExited", ct);

		// The supervisor waits 1 s before the restart (ADR 0017); the call fails at once, well inside that wait.
		_ = await Assert.ThrowsAsync<SidecarUnavailableException>(() => sidecar.GetInfoAsync(ct));
		Assert.Empty(logs.Named("SidecarRestarted"));
	}

	[Fact(Timeout = Timeout)]
	public async Task SidecarKilledFromOutside_IsRestartedAndAnswersAgain()
	{
		var ct = TestContext.Current.CancellationToken;
		using var logs = new LogCapture();
		await using var sidecar = await WhatsmeowSidecar.StartAsync(new WhatsmeowSidecarOptions { SidecarPath = binaries.Stage(binaries.Sidecar), DatabasePath = binaries.NewDatabase() }, logs.Factory, ct);
		var (first, _) = await logs.StartedAsync(1, ct);

		using (var process = Process.GetProcessById(first))
			process.Kill();

		var (second, _) = await logs.StartedAsync(2, ct);
		var restarted = await logs.WaitForAsync(r => r.Id.Name == "SidecarRestarted", ct);
		Assert.Equal(
			($"{second}", "00:00:01", "1"),
			(restarted.GetStructuredStateValue("ProcessId"), restarted.GetStructuredStateValue("Delay"), restarted.GetStructuredStateValue("Attempt"))
		);
		Assert.Equal(binaries.Expected, await sidecar.GetInfoAsync(ct));
	}

	[Fact(Timeout = Timeout)]
	public async Task ParentKilledHard_SidecarExitsOnItsOwn()
	{
		var ct = TestContext.Current.CancellationToken;
		var host = Path.Combine(AppContext.BaseDirectory, OperatingSystem.IsWindows() ? "SidecarHost.exe" : "SidecarHost");
		var info = new ProcessStartInfo(host) { UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
		info.ArgumentList.Add(binaries.Stage(binaries.Sidecar));
		info.ArgumentList.Add(binaries.NewDatabase());

		using var parent = Process.Start(info)!;
		var line = await parent.StandardOutput.ReadLineAsync(ct);
		if (!int.TryParse(line, System.Globalization.CultureInfo.InvariantCulture, out var sidecar))
			Assert.Fail($"the host printed no sidecar process id: {await parent.StandardError.ReadToEndAsync(ct)}");

		parent.Kill();
		await parent.WaitForExitAsync(ct);

		await Processes.WaitUntilGoneAsync(sidecar, ct);
	}

	/// <summary>
	/// Another process keeps the sidecar's stdin open after the parent dies, so EOF never comes: the backup to the
	/// tether stops the sidecar (ARCHITECTURE §13 invariant 9). The holder still runs at the end, so it was not its exit
	/// that closed stdin.
	/// </summary>
	[Fact(Timeout = Timeout)]
	public async Task ParentKilledHard_StdinHeldOpenElsewhere_SidecarExitsOnItsOwn()
	{
		var ct = TestContext.Current.CancellationToken;
		var host = Path.Combine(AppContext.BaseDirectory, OperatingSystem.IsWindows() ? "SidecarHost.exe" : "SidecarHost");
		var info = new ProcessStartInfo(host) { UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
		info.ArgumentList.Add("held-tether");
		info.ArgumentList.Add(binaries.Stage(binaries.Sidecar));
		info.ArgumentList.Add(binaries.NewDatabase());

		using var parent = Process.Start(info)!;
		var sidecarLine = await parent.StandardOutput.ReadLineAsync(ct);
		var holderLine = await parent.StandardOutput.ReadLineAsync(ct);

		// The holder is parsed first and killed in the finally, so no failure below can leak it.
		using var holder = int.TryParse(holderLine, System.Globalization.CultureInfo.InvariantCulture, out var holderId) ? Process.GetProcessById(holderId) : null;
		try
		{
			if (!int.TryParse(sidecarLine, System.Globalization.CultureInfo.InvariantCulture, out var sidecar) || holder is null)
			{
				// The holder keeps the host's stderr open too, so both go before it is read.
				parent.Kill();
				holder?.Kill();
				Assert.Fail($"the host printed no sidecar and holder process ids: {sidecarLine} {holderLine} {await parent.StandardError.ReadToEndAsync(ct)}");
			}

			parent.Kill();
			await parent.WaitForExitAsync(ct);

			await Processes.WaitUntilGoneAsync(sidecar, ct);
			Assert.False(holder.HasExited);
		}
		finally
		{
			holder?.Kill();
		}
	}
}
