using System.Security.Cryptography;
using Google.Protobuf.WellKnownTypes;
using Whatsmeow.Events;
using Whatsmeow.IntegrationTests.Events;
using Whatsmeow.IntegrationTests.Fake;
using Whatsmeow.IntegrationTests.Sessions;
using Whatsmeow.IntegrationTests.Support;
using MessageSource = Whatsmeow.Events.MessageSource;

namespace Whatsmeow.IntegrationTests.Media;

/// <summary>The Media slice through the public client, on the shared fake-mode sidecar (ARCHITECTURE §7.6).</summary>
public sealed class MediaTests(FakeSidecar fixture)
{
	private const int Timeout = 120_000;

	[Fact(Timeout = Timeout)]
	public async Task FullCycle_UploadAFileUnderTheRoot_DownloadsItBackByPathAndToAStream()
	{
		var ct = TestContext.Current.CancellationToken;
		var client = await SessionSetup.ConnectedAsync(fixture, "media-fullcycle", ct);
		var directory = Directory.CreateDirectory(Path.Combine(fixture.MediaRoot, "media-fullcycle")).FullName;
		var content = RandomNumberGenerator.GetBytes(300 * 1024);
		var source = Path.Combine(directory, "photo.jpg");
		await File.WriteAllBytesAsync(source, content, ct);

		var media = await client.UploadMediaFileAsync(source, MediaType.Image, ct);

		// The sidecar read the file in place: no bytes crossed the socket, and the file is as it was.
		MediaChecks.ValidateUploaded(media, MediaType.Image, content, FakeUrl(media));
		Assert.Equal(content, await File.ReadAllBytesAsync(source, ct));
		_ = Assert.Single(fixture.Logs.Named("UploadingByPath"), r => r.Message.Contains("media-fullcycle", StringComparison.Ordinal));
		var upload = await NextSentAsync("media-fullcycle", 0, ct);
		Assert.Equal(new MediaUpload { MediaType = "WhatsApp Image Keys", DirectPath = media.DirectPath }, upload.MediaUpload);

		var copy = Path.Combine(directory, "copy.jpg");
		await client.DownloadMediaFileAsync(media, copy, ct);
		Assert.Equal(content, await File.ReadAllBytesAsync(copy, ct));
		_ = Assert.Single(fixture.Logs.Named("DownloadingByPath"), r => r.Message.Contains("media-fullcycle", StringComparison.Ordinal));
		Assert.Equal(["copy.jpg", "photo.jpg"], Names(directory));

		await MediaChecks.ValidateDownloadsAsync(client, media, content, ct);
	}

	[Fact(Timeout = Timeout)]
	public async Task Upload_AStreamOrAFileOutsideTheRoot_TravelsInChunksAndRoundTrips()
	{
		var ct = TestContext.Current.CancellationToken;
		var client = await SessionSetup.ConnectedAsync(fixture, "media-chunks", ct);
		var outside = Directory.CreateTempSubdirectory("wmn-media-").FullName;
		var content = RandomNumberGenerator.GetBytes((1024 * 1024) + 123);

		using var input = new MemoryStream(content);
		var fromStream = await client.UploadMediaAsync(input, MediaType.Document, ct);
		MediaChecks.ValidateUploaded(fromStream, MediaType.Document, content, FakeUrl(fromStream));

		var file = Path.Combine(outside, "report.pdf");
		await File.WriteAllBytesAsync(file, content, ct);
		var fromFile = await client.UploadMediaFileAsync(file, MediaType.Document, ct);
		MediaChecks.ValidateUploaded(fromFile, MediaType.Document, content, FakeUrl(fromFile));
		Assert.Equal(2, fixture.Logs.Named("UploadingInChunks").Count(r => r.Message.Contains("media-chunks", StringComparison.Ordinal)));

		// Outside the root the client writes the file itself, from chunks, and puts it in place once complete.
		var copy = Path.Combine(outside, "copy.pdf");
		await client.DownloadMediaFileAsync(fromFile, copy, ct);
		Assert.Equal(content, await File.ReadAllBytesAsync(copy, ct));
		Assert.Equal(["copy.pdf", "report.pdf"], Names(outside));
	}

