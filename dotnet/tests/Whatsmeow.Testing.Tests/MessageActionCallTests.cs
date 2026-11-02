using Microsoft.Extensions.Time.Testing;
using Whatsmeow.Messaging;
using static Whatsmeow.Testing.Tests.Samples;

namespace Whatsmeow.Testing.Tests;

/// <summary>The fake records reactions, edits, revokes and the sends with options as the other sends (ADR 0036, ADR 0038).</summary>
public sealed class MessageActionCallTests : IAsyncDisposable
{
	private readonly FakeTimeProvider _time = new(Now);
	private readonly FakeWhatsAppClient _fake;

	public MessageActionCallTests() => _fake = new FakeWhatsAppClient(Session, _time);

	public async ValueTask DisposeAsync() => await _fake.DisposeAsync().ConfigureAwait(false);

	[Fact(Timeout = 10_000)]
	public async Task Actions_AndSendsWithOptions_AreRecordedWithTheirArguments_AndAnswerAsASend()
	{
		var ct = TestContext.Current.CancellationToken;
		var id = MessageId.From("3EB0C0FFEE0000000000AB");
		var target = new MessageKey { Id = MessageId.From("T1"), Sender = Bia };
		var options = new SendOptions { ReplyTo = new MessageReply { Id = MessageId.From("Q1"), Sender = Bia, QuotedText = "?" }, Mentions = [Bia] };
		var media = await _fake.UploadMediaAsync(new MemoryStream([1]), MediaType.Image, ct);

		var reacted = await _fake.SendReactionAsync(Team, target, "👍", id, ct);
		_ = await _fake.EditMessageAsync(Ana, MessageId.From("T2"), "fixed", cancellationToken: ct);
		_ = await _fake.RevokeMessageAsync(Team, target, cancellationToken: ct);
		_ = await _fake.SendTextAsync(Team, "@5511999990002 hi", options: options, cancellationToken: ct);
		_ = await _fake.SendImageAsync(Team, media, new ImageMetadata { Caption = "look" }, options: options, cancellationToken: ct);

		Assert.Equal(new SendResult { Id = id, Chat = Team, Timestamp = Now }, reacted);
		Assert.Equal(
			[
				nameof(IWhatsAppClient.UploadMediaAsync),
				nameof(IWhatsAppClient.SendReactionAsync),
				nameof(IWhatsAppClient.EditMessageAsync),
				nameof(IWhatsAppClient.RevokeMessageAsync),
				nameof(IWhatsAppClient.SendTextAsync),
				nameof(IWhatsAppClient.SendImageAsync),
			],
			_fake.Calls.Select(static call => call.Member),
			StringComparer.Ordinal
		);
		Assert.Equal(
			new Dictionary<string, object?>(StringComparer.Ordinal) { ["chat"] = Team, ["target"] = target, ["emoji"] = "👍", ["id"] = id },
			_fake.Calls[1].Arguments
		);
		Assert.Equal(
			new Dictionary<string, object?>(StringComparer.Ordinal) { ["chat"] = Ana, ["target"] = MessageId.From("T2"), ["text"] = "fixed", ["id"] = null },
			_fake.Calls[2].Arguments
		);
		Assert.Equal(new Dictionary<string, object?>(StringComparer.Ordinal) { ["chat"] = Team, ["target"] = target, ["id"] = null }, _fake.Calls[3].Arguments);
		// The options compare by content, so a test builds the ones it expects.
		Assert.Equal(
			new Dictionary<string, object?>(StringComparer.Ordinal)
			{
				["chat"] = Team,
				["text"] = "@5511999990002 hi",
				["options"] = options with { Mentions = [Bia] },
				["id"] = null,
			},
			_fake.Calls[4].Arguments
		);
		Assert.Equal(
			new Dictionary<string, object?>(StringComparer.Ordinal)
			{
				["chat"] = Team,
				["image"] = media,
				["metadata"] = new ImageMetadata { Caption = "look" },
				["options"] = options,
				["id"] = null,
			},
			_fake.Calls[5].Arguments
		);
	}

	/// <summary>
	/// Each typed send is one method, so these calls compile, and this is what they bind: a third argument of
	/// <see langword="null"/> or <see langword="default"/> is the id, a message id is the id, and the options go by name
	/// or after the id (ADR 0038).
	/// </summary>
	[Fact(Timeout = 10_000)]
	public async Task TypedSends_BindNullDefaultAndAnIdToTheId_AndTheOptionsByNameOrAfterTheId()
	{
		var ct = TestContext.Current.CancellationToken;
#pragma warning disable CA1859 // Justification: the interface's binding is under test, not the fake's
		IWhatsAppClient client = _fake;
#pragma warning restore CA1859
		var id = MessageId.From("3EB0C0FFEE0000000000AC");
		var options = new SendOptions { Mentions = [Bia] };
		var media = await _fake.UploadMediaAsync(new MemoryStream([1]), MediaType.Image, ct);
		var image = new ImageMetadata();

#pragma warning disable MA0003 // Justification: an unnamed null or default is the call shape under test
		_ = await client.SendTextAsync(Ana, "null", null, cancellationToken: ct);
		_ = await client.SendTextAsync(Ana, "default", default, cancellationToken: ct);
		_ = await client.SendTextAsync(Ana, "id", id, cancellationToken: ct);
		_ = await client.SendTextAsync(Ana, "named", options: options, cancellationToken: ct);
		_ = await client.SendTextAsync(Ana, "both", id, options, ct);
		_ = await _fake.SendTextAsync(Ana, "on the fake", null, cancellationToken: ct);
		_ = await client.SendImageAsync(Ana, media, image, null, cancellationToken: ct);
		_ = await client.SendImageAsync(Ana, media, image, default, cancellationToken: ct);
		_ = await client.SendVideoAsync(Ana, media, new VideoMetadata(), id, cancellationToken: ct);
		_ = await client.SendAudioAsync(Ana, media, new AudioMetadata(), options: options, cancellationToken: ct);
		_ = await client.SendDocumentAsync(Ana, media, new DocumentMetadata(), id, options, ct);
		_ = await client.SendStickerAsync(Ana, media, new StickerMetadata(), null, cancellationToken: ct);
#pragma warning restore MA0003

		Assert.Equal(
			[
				(null, null),
				(null, null),
				(id, null),
				(null, options),
				(id, options),
				(null, null),
				(null, null),
				(null, null),
				(id, null),
				(null, options),
				(id, options),
				(null, null),
			],
			_fake.Calls.Skip(1).Select(static call => ((MessageId?)call.Arguments["id"], (SendOptions?)call.Arguments["options"]))
		);
	}

	[Fact(Timeout = 10_000)]
	public async Task FailNext_OfASendWithOptionsAndOfARevoke_FailsThem()
	{
		var ct = TestContext.Current.CancellationToken;
		var refused = new WhatsAppErrorException("refused") { Code = 403 };
		_fake.FailNext(nameof(IWhatsAppClient.SendTextAsync), refused);
		_fake.FailNext(nameof(IWhatsAppClient.RevokeMessageAsync), refused);

		Assert.Same(refused, await Assert.ThrowsAsync<WhatsAppErrorException>(() => _fake.SendTextAsync(Ana, "hi", options: new SendOptions(), cancellationToken: ct)));
		Assert.Same(refused, await Assert.ThrowsAsync<WhatsAppErrorException>(() => _fake.RevokeMessageAsync(Team, new MessageKey { Id = MessageId.From("T1"), Sender = Bia }, cancellationToken: ct)));
		_ = await _fake.SendTextAsync(Ana, "again", cancellationToken: ct);
	}
}
