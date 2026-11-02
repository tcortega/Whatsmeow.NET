using System.Collections.Immutable;
using Microsoft.Extensions.Time.Testing;
using Whatsmeow.Messaging;
using static Whatsmeow.Testing.Tests.Samples;

namespace Whatsmeow.Testing.Tests;

/// <summary>The fake records polls and votes as the other sends (ADR 0036).</summary>
public sealed class PollCallTests : IAsyncDisposable
{
	private readonly FakeTimeProvider _time = new(Now);
	private readonly FakeWhatsAppClient _fake;

	public PollCallTests() => _fake = new FakeWhatsAppClient(Session, _time);

	public async ValueTask DisposeAsync() => await _fake.DisposeAsync().ConfigureAwait(false);

	[Fact(Timeout = 10_000)]
	public async Task PollsAndVotes_AreRecordedWithTheirArguments_AndAnswerAsASend()
	{
		var ct = TestContext.Current.CancellationToken;
		var id = MessageId.From("3EB0C0FFEE0000000000AB");
		var poll = new Poll { Question = "Lunch?", Options = ["Pizza", "Sushi"], MaxSelectable = 1 };
		var key = new MessageKey { Id = id };
		var options = new SendOptions { Mentions = [Bia] };
		string[] selected = ["Sushi"];

		var sent = await _fake.SendPollAsync(Team, poll, id, options, ct);
		var voted = await _fake.SendPollVoteAsync(Team, key, selected, cancellationToken: ct);
		selected[0] = "Pizza";

		Assert.Equal(new SendResult { Id = id, Chat = Team, Timestamp = Now }, sent);
		Assert.Equal(Team, voted.Chat);
		Assert.Equal([nameof(IWhatsAppClient.SendPollAsync), nameof(IWhatsAppClient.SendPollVoteAsync)], _fake.Calls.Select(static call => call.Member), StringComparer.Ordinal);
		// The poll compares by content, so a test builds the one it expects.
		Assert.Equal(
			new Dictionary<string, object?>(StringComparer.Ordinal)
			{
				["chat"] = Team,
				["poll"] = poll with { Options = ["Pizza", "Sushi"] },
				["id"] = id,
				["options"] = options,
			},
			_fake.Calls[0].Arguments
		);
		Assert.Equal(Team, _fake.Calls[1].Arguments["chat"]);
		Assert.Equal(key, _fake.Calls[1].Arguments["poll"]);
		Assert.Null(_fake.Calls[1].Arguments["id"]);
		// A copy, as the vote was when it was sent.
		Assert.Equal(["Sushi"], (ImmutableArray<string>)_fake.Calls[1].Arguments["selectedOptions"]!, StringComparer.Ordinal);
	}
}
