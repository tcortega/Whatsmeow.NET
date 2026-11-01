using System.Diagnostics;
using Whatsmeow.IntegrationTests.Support;

namespace Whatsmeow.IntegrationTests.Sidecar;

/// <summary>
/// The Sidecar slice through its public API against the real <c>whatsmeow-net</c>, and the stub sidecars for the
/// startup failures the real one never produces. Each test starts its own sidecar, because starting and stopping
/// is the behaviour under test.
/// </summary>
public sealed class SidecarTests(SidecarBinaries binaries)
{
	private const int Timeout = 120_000;

	/// <summary>AF_UNIX everywhere, and on Windows the named-pipe fallback too (ADR 0014).</summary>
	public static TheoryData<SidecarTransport> Transports =>
		OperatingSystem.IsWindows() ? [SidecarTransport.UnixDomainSocket, SidecarTransport.NamedPipe] : [SidecarTransport.UnixDomainSocket];

	[Theory(Timeout = Timeout)]
	[MemberData(nameof(Transports))]
	public async Task FullCycle_StartGetInfoDispose(SidecarTransport transport)
	{
		var ct = TestContext.Current.CancellationToken;
		using var logs = new LogCapture();
		var options = new WhatsmeowSidecarOptions { SidecarPath = binaries.Stage(binaries.Sidecar), Transport = transport };

		await using var sidecar = await WhatsmeowSidecar.StartAsync(options, logs.Factory, ct);
		var (processId, _) = await logs.StartedAsync(1, ct);
		Assert.Equal(binaries.Expected, await sidecar.GetInfoAsync(ct));

		await sidecar.DisposeAsync();
		Processes.AssertGone(processId);
	}

	[Fact(Timeout = Timeout)]
	public async Task Start_ABinaryWithoutTheExecuteBit_RunsItInPlace()
	{
		var ct = TestContext.Current.CancellationToken;
		using var logs = new LogCapture();

		// Stage leaves the copy without its execute bit on Unix; the client sets it rather than copying the binary.
		await using var sidecar = await WhatsmeowSidecar.StartAsync(new WhatsmeowSidecarOptions { SidecarPath = binaries.Stage(binaries.Sidecar) }, logs.Factory, ct);

		Assert.Empty(logs.Named("SidecarCopied"));
	}

	[Fact(Timeout = Timeout)]
	public async Task Dispose_DeletesTheSocketDirectory()
	{
		var ct = TestContext.Current.CancellationToken;
		using var logs = new LogCapture();
		var sidecar = await WhatsmeowSidecar.StartAsync(new WhatsmeowSidecarOptions { SidecarPath = binaries.Stage(binaries.Sidecar) }, logs.Factory, ct);
		var (_, endpoint) = await logs.StartedAsync(1, ct);

		await sidecar.DisposeAsync();

		Assert.False(Directory.Exists(Path.GetDirectoryName(endpoint)));
	}

	[Fact(Timeout = Timeout)]
	public async Task Start_TheSidecarNeverPrintsItsReadyLine_FailsWithTheStderrTailAndLeavesNoProcess()
	{
		var ct = TestContext.Current.CancellationToken;
		var stub = binaries.Stage(binaries.HangingStub);
		var options = new WhatsmeowSidecarOptions { SidecarPath = stub, StartupTimeout = TimeSpan.FromSeconds(2) };

		var error = await Assert.ThrowsAsync<SidecarUnavailableException>(() => WhatsmeowSidecar.StartAsync(options, ct));

		Assert.Contains("stub-sidecar: mode hang, this line is the stderr tail", error.Message, StringComparison.Ordinal);
		Processes.AssertGone(Processes.StubProcessId(stub));
	}

	[Fact(Timeout = Timeout)]
	public async Task Start_AnotherSidecarVersion_ThrowsAndStopsIt()
	{
		var ct = TestContext.Current.CancellationToken;
		using var logs = new LogCapture();
		var options = new WhatsmeowSidecarOptions { SidecarPath = binaries.Stage(binaries.OtherVersionSidecar) };

		_ = await Assert.ThrowsAsync<SidecarVersionMismatchException>(() => WhatsmeowSidecar.StartAsync(options, logs.Factory, ct));

		Processes.AssertGone((await logs.StartedAsync(1, ct)).ProcessId);
	}

