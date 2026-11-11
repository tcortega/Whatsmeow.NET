using System.Collections.Immutable;
using Microsoft.Extensions.Time.Testing;
using Whatsmeow.Messaging;
using static Whatsmeow.Testing.Tests.Samples;
using E2E = Whatsmeow.Proto.WAWebProtobufsE2E;

namespace Whatsmeow.Testing.Tests;

/// <summary>
/// The fake refuses, as <see cref="WhatsmeowValidationException"/>, the arguments every session refuses before WhatsApp
/// is involved, and records nothing for them, as for a cancelled call.
/// </summary>
public sealed class ArgumentCallTests : IAsyncDisposable
{
	private static readonly Poll s_lunch = new() { Question = "Lunch?", Options = ["Pizza", "Sushi"], MaxSelectable = 1 };

	private readonly FakeTimeProvider _time = new(Now);
	private readonly FakeWhatsAppClient _fake;

	public ArgumentCallTests() => _fake = new FakeWhatsAppClient(Session, _time);

	public async ValueTask DisposeAsync() => await _fake.DisposeAsync().ConfigureAwait(false);

	[Theory(Timeout = 10_000)]
	[InlineData("", "not a JID")]
	[InlineData("s.whatsapp.net", "not a JID")]
	[InlineData("5511999990001:3@s.whatsapp.net", "names a device; send to the user")]
	[InlineData("100000000000001:2@lid", "names a device; send to the user")]
	[InlineData("status@broadcast", "not a user or a group")]
	[InlineData("120363000000000001@newsletter", "not a user or a group")]
	public async Task EverySendAndMarkRead_ToAChatThatIsNoUserOrGroup_IsRefused_AndNotRecorded(string text, string rule)
	{
		var ct = TestContext.Current.CancellationToken;
		var chat = text.Length == 0 ? default : Jid.Parse(text);
		var media = await _fake.UploadMediaAsync(new MemoryStream([1]), MediaType.Image, ct);
		var key = new MessageKey { Id = MessageId.From("T1") };
		Func<Task>[] calls =
		[
			() => _fake.SendTextAsync(chat, "hi", cancellationToken: ct),
			() => _fake.SendImageAsync(chat, media, new ImageMetadata(), cancellationToken: ct),
			() => _fake.SendVideoAsync(chat, media, new VideoMetadata(), cancellationToken: ct),
			() => _fake.SendAudioAsync(chat, media, new AudioMetadata(), cancellationToken: ct),
			() => _fake.SendDocumentAsync(chat, media, new DocumentMetadata(), cancellationToken: ct),
			() => _fake.SendStickerAsync(chat, media, new StickerMetadata(), cancellationToken: ct),
			() => _fake.SendReactionAsync(chat, key, "👍", cancellationToken: ct),
			() => _fake.EditMessageAsync(chat, MessageId.From("T1"), "fixed", cancellationToken: ct),
			() => _fake.RevokeMessageAsync(chat, key, cancellationToken: ct),
			() => _fake.SendPollAsync(chat, s_lunch, cancellationToken: ct),
			() => _fake.SendPollVoteAsync(chat, key, ["Sushi"], cancellationToken: ct),
#pragma warning disable WMN1001 // Justification: the raw send is a send like the others
			() => _fake.SendRawAsync(chat, new E2E.Message { Conversation = "raw" }, cancellationToken: ct),
#pragma warning restore WMN1001
			() => _fake.MarkReadAsync(chat, [MessageId.From("T1")], Bia, ct),
		];

		foreach (var call in calls)
		{
			var error = await Assert.ThrowsAsync<WhatsmeowValidationException>(call);
			Assert.Equal(WhatsmeowErrorCode.InvalidArgument, error.ErrorCode);
			Assert.Contains(rule, error.Message, StringComparison.Ordinal);
			// As the sidecar's, the message names the rule, never the JID (ADR 0039).
			if (chat.User.Length > 0)
				Assert.DoesNotContain(chat.User, error.Message, StringComparison.Ordinal);
		}

		Assert.Equal(nameof(IWhatsAppClient.UploadMediaAsync), Assert.Single(_fake.Calls).Member);
	}

	[Theory(Timeout = 10_000)]
	[InlineData(nameof(IWhatsAppClient.SendTextAsync))]
	[InlineData(nameof(IWhatsAppClient.EditMessageAsync))]
	public async Task TextOfASendOrAnEdit_ThatIsEmpty_IsRefused_AndNotRecorded(string member)
	{
		var ct = TestContext.Current.CancellationToken;
		Func<Task> send = member switch
		{
			nameof(IWhatsAppClient.SendTextAsync) => () => _fake.SendTextAsync(Ana, "", cancellationToken: ct),
			_ => () => _fake.EditMessageAsync(Ana, MessageId.From("T1"), "", cancellationToken: ct),
		};

		var error = await Assert.ThrowsAsync<WhatsmeowValidationException>(send);

		Assert.Equal(WhatsmeowErrorCode.InvalidArgument, error.ErrorCode);
		Assert.Contains("the text is empty", error.Message, StringComparison.Ordinal);
		Assert.Empty(_fake.Calls);
	}