	[Fact(Timeout = Timeout)]
	public async Task Transfer_APathWhoseRootDiffersInCase_GoesInChunksExceptOnWindows()
	{
		var ct = TestContext.Current.CancellationToken;
		var client = await SessionSetup.ConnectedAsync(fixture, "media-case", ct);
		// The root as written in other case: the root itself on a case-insensitive file system (macOS, Windows), a
		// sibling of it on a case-sensitive one. The sidecar compares paths as Go does, by case except on Windows.
		var variant = Path.Combine(Path.GetDirectoryName(fixture.MediaRoot)!, Path.GetFileName(fixture.MediaRoot).ToUpperInvariant(), "media-case");
		_ = Directory.CreateDirectory(variant);
		var content = RandomNumberGenerator.GetBytes(1000);
		var source = Path.Combine(variant, "a.bin");
		await File.WriteAllBytesAsync(source, content, ct);

		var media = await client.UploadMediaFileAsync(source, MediaType.Document, ct);
		await client.DownloadMediaFileAsync(media, Path.Combine(variant, "b.bin"), ct);

		Assert.Equal(content, await File.ReadAllBytesAsync(Path.Combine(variant, "b.bin"), ct));
		var (upload, download) = OperatingSystem.IsWindows() ? ("UploadingByPath", "DownloadingByPath") : ("UploadingInChunks", "DownloadingInChunks");
		_ = Assert.Single(fixture.Logs.Named(upload), r => r.Message.Contains("media-case", StringComparison.Ordinal));
		_ = Assert.Single(fixture.Logs.Named(download), r => r.Message.Contains("media-case", StringComparison.Ordinal));
	}

	[Fact(Timeout = Timeout)]
	public async Task Transfer_AMissingFileOrDirectory_ThrowsTheSameUnderTheRootAndOutside()
	{
		var ct = TestContext.Current.CancellationToken;
		var client = await SessionSetup.ConnectedAsync(fixture, "media-missing", ct);
		var underRoot = Directory.CreateDirectory(Path.Combine(fixture.MediaRoot, "media-missing")).FullName;
		var outside = Directory.CreateTempSubdirectory("wmn-media-").FullName;
		using var input = new MemoryStream([1, 2, 3]);
		var media = await client.UploadMediaAsync(input, MediaType.Document, ct);

		foreach (var directory in new[] { underRoot, outside })
		{
			_ = await Assert.ThrowsAsync<FileNotFoundException>(() => client.UploadMediaFileAsync(Path.Combine(directory, "missing.bin"), MediaType.Document, ct));
			_ = await Assert.ThrowsAsync<DirectoryNotFoundException>(() => client.UploadMediaFileAsync(Path.Combine(directory, "missing", "a.bin"), MediaType.Document, ct));
			_ = await Assert.ThrowsAsync<DirectoryNotFoundException>(() => client.DownloadMediaFileAsync(media, Path.Combine(directory, "missing", "a.bin"), ct));
		}

		Assert.Empty(Names(underRoot));
		Assert.Empty(Names(outside));
	}

	[Fact(Timeout = Timeout)]
	public async Task Transfer_ThroughASymlinkOutOfTheRoot_IsRefusedAndTouchesNothingOutside()
	{
		var ct = TestContext.Current.CancellationToken;
		var client = await SessionSetup.ConnectedAsync(fixture, "media-symlink", ct);
		var directory = Directory.CreateDirectory(Path.Combine(fixture.MediaRoot, "media-symlink")).FullName;
		var outside = Directory.CreateTempSubdirectory("wmn-media-").FullName;
		await File.WriteAllTextAsync(Path.Combine(outside, "secret.txt"), "secret", ct);
		_ = Directory.CreateSymbolicLink(Path.Combine(directory, "out"), outside);
		using var input = new MemoryStream([1, 2, 3]);
		var media = await client.UploadMediaAsync(input, MediaType.Document, ct);

		// The path is under the root as written, so it is handed over; the sidecar resolves it and refuses it.
		_ = await Assert.ThrowsAsync<WhatsmeowValidationException>(() => client.UploadMediaFileAsync(Path.Combine(directory, "out", "secret.txt"), MediaType.Document, ct));
		_ = await Assert.ThrowsAsync<WhatsmeowValidationException>(() => client.DownloadMediaFileAsync(media, Path.Combine(directory, "out", "new.txt"), ct));

		Assert.Equal(["secret.txt"], Names(outside));
		Assert.Equal("secret", await File.ReadAllTextAsync(Path.Combine(outside, "secret.txt"), ct));
	}

