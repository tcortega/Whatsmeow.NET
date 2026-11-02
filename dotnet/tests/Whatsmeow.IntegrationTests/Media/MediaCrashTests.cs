using System.Security.Cryptography;
using Whatsmeow.IntegrationTests.Sessions;
using Whatsmeow.IntegrationTests.Support;
using Whatsmeow.Sessions;

namespace Whatsmeow.IntegrationTests.Media;

/// <summary>A stored <see cref="MediaRef"/> across a crash of the shared sidecar.</summary>
[Collection(nameof(SidecarCrashes))]
public sealed class MediaCrashTests(FakeSidecar fixture)
{
	private const int Timeout = 120_000;

	[Fact(Timeout = Timeout)]
	public async Task Download_AStoredRefAfterASidecarRestart_GivesTheFileBack()
	{
		var ct = TestContext.Current.CancellationToken;
		var client = await SessionSetup.ConnectedAsync(fixture, "media-crash", ct);
		var content = RandomNumberGenerator.GetBytes(100 * 1024);
		using var input = new MemoryStream(content);
		var media = await client.UploadMediaAsync(input, MediaType.Document, ct);

		await fixture.CrashAsync(ct);

		// The client connects the session again on the restarted sidecar, which kept nothing of the upload.
		var watch = StateWatch.Start(client, ct);
		await using (watch)
			await watch.ReachesAsync(new SessionState.Connected(), new SessionState.Disconnected(), new SessionState.Connecting());
		using var downloaded = new MemoryStream();
		await client.DownloadMediaAsync(media, downloaded, ct);

		Assert.Equal(content, downloaded.ToArray());
	}
}
