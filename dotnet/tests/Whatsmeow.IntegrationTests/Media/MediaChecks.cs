using System.Security.Cryptography;

namespace Whatsmeow.IntegrationTests.Media;

/// <summary>
/// What an upload and a download must give. The fake-mode tests and <c>Whatsmeow.LiveTests</c> check a run with these
/// same assertions (docs/guidelines/tests.md Live WhatsApp); what only WhatsApp knows (the path, the keys, the URL) is
/// passed in.
/// </summary>
internal static class MediaChecks
{
	/// <summary>
	/// An upload of <paramref name="content"/> as <paramref name="type"/>: its hash and length, the URL WhatsApp answered
	/// (<paramref name="url"/>), no streaming sidecar (whatsmeow makes one for a video only), and the path and keys
	/// WhatsApp and whatsmeow chose.
	/// </summary>
	public static void ValidateUploaded(MediaRef media, MediaType type, byte[] content, Uri url) =>
		Assert.Equal(
			new MediaRef
			{
				Type = type,
				DirectPath = media.DirectPath,
				Url = url,
				MediaKey = media.MediaKey,
				FileSha256 = [.. SHA256.HashData(content)],
				FileEncSha256 = media.FileEncSha256,
				FileLength = content.Length,
				StreamingSidecar = [],
			},
			media
		);

	/// <summary>The media downloads, to a stream, exactly what was uploaded.</summary>
	public static async Task ValidateDownloadsAsync(IWhatsAppClient client, MediaRef media, byte[] content, CancellationToken cancellationToken)
	{
		using var downloaded = new MemoryStream();
		await client.DownloadMediaAsync(media, downloaded, cancellationToken).ConfigureAwait(false);
		Assert.Equal(content, downloaded.ToArray());
	}
}