	[Fact(Timeout = Timeout)]
	public async Task Download_ACopyThatFailsVerification_ThrowsAndDeliversNothing()
	{
		var ct = TestContext.Current.CancellationToken;
		var client = await SessionSetup.ConnectedAsync(fixture, "media-corrupt", ct);
		var directory = Directory.CreateDirectory(Path.Combine(fixture.MediaRoot, "media-corrupt")).FullName;
		var outside = Directory.CreateTempSubdirectory("wmn-media-").FullName;
		using var input = new MemoryStream(RandomNumberGenerator.GetBytes(200 * 1024));
		var media = await client.UploadMediaAsync(input, MediaType.Image, ct);
		_ = await fixture.Fake.CorruptMediaAsync(new CorruptMediaRequest { DirectPath = media.DirectPath }, cancellationToken: ct);

		foreach (var path in new[] { Path.Combine(directory, "x.jpg"), Path.Combine(outside, "x.jpg") })
		{
			var error = await Assert.ThrowsAsync<WhatsmeowException>(() => client.DownloadMediaFileAsync(media, path, ct));
			Assert.Equal(WhatsmeowErrorCode.Internal, error.ErrorCode);
		}

		using var stream = new MemoryStream();
		_ = await Assert.ThrowsAsync<WhatsmeowException>(() => client.DownloadMediaAsync(media, stream, ct));

		Assert.Equal(0, stream.Length);
		// The sidecar removed its partial file before it answered.
		Assert.Empty(Names(outside));
		Assert.Empty(Names(directory));
	}

	[Fact(Timeout = Timeout)]
	public async Task Download_ExpiredMedia_ThrowsMediaExpiredException()
	{
		var ct = TestContext.Current.CancellationToken;
		var client = await SessionSetup.ConnectedAsync(fixture, "media-expired", ct);
		using var input = new MemoryStream([1, 2, 3]);
		var media = await client.UploadMediaAsync(input, MediaType.Audio, ct);
		_ = await fixture.Fake.ExpireMediaAsync(new ExpireMediaRequest { DirectPath = media.DirectPath, Status = 410 }, cancellationToken: ct);

		var error = await Assert.ThrowsAsync<MediaExpiredException>(() => client.DownloadMediaAsync(media, Stream.Null, ct));

		Assert.Equal(WhatsmeowErrorCode.MediaExpired, error.ErrorCode);
	}