	[Fact(Timeout = Timeout)]
	public async Task Start_AProtocolOutsideTheClientsRange_ThrowsAndStopsTheSidecar()
	{
		var ct = TestContext.Current.CancellationToken;
		using var logs = new LogCapture();
		var options = new WhatsmeowSidecarOptions { SidecarPath = binaries.Stage(binaries.Sidecar) };

		// The one internal seam: the real sidecar cannot be made to report another protocol (tests.md).
		_ = await Assert.ThrowsAsync<SidecarVersionMismatchException>(() => WhatsmeowSidecar.StartAsync(options, logs.Factory, minProtocol: 2, maxProtocol: 3, ct));

		Processes.AssertGone((await logs.StartedAsync(1, ct)).ProcessId);
	}

	[Fact(Timeout = Timeout)]
	public async Task Start_AReadyLineWithAnotherNonce_NeverUsesThatSidecar()
	{
		var ct = TestContext.Current.CancellationToken;
		using var logs = new LogCapture();
		var stub = binaries.Stage(binaries.WrongNonceStub);

		_ = await Assert.ThrowsAsync<SidecarUnavailableException>(() => WhatsmeowSidecar.StartAsync(new WhatsmeowSidecarOptions { SidecarPath = stub }, logs.Factory, ct));

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
	public async Task Start_StderrBeforeTheReadyLine_IsLoggedUnderWhatsmeowSidecar()
	{
		var ct = TestContext.Current.CancellationToken;
		using var logs = new LogCapture();

		await using var sidecar = await WhatsmeowSidecar.StartAsync(new WhatsmeowSidecarOptions { SidecarPath = binaries.Stage(binaries.Sidecar) }, logs.Factory, ct);

		var record = await logs.WaitForAsync(r => r.Message.Contains("\"message\":\"starting\"", StringComparison.Ordinal), ct);
		Assert.Equal(("Whatsmeow.Sidecar", LogLevel.Information), (record.Category, record.Level));
	}

	[Fact(Timeout = Timeout)]
	public async Task SidecarKilledFromOutside_IsRestartedAndAnswersAgain()
	{
		var ct = TestContext.Current.CancellationToken;
		using var logs = new LogCapture();
		await using var sidecar = await WhatsmeowSidecar.StartAsync(new WhatsmeowSidecarOptions { SidecarPath = binaries.Stage(binaries.Sidecar) }, logs.Factory, ct);
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

		using var parent = Process.Start(info)!;
		var line = await parent.StandardOutput.ReadLineAsync(ct);
		if (!int.TryParse(line, System.Globalization.CultureInfo.InvariantCulture, out var sidecar))
			Assert.Fail($"the host printed no sidecar process id: {await parent.StandardError.ReadToEndAsync(ct)}");

		parent.Kill();
		await parent.WaitForExitAsync(ct);

		await Processes.WaitUntilGoneAsync(sidecar, ct);
	}

	[Fact(Timeout = Timeout)]
	public async Task Start_ABinaryOnANoexecMount_RunsACopy()
	{
		var ct = TestContext.Current.CancellationToken;
		// The single declared platform condition (tests.md): only root can mount noexec, so build/ci.sh provides the
		// mount on linux-x64 CI, and there the test must run.
		var noexec = Environment.GetEnvironmentVariable("WHATSMEOW_NET_TEST_NOEXEC_DIR");
		if (noexec is null)
		{
			Assert.False(
				Environment.GetEnvironmentVariable("CI") == "true" && OperatingSystem.IsLinux() && System.Runtime.InteropServices.RuntimeInformation.OSArchitecture is System.Runtime.InteropServices.Architecture.X64,
				"WHATSMEOW_NET_TEST_NOEXEC_DIR must name a noexec mount on linux-x64 CI (build/ci.sh)"
			);
			Assert.Skip("needs a noexec mount in WHATSMEOW_NET_TEST_NOEXEC_DIR (build/ci.sh provides one on linux-x64 CI)");
		}

		using var logs = new LogCapture();
		var binary = Path.Combine(Directory.CreateDirectory(Path.Combine(noexec, Guid.NewGuid().ToString("N"))).FullName, SidecarBinaries.FileName);
		File.Copy(binaries.Sidecar, binary);

		await using var sidecar = await WhatsmeowSidecar.StartAsync(new WhatsmeowSidecarOptions { SidecarPath = binary }, logs.Factory, ct);

		var copied = Assert.Single(logs.Named("SidecarCopied"));
		Assert.Equal(binary, copied.GetStructuredStateValue("Path"));
		Assert.Equal(binaries.Expected, await sidecar.GetInfoAsync(ct));
	}
}
