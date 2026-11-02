using System.Globalization;
using System.Security.Cryptography;
using Whatsmeow.Events;
using Whatsmeow.IntegrationTests.Media;
using Whatsmeow.Messaging;

namespace Whatsmeow.LiveTests;

/// <summary>
/// Media (#28) against real WhatsApp, with the fake-mode tests' own checks (<see cref="MediaChecks"/>), and what only
/// WhatsApp answers, recorded for ARCHITECTURE §14 and RESEARCH items 12 and 14: how large a document WhatsApp takes, and
/// how long a ref stays downloadable and can be sent again.
/// </summary>
[Collection(nameof(Live))]
public sealed class LiveMediaTests(LiveSidecar live, LivePeer peer)
{
	/// <summary>The size test's timeout: four uploads, sends and downloads, the largest 100 MiB, on a home connection.</summary>
	private const int SizeTimeout = 1_800_000;

	/// <summary>The document sizes tried, in MiB, smallest first, up to the 100 MiB cap.</summary>
	private static readonly int[] s_sizes = [1, 16, 64, 100];

	/// <summary>An image and a document upload, each downloads back by its <see cref="MediaRef"/> alone.</summary>
	[LiveFact]
	public async Task Live_UploadThenDownloadByTheMediaRef_GivesTheBytesBack()
	{
		var ct = TestContext.Current.CancellationToken;
		var client = await live.LiveAsync(ct);
		var content = RandomNumberGenerator.GetBytes(300 * 1024);

		foreach (var type in new[] { MediaType.Image, MediaType.Document })
		{
			using var input = new MemoryStream(content);
			var started = LiveClock.Time.GetTimestamp();
			var media = await client.UploadMediaAsync(input, type, ct);
			live.Write($"live: uploaded a {content.Length} byte {type} in {LiveClock.Time.GetElapsedTime(started).TotalSeconds:F1} s, to {media.Url?.Host ?? "no URL"}");

			Assert.True(media.Url is { Scheme: "https" } url && url.Host.EndsWith(".whatsapp.net", StringComparison.Ordinal), $"the upload's URL is not WhatsApp's: {media.Url?.Host}");
			MediaChecks.ValidateUploaded(media, type, content, media.Url!);
			await MediaChecks.ValidateDownloadsAsync(client, media, content, ct);
		}
	}

	/// <summary>
	/// Documents of 1, 16, 64 and 100 MiB (RESEARCH item 14): each is uploaded, sent to the peer, and downloaded there by
	/// the ref it got, until WhatsApp refuses one. Each size, its times and the first refusal go to the log; the cap is
	/// ours, so a run that takes all of them records that 100 MiB went through, not the limit.
	/// </summary>
	[LiveFact(NeedsPeer = true, Timeout = SizeTimeout)]
	public async Task Live_DocumentsOfIncreasingSize_ThePeerDownloadsEach_UntilWhatsAppRefusesOne()
	{
		var ct = TestContext.Current.CancellationToken;
		var client = await live.LiveAsync(ct);
		var other = await peer.ConnectedAsync(live, ct);
		var sender = await live.AccountAsync(ct);
		var (to, _) = await peer.AccountAsync(ct);

		foreach (var mib in s_sizes)
		{
			var length = mib * 1024L * 1024;
			MediaRef media;
			byte[] hash;
			var started = LiveClock.Time.GetTimestamp();
			await using (var input = new GeneratedStream(length, memory: null))
			{
				try
				{
					media = await client.UploadMediaAsync(input, MediaType.Document, ct);
				}
				catch (WhatsmeowException e)
				{
					live.Write($"live: WhatsApp refused the {mib} MiB upload after {LiveClock.Time.GetElapsedTime(started).TotalSeconds:F0} s: {e.ErrorCode} {e.Message}");
					return;
				}

				hash = input.Hash();
			}

			var uploaded = LiveClock.Time.GetElapsedTime(started);
			Assert.Equal((hash, length), (media.FileSha256.ToArray(), media.FileLength));

			var id = MessageId.New();
			var metadata = new DocumentMetadata { Mimetype = "application/octet-stream", FileName = $"wmn-{mib}MiB.bin" };
			var arrived = LiveChecks.ArrivalAsync(other, id, ct);
			try
			{
				_ = await client.SendDocumentAsync(to, media, metadata, id, cancellationToken: ct);
			}
			catch (WhatsmeowException e)
			{
				live.Write($"live: the {mib} MiB upload took {uploaded.TotalSeconds:F0} s, and WhatsApp refused its send: {e.ErrorCode} {e.Message}");
				return;
			}

			var got = await LiveChecks.WithinAsync(arrived, $"the {mib} MiB document");
			Assert.Equal(LiveChecks.Received(other, got, sender, id, new DocumentContent { Media = media, Metadata = metadata }), got);

			var downloading = LiveClock.Time.GetTimestamp();
			await using var downloaded = new HashingStream(memory: null);
			await other.DownloadMediaAsync(((DocumentContent)got.Content).Media, downloaded, ct);
			live.Write(
				$"live: a {mib} MiB document: uploaded in {uploaded.TotalSeconds:F0} s, sent, and downloaded by the peer in {LiveClock.Time.GetElapsedTime(downloading).TotalSeconds:F0} s"
			);
			Assert.Equal((hash, length), (downloaded.Hash(), downloaded.Length));
		}

		live.Write($"live: every size up to the {s_sizes[^1]} MiB cap went through");
	}