	[Theory(Timeout = 10_000)]
	[InlineData("5511999990001@s.whatsapp.net", null, null, "no message ids")]
	[InlineData("120363025246125486@g.us", null, "5511999990002@s.whatsapp.net", "no message ids")]
	[InlineData("120363025246125486@g.us", "M1", null, "a group chat needs the messages' sender")]
	public async Task MarkRead_WithoutIds_OrInAGroupWithoutTheSender_IsRefused_AndNotRecorded(string chat, string? id, string? sender, string rule)
	{
		ImmutableArray<MessageId> ids = id is null ? [] : [MessageId.From(id)];
		Jid? from = sender is null ? null : Jid.Parse(sender);

		var error = await Assert.ThrowsAsync<WhatsmeowValidationException>(() => _fake.MarkReadAsync(Jid.Parse(chat), ids, from, TestContext.Current.CancellationToken));

		Assert.Equal(WhatsmeowErrorCode.InvalidArgument, error.ErrorCode);
		Assert.Contains(rule, error.Message, StringComparison.Ordinal);
		Assert.Empty(_fake.Calls);
	}

	/// <summary>The options are given as one text split on <c>|</c>; none is the poll's default, unset options.</summary>
	[Theory(Timeout = 10_000)]
	[InlineData("", "Pizza|Sushi", 1, "the question is empty")]
	[InlineData("Lunch?", "Pizza", 1, "a poll needs at least two options")]
	[InlineData("Lunch?", null, 1, "a poll needs at least two options")]
	[InlineData("Lunch?", "Pizza|", 1, "an option is empty")]
	[InlineData("Lunch?", "Pizza|Pizza", 1, "an option is repeated")]
	[InlineData("Lunch?", "Pizza|Sushi", 0, "is not from 1 to the 2 options")]
	[InlineData("Lunch?", "Pizza|Sushi", -1, "is not from 1 to the 2 options")]
	[InlineData("Lunch?", "Pizza|Sushi", 3, "is not from 1 to the 2 options")]
	public async Task Poll_ThatBreaksAPollsRule_IsRefused_AndNotRecorded(string question, string? options, int maxSelectable, string rule)
	{
		var poll = new Poll
		{
			Question = question,
			Options = options is null ? default : [.. options.Split('|')],
			MaxSelectable = maxSelectable,
		};

		var error = await Assert.ThrowsAsync<WhatsmeowValidationException>(() => _fake.SendPollAsync(Team, poll, cancellationToken: TestContext.Current.CancellationToken));

		Assert.Equal(WhatsmeowErrorCode.InvalidArgument, error.ErrorCode);
		Assert.Contains(rule, error.Message, StringComparison.Ordinal);
		Assert.Empty(_fake.Calls);
	}

	[Theory(Timeout = 10_000)]
	[InlineData("Sushi|", "an option is empty")]
	[InlineData("Sushi|Sushi", "an option is repeated")]
	public async Task Vote_WithAnEmptyOrARepeatedOption_IsRefused_AndNotRecorded(string selected, string rule)
	{
		var key = new MessageKey { Id = MessageId.From("P1") };

		var error = await Assert.ThrowsAsync<WhatsmeowValidationException>(() => _fake.SendPollVoteAsync(Team, key, selected.Split('|'), cancellationToken: TestContext.Current.CancellationToken));

		Assert.Equal(WhatsmeowErrorCode.InvalidArgument, error.ErrorCode);
		Assert.Contains(rule, error.Message, StringComparison.Ordinal);
		Assert.Empty(_fake.Calls);
	}

	/// <summary>Each member's arguments at the edge of its rules: a user by LID, a group, a text of one space, a mark-read in a group with the sender, a poll whose limit is its option count, and a vote that selects nothing.</summary>
	[Fact(Timeout = 10_000)]
	public async Task CallsAtTheEdgeOfEachRule_AreRecorded()
	{
		var ct = TestContext.Current.CancellationToken;
		var lid = Jid.Parse("100000000000001@lid");
		var key = new MessageKey { Id = MessageId.From("P1") };

		_ = await _fake.SendTextAsync(lid, " ", cancellationToken: ct);
		_ = await _fake.EditMessageAsync(Team, MessageId.From("T1"), " ", cancellationToken: ct);
		await _fake.MarkReadAsync(Team, [MessageId.From("M1")], Bia, ct);
		await _fake.MarkReadAsync(lid, [MessageId.From("M2")], cancellationToken: ct);
		_ = await _fake.SendPollAsync(Team, s_lunch with { MaxSelectable = 2 }, cancellationToken: ct);
		_ = await _fake.SendPollVoteAsync(Team, key, [], cancellationToken: ct);

		Assert.Equal(
			[
				nameof(IWhatsAppClient.SendTextAsync),
				nameof(IWhatsAppClient.EditMessageAsync),
				nameof(IWhatsAppClient.MarkReadAsync),
				nameof(IWhatsAppClient.MarkReadAsync),
				nameof(IWhatsAppClient.SendPollAsync),
				nameof(IWhatsAppClient.SendPollVoteAsync),
			],
			_fake.Calls.Select(static call => call.Member),
			StringComparer.Ordinal
		);
	}

	[Fact(Timeout = 10_000)]
	public async Task AnEmptyGroupPhoto_IsRefused_AndNotRecorded()
	{
		var ct = TestContext.Current.CancellationToken;

		var error = await Assert.ThrowsAsync<WhatsmeowValidationException>(() => _fake.SetGroupPhotoAsync(Team, new MemoryStream(), ct));

		Assert.Equal(WhatsmeowErrorCode.InvalidArgument, error.ErrorCode);
		Assert.Contains("the photo is empty", error.Message, StringComparison.Ordinal);
		Assert.Empty(_fake.Calls);
	}
}
