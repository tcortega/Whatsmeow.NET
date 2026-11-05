using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Whatsmeow.Events;
using Whatsmeow.IntegrationTests.Events;
using Whatsmeow.IntegrationTests.Fake;
using Whatsmeow.IntegrationTests.Sessions;
using Whatsmeow.IntegrationTests.Support;
using MessageSource = Whatsmeow.Events.MessageSource;

namespace Whatsmeow.IntegrationTests.Media;

/// <summary>A malformed phone answer reports a failure without settling the request.</summary>
public sealed class MediaRetryMalformedTests(FakeSidecar fixture)
{
	[Theory(Timeout = 30_000)]
	[InlineData(0, 16)]
	[InlineData(1, 16)]
	[InlineData(11, 16)]
	[InlineData(13, 16)]
	[InlineData(16, 16)]
	[InlineData(12, 0)]
	[InlineData(0, 0)]
	public async Task MediaRetry_MalformedEncryptedAnswer_ReportsFailureThenAcceptsALaterAnswer(int ivLength, int ciphertextLength)
	{
		var ct = TestContext.Current.CancellationToken;
		var session = $"media-malformed-{ivLength}-{ciphertextLength}";
		var client = await SessionSetup.ConnectedAsync(fixture, session, ct);
		using var retries = new Received<MediaRetryReceived>(client);
		var content = "media"u8.ToArray();
		using var input = new MemoryStream(content);
		var media = await client.UploadMediaAsync(input, MediaType.Image, ct);
		var source = new MessageSource
		{
			Chat = Jid.Parse(Deliveries.Group),
			Sender = Jid.Parse(Deliveries.Chat),
			SenderAlt = null,
			RecipientAlt = null,
			IsFromMe = false,
		};
		await client.RequestMediaRetryAsync(MessageId.From("M1"), source, media, ct);

		_ = await fixture.Fake.AnswerMediaRetryAsync(
			new AnswerMediaRetryRequest
			{
				SessionId = session,
				MessageId = "M1",
				Encrypted = new EncryptedMediaRetryAnswer
				{
					Ciphertext = ByteString.CopyFrom(new byte[ciphertextLength]),
					Iv = ByteString.CopyFrom(new byte[ivLength]),
				},
				AnswerTime = Timestamp.FromDateTimeOffset(Deliveries.At),
			},
			cancellationToken: ct
		);
		var expected = new MediaRetryReceived
		{
			SessionId = SessionId.From(session),
			Id = MessageId.From("M1"),
			Chat = source.Chat,
			Sender = source.Sender,
			IsFromMe = false,
			Timestamp = Deliveries.At,
			Result = new MediaRetryResult.Failed { Reason = MediaRetryFailure.Unknown },
		};
		Assert.Equal(expected, await retries.NextAsync(ct));

		// No new request: the malformed answer must have kept the stored media key.
		_ = await fixture.Fake.AnswerMediaRetryAsync(
			new AnswerMediaRetryRequest
			{
				SessionId = session,
				MessageId = "M1",
				ReuploadFrom = media.DirectPath,
				AnswerTime = Timestamp.FromDateTimeOffset(Deliveries.At.AddSeconds(1)),
			},
			cancellationToken: ct
		);
		var recovered = await retries.NextAsync(ct);
		var reuploaded = Assert.IsType<MediaRetryResult.Reuploaded>(recovered.Result);
		Assert.NotEqual(media.DirectPath, reuploaded.Media.DirectPath, StringComparer.Ordinal);
		Assert.Equal(
			expected with
			{
				Timestamp = Deliveries.At.AddSeconds(1),
				Result = new MediaRetryResult.Reuploaded { Media = media with { DirectPath = reuploaded.Media.DirectPath, Url = null } },
			},
			recovered
		);
		using var downloaded = new MemoryStream();
		await client.DownloadMediaAsync(reuploaded.Media, downloaded, ct);
		Assert.Equal(content, downloaded.ToArray());
	}
}