	[Fact(Timeout = Timeout)]
	public async Task MediaRetry_ExpiredMedia_ThePhoneUploadsItAgainAndTheNewRefDownloads()
	{
		var ct = TestContext.Current.CancellationToken;
		var client = await SessionSetup.ConnectedAsync(fixture, "media-retry", ct);
		using var retries = new Received<MediaRetryReceived>(client);
		var content = RandomNumberGenerator.GetBytes(100 * 1024);
		using var input = new MemoryStream(content);
		var media = await client.UploadMediaAsync(input, MediaType.Video, ct);
		_ = await fixture.Fake.ExpireMediaAsync(new ExpireMediaRequest { DirectPath = media.DirectPath, Status = 404 }, cancellationToken: ct);
		_ = await Assert.ThrowsAsync<MediaExpiredException>(() => client.DownloadMediaAsync(media, Stream.Null, ct));
		var upload = await NextSentAsync("media-retry", 0, ct);
		var expired = await NextSentAsync("media-retry", upload.Index, ct);
		var source = new MessageSource
		{
			Chat = new Jid { User = "120363000000000000", Server = "g.us" },
			Sender = new Jid { User = "111", Server = "lid" },
			SenderAlt = null,
			RecipientAlt = null,
			IsFromMe = false,
		};

		await client.RequestMediaRetryAsync(MessageId.From("M1"), source, media, ct);

		var receipt = await NextSentAsync("media-retry", expired.Index, ct);
		Assert.Equal(new MediaRetryReceipt { MessageId = "M1", Chat = "120363000000000000@g.us", Sender = "111@lid" }, receipt.MediaRetryReceipt);

		_ = await fixture.Fake.AnswerMediaRetryAsync(
			new AnswerMediaRetryRequest
			{
				SessionId = "media-retry",
				MessageId = "M1",
				ReuploadFrom = media.DirectPath,
				AnswerTime = Timestamp.FromDateTimeOffset(Deliveries.At),
			},
			cancellationToken: ct
		);
		var answer = await retries.NextAsync(ct);

		var reuploaded = Assert.IsType<MediaRetryResult.Reuploaded>(answer.Result);
		Assert.NotEqual(media.DirectPath, reuploaded.Media.DirectPath, StringComparer.Ordinal);
		Assert.Equal(
			new MediaRetryReceived
			{
				SessionId = SessionId.From("media-retry"),
				Id = MessageId.From("M1"),
				Chat = source.Chat,
				Sender = source.Sender,
				IsFromMe = false,
				Timestamp = Deliveries.At,
				Result = new MediaRetryResult.Reuploaded { Media = media with { DirectPath = reuploaded.Media.DirectPath, Url = null } },
			},
			answer
		);

		using var downloaded = new MemoryStream();
		await client.DownloadMediaAsync(reuploaded.Media, downloaded, ct);
		Assert.Equal(content, downloaded.ToArray());

		// That answer settled the request; asked again, the phone can also have nothing to send.
		await client.RequestMediaRetryAsync(MessageId.From("M1"), source, media, ct);
		_ = await fixture.Fake.AnswerMediaRetryAsync(
			new AnswerMediaRetryRequest { SessionId = "media-retry", MessageId = "M1", ErrorCode = 2, AnswerTime = Timestamp.FromDateTimeOffset(Deliveries.At.AddSeconds(1)) },
			cancellationToken: ct
		);
		Assert.Equal(new MediaRetryResult.Failed { Reason = MediaRetryFailure.NotOnPhone }, (await retries.NextAsync(ct)).Result);
	}

	[Fact(Timeout = Timeout)]
	public async Task Upload_ATransientFailure_IsRetriedAndTheApplicationSeesOneUpload()
	{
		var ct = TestContext.Current.CancellationToken;
		var client = await SessionSetup.ConnectedAsync(fixture, "media-retried", ct);
		_ = await fixture.Fake.FailNextUploadsAsync(new FailNextUploadsRequest { SessionId = "media-retried", Count = 1 }, cancellationToken: ct);
		var content = RandomNumberGenerator.GetBytes(1000);
		using var input = new MemoryStream(content);

		var media = await client.UploadMediaAsync(input, MediaType.Image, ct);

		MediaChecks.ValidateUploaded(media, MediaType.Image, content, FakeUrl(media));
		var failed = await NextSentAsync("media-retried", 0, ct);
		var succeeded = await NextSentAsync("media-retried", failed.Index, ct);
		Assert.Equal(new MediaUpload { MediaType = "WhatsApp Image Keys" }, failed.MediaUpload);
		Assert.Equal(new MediaUpload { MediaType = "WhatsApp Image Keys", DirectPath = media.DirectPath }, succeeded.MediaUpload);
	}