	/// <summary>
	/// How long a ref stays downloadable, and whether an old one can be sent again (RESEARCH item 12). Every ref an
	/// earlier run kept in <see cref="LiveSettings.MediaRefsVariable"/>'s file is downloaded, and its age and answer go to
	/// the log; the oldest one that still downloads is sent to the peer, which downloads it by the ref it got. This run's
	/// ref is then added to the file. Without the file, this run's ref is the oldest one tried.
	/// </summary>
	[LiveFact(NeedsPeer = true)]
	public async Task Live_TheRefsOfEarlierRuns_EachDownloadsOrExpires_AndTheOldestThatDownloadsIsSentAgain()
	{
		var ct = TestContext.Current.CancellationToken;
		var client = await live.LiveAsync(ct);
		var other = await peer.ConnectedAsync(live, ct);
		var sender = await live.AccountAsync(ct);
		var (to, _) = await peer.AccountAsync(ct);
		var now = LiveClock.Time.GetUtcNow();
		var kept = LiveMediaRefs.Read(LiveSettings.MediaRefs);
		if (LiveSettings.MediaRefs.Length == 0)
			live.Write($"live: no {LiveSettings.MediaRefsVariable}, so no ref of an earlier run is tried");

		(LiveMediaRefs.Kept Ref, TimeSpan Age)? oldest = null;
		foreach (var earlier in kept.OrderBy(k => k.UploadedAt))
		{
			var age = now - earlier.UploadedAt;
			try
			{
				await using var downloaded = new HashingStream(memory: null);
				await client.DownloadMediaAsync(earlier.Media, downloaded, ct);
				var intact = downloaded.Hash().AsSpan().SequenceEqual(earlier.Media.FileSha256.AsSpan());
				live.Write($"live: a ref {Age(age)} old downloads{(intact ? "" : ", but not the bytes uploaded")}");
				if (intact)
					oldest ??= (earlier, age);
			}
			catch (WhatsmeowException e)
			{
				live.Write($"live: a ref {Age(age)} old does not download: {e.ErrorCode}");
			}
		}

		var content = RandomNumberGenerator.GetBytes(64 * 1024);
		using (var input = new MemoryStream(content))
		{
			var media = await client.UploadMediaAsync(input, MediaType.Document, ct);
			MediaChecks.ValidateUploaded(media, MediaType.Document, content, media.Url!);
			LiveMediaRefs.Append(LiveSettings.MediaRefs, new LiveMediaRefs.Kept { UploadedAt = now, Media = media });
			oldest ??= (new LiveMediaRefs.Kept { UploadedAt = now, Media = media }, TimeSpan.Zero);
		}

		var (again, oldestAge) = oldest.Value;
		var id = MessageId.New();
		var metadata = new DocumentMetadata { Mimetype = "application/octet-stream", FileName = "wmn-old-ref.bin" };
		var arrived = LiveChecks.ArrivalAsync(other, id, ct);
		_ = await client.SendDocumentAsync(to, again.Media, metadata, id, cancellationToken: ct);
		var got = await LiveChecks.WithinAsync(arrived, $"the document sent again from a ref {Age(oldestAge)} old");
		Assert.Equal(LiveChecks.Received(other, got, sender, id, new DocumentContent { Media = again.Media, Metadata = metadata }), got);

		await using var resent = new HashingStream(memory: null);
		await other.DownloadMediaAsync(((DocumentContent)got.Content).Media, resent, ct);
		live.Write($"live: a ref {Age(oldestAge)} old was sent again without an upload, and the peer downloaded it");
		Assert.Equal(again.Media.FileSha256.ToArray(), resent.Hash());
	}

	private static string Age(TimeSpan age) => age.TotalDays >= 1 ? $"{age.TotalDays.ToString("F1", CultureInfo.InvariantCulture)} days" : $"{age.TotalHours.ToString("F1", CultureInfo.InvariantCulture)} hours";
}
