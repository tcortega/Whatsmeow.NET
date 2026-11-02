using Whatsmeow.Events;
using Whatsmeow.IntegrationTests.Messaging;
using Whatsmeow.Messaging;

namespace Whatsmeow.LiveTests;

/// <summary>
/// Polls (#31) between the live account and the peer, through real WhatsApp: a poll each way, and a vote on it from the
/// other side, which its sender gets decrypted. The votes are compared with the fake-mode tests' records
/// (<see cref="MessageChecks.Vote"/>).
/// </summary>
[Collection(nameof(Live))]
public sealed class LivePollTests(LiveSidecar live, LivePeer peer)
{
	private static readonly Poll s_lunch = new()
	{
		Question = "wmn Lunch?",
		Options = ["Pizza", "Sushi", "Tacos"],
		MaxSelectable = 2,
	};

	/// <summary>
	/// The live session sends a poll; the peer gets it, and votes on it naming it as it got it; the live session gets
	/// the vote decrypted, on its own poll, and tells which options it selected.
	/// </summary>
	[LiveFact(NeedsPeer = true)]
	public async Task Live_APollToThePeer_ThePeerVotes_AndTheLiveSessionGetsTheVoteDecrypted()
	{
		var ct = TestContext.Current.CancellationToken;
		var client = await live.LiveAsync(ct);
		var other = await peer.ConnectedAsync(live, ct);
		var liveAccount = await live.AccountAsync(ct);
		var peerAccount = await peer.AccountAsync(ct);

		var vote = await PollThenVoteAsync(client, liveAccount, other, peerAccount, ["Sushi", "Tacos"], ct);

		Assert.Equal<bool>([false, true, true], s_lunch.Options.Select(((PollVoteContent)vote.Content).Selects));
	}

	/// <summary>The same the other way: the peer's poll, the live session's vote, which the peer gets decrypted.</summary>
	[LiveFact(NeedsPeer = true)]
	public async Task Live_APollFromThePeer_TheLiveSessionVotes_AndThePeerGetsTheVoteDecrypted()
	{
		var ct = TestContext.Current.CancellationToken;
		var client = await live.LiveAsync(ct);
		var other = await peer.ConnectedAsync(live, ct);
		var liveAccount = await live.AccountAsync(ct);
		var peerAccount = await peer.AccountAsync(ct);

		var vote = await PollThenVoteAsync(other, peerAccount, client, liveAccount, ["Pizza"], ct);

		Assert.Equal<bool>([true, false, false], s_lunch.Options.Select(((PollVoteContent)vote.Content).Selects));
	}

	/// <summary>
	/// <paramref name="author"/> sends <see cref="s_lunch"/> to <paramref name="voter"/>, which gets it as a poll and votes
	/// <paramref name="options"/> on it in the chat it got it in; returns the vote <paramref name="author"/> got, checked.
	/// </summary>
	private async Task<MessageReceived> PollThenVoteAsync(
		IWhatsAppClient author,
		(Jid Chat, Jid? Lid) authorAccount,
		IWhatsAppClient voter,
		(Jid Chat, Jid? Lid) voterAccount,
		string[] options,
		CancellationToken ct
	)
	{
		var pollId = MessageId.New();
		var polled = LiveChecks.ArrivalAsync(voter, pollId, ct);
		_ = await author.SendPollAsync(voterAccount.Chat, s_lunch, pollId, cancellationToken: ct).ConfigureAwait(false);
		var poll = await LiveChecks.WithinAsync(polled, "the poll").ConfigureAwait(false);
		Assert.Equal(LiveChecks.Received(voter, poll, authorAccount, pollId, new PollContent { Poll = s_lunch }), poll);

		var voteId = MessageId.New();
		var voted = LiveChecks.ArrivalAsync(author, voteId, ct);
		_ = await voter.SendPollVoteAsync(poll.Source.Chat, new MessageKey { Id = pollId, Sender = poll.Source.Sender with { Device = 0 } }, options, voteId, ct).ConfigureAwait(false);
		var vote = await LiveChecks.WithinAsync(voted, "the vote").ConfigureAwait(false);
		live.Write(
			$"live: a vote on a poll from a {LiveChecks.Form(poll.Source.Sender)} in a {LiveChecks.Form(poll.Source.Chat)} chat came to the poll's author in a {LiveChecks.Form(vote.Source.Chat)} chat, {vote.Content switch { PollVoteContent { IsUndecryptable: true } => "undecryptable", PollVoteContent => "decrypted", var other => $"as {other.GetType().Name}" }}"
		);

		// The author's own poll: no sender.
		Assert.Equal(LiveChecks.Received(author, vote, voterAccount, voteId, MessageChecks.Vote(new MessageKey { Id = pollId }, options)), vote);
		return vote;
	}
}