	[Fact(Timeout = Timeout)]
	public async Task Download_Cancelled_ThrowsAndLeavesNothingAtTheDestination()
	{
		var ct = TestContext.Current.CancellationToken;
		var client = await SessionSetup.ConnectedAsync(fixture, "media-cancel", ct);
		var underRoot = Directory.CreateDirectory(Path.Combine(fixture.MediaRoot, "media-cancel")).FullName;
		var outside = Directory.CreateTempSubdirectory("wmn-media-").FullName;
		using var input = new MemoryStream(RandomNumberGenerator.GetBytes(1000));
		var media = await client.UploadMediaAsync(input, MediaType.Image, ct);
		var last = (await NextSentAsync("media-cancel", 0, ct)).Index;

		foreach (var destination in new[] { Path.Combine(underRoot, "x.jpg"), Path.Combine(outside, "x.jpg") })
		{
			_ = await fixture.Fake.HoldNextDownloadAsync(new HoldNextDownloadRequest { SessionId = "media-cancel" }, cancellationToken: ct);
			using var cancel = CancellationTokenSource.CreateLinkedTokenSource(ct);
			var download = client.DownloadMediaFileAsync(media, destination, cancel.Token);

			// The download started and wrote part of the file before the cancel.
			var started = await NextSentAsync("media-cancel", last, ct);
			Assert.NotNull(started.MediaDownload);
			last = started.Index;
			// Meanwhile the destination path never holds a partial file.
			Assert.False(File.Exists(destination));
			await cancel.CancelAsync();

			_ = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => download);
			Assert.False(File.Exists(destination));
		}

		// Outside the root the client cleans its own partial file before it throws.
		Assert.Empty(Names(outside));
	}

	[Fact(Timeout = Timeout)]
	public async Task Upload_Cancelled_Throws()
	{
		var ct = TestContext.Current.CancellationToken;
		var client = await SessionSetup.ConnectedAsync(fixture, "media-cancel-up", ct);
		await using var input = new StallingStream();
		using var cancel = CancellationTokenSource.CreateLinkedTokenSource(ct);

		_ = await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
		{
			var upload = client.UploadMediaAsync(input, MediaType.Document, cancel.Token);
			await input.StalledAsync(ct).ConfigureAwait(false);
			await cancel.CancelAsync().ConfigureAwait(false);
			_ = await upload.ConfigureAwait(false);
		});
	}

	[Fact(Timeout = Timeout)]
	public async Task Transfer_ASessionThatIsNotConnected_ThrowsNotConnected()
	{
		var ct = TestContext.Current.CancellationToken;
		var client = await SessionSetup.LinkedAsync(fixture, "media-offline", ct);
		using var input = new MemoryStream([1]);

		_ = await Assert.ThrowsAsync<NotConnectedException>(() => client.UploadMediaAsync(input, MediaType.Image, ct));
	}

	[Fact(Timeout = Timeout)]
	public async Task Upload_RefusedWhileChunksStillGoOut_ThrowsTheSidecarsReason()
	{
		var ct = TestContext.Current.CancellationToken;
		var client = await SessionSetup.LinkedAsync(fixture, "media-offline-chunks", ct);

		// The sidecar refuses after the header and ends the call while the client still writes chunks: the error is
		// the sidecar's, never the write the ended call cut short.
		for (var i = 0; i < 20; i++)
		{
			await using var input = new GeneratedStream(32L * 1024 * 1024, memory: null);
			_ = await Assert.ThrowsAsync<NotConnectedException>(() => client.UploadMediaAsync(input, MediaType.Document, ct));
		}
	}

	/// <summary>The URL the fake WhatsApp answers an upload with.</summary>
	private static Uri FakeUrl(MediaRef media) => new($"https://fake.whatsapp.net{media.DirectPath}");

	private static string[] Names(string directory) => [.. Directory.EnumerateFileSystemEntries(directory).Select(Path.GetFileName).Order(StringComparer.Ordinal)!];

	/// <summary>The session's first entry after the entry <paramref name="after"/>; each test's session is its own, so 0 is its first.</summary>
	private async Task<Sent> NextSentAsync(string sessionId, ulong after, CancellationToken cancellationToken) =>
		(await fixture.Fake.WaitForSentAsync(new WaitForSentRequest { SessionId = sessionId, After = after }, cancellationToken: cancellationToken).ConfigureAwait(false)).Sent;
}
