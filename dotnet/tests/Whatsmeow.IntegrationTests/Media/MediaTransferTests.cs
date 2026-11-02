using System.Diagnostics;
using System.Globalization;
using Whatsmeow.IntegrationTests.Fake;
using Whatsmeow.IntegrationTests.Sessions;
using Whatsmeow.IntegrationTests.Support;
using Whatsmeow.Sessions;

namespace Whatsmeow.IntegrationTests.Media;

/// <summary>
/// Hundreds of MiB up and down over each transport, at constant memory in both processes (ARCHITECTURE §8.6,
/// invariant 8). Each run starts a fake-mode sidecar of its own, on the transport under test, and measures its memory
/// alone; the collection runs alone, so no other test shares this process's heap meanwhile.
/// </summary>
[Collection(nameof(MediaTransfers))]
public sealed class MediaTransferTests(FakeSidecar fixture, SidecarBinaries binaries)
{
	private const long Size = 300L * 1024 * 1024;

	/// <summary>
	/// The growth allowed in each phase while 300 MiB move, at least twice the peak measured on the three CI hosts (CI
	/// runs 41394734844 and 41401548064: managed heap +9 to +44 MiB, the most on osx-arm64 in the whole suite; sidecar
	/// working set +10 to +14 MiB), and far below the file's size.
	/// </summary>
	private const long ManagedBound = 128L * 1024 * 1024;

	private const long SidecarBound = 64L * 1024 * 1024;

#pragma warning disable RS0030 // Justification: throughput is real time; nothing here can be faked.
	private static readonly TimeProvider s_wallClock = TimeProvider.System;
#pragma warning restore RS0030

	/// <summary>AF_UNIX everywhere, and on Windows the named-pipe fallback too (ADR 0014).</summary>
	public static TheoryData<SidecarTransport> Transports =>
		OperatingSystem.IsWindows() ? [SidecarTransport.UnixDomainSocket, SidecarTransport.NamedPipe] : [SidecarTransport.UnixDomainSocket];

	[Theory(Timeout = 600_000)]
	[MemberData(nameof(Transports))]
	public async Task FullCycle_HundredsOfMiB_UpAndDownInChunks_AtConstantMemory(SidecarTransport transport)
	{
		var ct = TestContext.Current.CancellationToken;
		using var logs = new LogCapture();
		var options = new WhatsmeowSidecarOptions { SidecarPath = fixture.Binary, DatabasePath = binaries.NewDatabase(), Transport = transport };
		await using var sidecar = await WhatsmeowSidecar.StartFakeWhatsAppAsync(options, logs.Factory, ct);
		var (processId, _) = await logs.StartedAsync(1, ct);
		using var process = Process.GetProcessById(processId);
		var fake = new FakeWhatsAppService.FakeWhatsAppServiceClient(sidecar.CallInvoker);
		var client = await sidecar.Sessions.GetOrCreateAsync(SessionId.From("media-transfer"), ct);
		_ = await fake.LinkDeviceAsync(new LinkDeviceRequest { SessionId = "media-transfer", Jid = SessionSetup.Jid("media-transfer") }, cancellationToken: ct);
		await client.ConnectAsync(ct);
		var watch = StateWatch.Start(client, ct);
		await using (watch)
			await watch.ReachesAsync(new SessionState.Connected(), new SessionState.Disconnected(), new SessionState.Connecting());

		var uploading = new MemorySampler(process);
		var downloading = uploading.Next();
		await using var sent = new GeneratedStream(Size, uploading);
		var started = s_wallClock.GetTimestamp();
		var media = await client.UploadMediaAsync(sent, MediaType.Document, ct);
		var upload = s_wallClock.GetElapsedTime(started);

		await using var received = new HashingStream(downloading);
		started = s_wallClock.GetTimestamp();
		await client.DownloadMediaAsync(media, received, ct);
		var download = s_wallClock.GetElapsedTime(started);

		Assert.Equal(Size, received.Length);
		Assert.Equal(sent.Hash(), received.Hash());
		Assert.Equal(Size, media.FileLength);

		var line = string.Create(
			CultureInfo.InvariantCulture,
			$"{System.Runtime.InteropServices.RuntimeInformation.RuntimeIdentifier} {transport}: upload {Size / 1048576.0 / upload.TotalSeconds:F0} MiB/s, managed heap +{uploading.ManagedGrowth / 1048576} MiB, sidecar working set +{uploading.SidecarGrowth / 1048576} MiB; download {Size / 1048576.0 / download.TotalSeconds:F0} MiB/s, managed heap +{downloading.ManagedGrowth / 1048576} MiB, sidecar working set +{downloading.SidecarGrowth / 1048576} MiB"
		);
		TestContext.Current.TestOutputHelper?.WriteLine(line);
		await File.AppendAllTextAsync(Path.Combine(binaries.Root, "dotnet", "artifacts", "TestResults", "media-transfers.txt"), line + Environment.NewLine, ct);

		Assert.InRange(uploading.ManagedGrowth, 0, ManagedBound);
		Assert.InRange(uploading.SidecarGrowth, 0, SidecarBound);
		Assert.InRange(downloading.ManagedGrowth, 0, ManagedBound);
		Assert.InRange(downloading.SidecarGrowth, 0, SidecarBound);
	}
}

/// <summary>The transfers that measure memory: never in parallel with another test.</summary>
[CollectionDefinition(nameof(MediaTransfers), DisableParallelization = true)]
public sealed class MediaTransfers;
