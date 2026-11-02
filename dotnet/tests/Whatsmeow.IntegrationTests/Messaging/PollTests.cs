using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Whatsmeow.Events;
using Whatsmeow.IntegrationTests.Events;
using Whatsmeow.IntegrationTests.Fake;
using Whatsmeow.IntegrationTests.Sessions;
using Whatsmeow.IntegrationTests.Support;
using Whatsmeow.Messaging;
using Whatsmeow.Sessions;
using E2E = Whatsmeow.Proto.WAWebProtobufsE2E;
using MessageKey = Whatsmeow.Messaging.MessageKey;
using MessageSource = Whatsmeow.Events.MessageSource;
using WACommon = Whatsmeow.Proto.WACommon;

namespace Whatsmeow.IntegrationTests.Messaging;

/// <summary>
/// Polls and their votes through the public client, on the shared fake-mode sidecar. The fake WhatsApp encrypts and
/// decrypts votes with whatsmeow's own message secret code and the session's store, so a vote the account sends is
/// shown recorded by delivering it back as from another of its devices, and decrypting there. What WhatsApp got is read
/// from the fake's log by condition; "nothing was sent" shows by order.
/// </summary>
public sealed class PollTests(FakeSidecar fixture)
{
	private const int Timeout = 120_000;

	private static readonly Jid s_peer = Jid.Parse("900000401@lid");
	private static readonly Jid s_group = Jid.Parse("120363000000000401@g.us");
	private static readonly Jid s_member = Jid.Parse("900000402@lid");
	private static readonly Jid s_other = Jid.Parse("900000403@lid");

	private static readonly Poll s_lunch = new()
	{
		Question = "Lunch?",
		Options = ["Pizza", "Sushi", "Tacos"],
		MaxSelectable = 2,
	};

	[Fact(Timeout = Timeout)]
	public async Task SendPoll_WhatsAppGetsThePollWithItsSecret_AndTheResultIsAsForText()
	{
		var ct = TestContext.Current.CancellationToken;
		const string Session = "polls-send";
		var client = await ConnectedAsync(Session, 1, ct);
		var id = MessageId.New();

		var result = await client.SendPollAsync(s_peer, s_lunch, id, cancellationToken: ct);

		var sent = (await NextAsync(Session, 0, 1, ct))[0].Message;
		Assert.Equal(32, sent.Message.MessageContextInfo.MessageSecret.Length);
		Assert.Equal(
			new E2E.Message
			{
				PollCreationMessage = new E2E.PollCreationMessage
				{
					Name = "Lunch?",
					Options = { Option("Pizza"), Option("Sushi"), Option("Tacos") },
					SelectableOptionsCount = 2,
				},
				MessageContextInfo = new E2E.MessageContextInfo { MessageSecret = sent.Message.MessageContextInfo.MessageSecret },
			},
			sent.Message
		);
		Assert.Equal(new SendResult { Id = id, Chat = s_peer, Timestamp = sent.MessageTime.ToDateTimeOffset() }, result);
	}

	[Fact(Timeout = Timeout)]
	public async Task SendPoll_Malformed_ThrowsValidation_AndSendsNothing()
	{
		var ct = TestContext.Current.CancellationToken;
		const string Session = "polls-malformed";
		var client = await ConnectedAsync(Session, 2, ct);

		Poll[] malformed =
		[
			s_lunch with { Options = ["Pizza"] },
			s_lunch with { Options = ["Pizza", "Pizza"] },
			s_lunch with { Options = ["Pizza", ""] },
			s_lunch with { Question = "" },
			s_lunch with { MaxSelectable = 0 },
			s_lunch with { MaxSelectable = -1 },
			s_lunch with { MaxSelectable = 4 },
		];
		foreach (var poll in malformed)
			_ = await Assert.ThrowsAsync<WhatsmeowValidationException>(() => client.SendPollAsync(s_peer, poll, cancellationToken: ct));

		var votes = await Assert.ThrowsAsync<WhatsmeowValidationException>(() =>
			client.SendPollVoteAsync(s_peer, new MessageKey { Id = MessageId.From("P1") }, ["Pizza", "Pizza"], cancellationToken: ct)
		);
		Assert.Equal(WhatsmeowErrorCode.InvalidArgument, votes.ErrorCode);

		// Nothing went out: the next message node is the text after.
		var next = await client.SendTextAsync(s_peer, "next", cancellationToken: ct);
		Assert.Equal(next.Id.Value, (await NextAsync(Session, 0, 1, ct))[0].Message.Id);
	}

	[Fact(Timeout = Timeout)]
	public async Task Vote_OnTheSessionsOwnPoll_WhatsAppGetsAVoteThatDecrypts_AndNoOptionsClearsIt()
	{
		var ct = TestContext.Current.CancellationToken;
		const string Session = "polls-vote-own";
		var client = await LinkedAsync(Session, 3, ct);
		using var received = new Received<MessageReceived>(client);
		await ConnectAsync(client, ct);
		var poll = await client.SendPollAsync(s_peer, s_lunch, cancellationToken: ct);
		var own = new MessageKey { Id = poll.Id };

		var vote = await client.SendPollVoteAsync(s_peer, own, ["Sushi", "Tacos"], cancellationToken: ct);
		var cleared = await client.SendPollVoteAsync(s_peer, own, [], cancellationToken: ct);

		var sent = await NextAsync(Session, 0, 3, ct);
		Assert.Equal(new WACommon.MessageKey { RemoteJID = s_peer.ToString(), FromMe = true, ID = poll.Id.Value }, sent[1].Message.Message.PollUpdateMessage.PollCreationMessageKey);
		Assert.Equal(new SendResult { Id = vote.Id, Chat = s_peer, Timestamp = sent[1].Message.MessageTime.ToDateTimeOffset() }, vote);

		// Another of the account's devices gets each vote as sent, and decrypts it with the poll's secret.
		var self = Lid(3);
		await DeliverAsync(Session, vote.Id.Value, s_peer, self, sent[1].Message.Message, ct, isFromMe: true);
		await DeliverAsync(Session, cleared.Id.Value, s_peer, self, sent[2].Message.Message, ct, isFromMe: true);

		var votes = await received.NextAsync(2, ct);
		var selected = new PollVoteContent { Poll = own, SelectedOptionHashes = [Hash("Sushi"), Hash("Tacos")] };
		Assert.Equal(Received(Session, vote.Id.Value, s_peer, self, selected, isFromMe: true), votes[0]);
		Assert.Equal(Received(Session, cleared.Id.Value, s_peer, self, new PollVoteContent { Poll = own }, isFromMe: true), votes[1]);
	}

	[Fact(Timeout = Timeout)]
	public async Task Receive_APoll_TheHandlerGetsTheQuestionTheOptionsAndTheMaximum()
	{
		var ct = TestContext.Current.CancellationToken;
		const string Session = "polls-receive";
		var client = await LinkedAsync(Session, 4, ct);
		using var received = new Received<MessageReceived>(client);
		await ConnectAsync(client, ct);

		var v1 = PollMessage(2);
		// A later version, as WhatsApp sends a single-choice poll, with no limit: every option may be selected.
		var v3 = new E2E.Message { PollCreationMessageV3 = PollMessage(0).PollCreationMessage, MessageContextInfo = v1.MessageContextInfo };
		var quiz = PollMessage(1);
		quiz.PollCreationMessage.PollType = E2E.PollType.Quiz;

		await DeliverAsync(Session, "R1", s_group, s_member, v1, ct);
		await DeliverAsync(Session, "R2", s_group, s_member, v3, ct);
		await DeliverAsync(Session, "R3", s_group, s_member, quiz, ct);

		var polls = await received.NextAsync(3, ct);
		Assert.Equal(Received(Session, "R1", s_group, s_member, new PollContent { Poll = s_lunch }), polls[0]);
		Assert.Equal(Received(Session, "R2", s_group, s_member, new PollContent { Poll = s_lunch with { MaxSelectable = 3 } }), polls[1]);
		// A quiz is no poll this version maps.
		Assert.Equal(Received(Session, "R3", s_group, s_member, new UnknownContent()), polls[2]);
	}

	[Fact(Timeout = Timeout)]
	public async Task Receive_VotesOnTheSessionsPoll_EachIsDeliveredDecrypted_AndTellsWhichOptionsWereSelected()
	{
		var ct = TestContext.Current.CancellationToken;
		const string Session = "polls-receive-votes-own";
		var client = await LinkedAsync(Session, 5, ct);
		using var received = new Received<MessageReceived>(client);
		await ConnectAsync(client, ct);
		var poll = await client.SendPollAsync(s_group, s_lunch, cancellationToken: ct);
		// whatsmeow named the account by its phone number in a group it knows no LID addressing of.
		var account = Jid.Parse(SessionSetup.Jid(Session)) with { Device = 0 };

		await DeliverVoteAsync(Session, "V1", s_group, s_member, poll.Id, account, ["Pizza"], ct);
		await DeliverVoteAsync(Session, "V2", s_group, s_member, poll.Id, account, ["Sushi", "Tacos"], ct);

		var votes = await received.NextAsync(2, ct);
		var key = new MessageKey { Id = poll.Id };
		Assert.Equal(Received(Session, "V1", s_group, s_member, new PollVoteContent { Poll = key, SelectedOptionHashes = [Hash("Pizza")] }), votes[0]);
		Assert.Equal(Received(Session, "V2", s_group, s_member, new PollVoteContent { Poll = key, SelectedOptionHashes = [Hash("Sushi"), Hash("Tacos")] }), votes[1]);

		// The application holds the poll's options, and tells which the latest vote selected.
		var latest = (PollVoteContent)votes[1].Content;
		Assert.Equal<bool>([false, true, true], s_lunch.Options.Select(latest.Selects));
	}

	[Fact(Timeout = Timeout)]
	public async Task Receive_AVoteOnAReceivedPoll_IsDeliveredDecrypted_WithThePollsSender()
	{
		var ct = TestContext.Current.CancellationToken;
		const string Session = "polls-receive-votes-other";
		var client = await LinkedAsync(Session, 6, ct);
		using var received = new Received<MessageReceived>(client);
		await ConnectAsync(client, ct);

		await DeliverAsync(Session, "R1", s_group, s_member, PollMessage(2), ct);
		await DeliverVoteAsync(Session, "V1", s_group, s_other, MessageId.From("R1"), s_member, ["Tacos"], ct);

		var events = await received.NextAsync(2, ct);
		Assert.Equal(
			Received(Session, "V1", s_group, s_other, new PollVoteContent { Poll = new MessageKey { Id = MessageId.From("R1"), Sender = s_member }, SelectedOptionHashes = [Hash("Tacos")] }),
			events[1]
		);
	}

	[Fact(Timeout = Timeout)]
	public async Task Receive_AVoteTheSidecarCannotDecrypt_IsStillDelivered_MarkedUndecryptable()
	{
		var ct = TestContext.Current.CancellationToken;
		const string Session = "polls-receive-undecryptable";
		var client = await LinkedAsync(Session, 7, ct);
		using var received = new Received<MessageReceived>(client);
		await ConnectAsync(client, ct);

		// A vote on a poll this device never saw, as on one that predates it.
		var vote = new E2E.Message
		{
			PollUpdateMessage = new E2E.PollUpdateMessage
			{
				PollCreationMessageKey = new WACommon.MessageKey { RemoteJID = s_group.ToString(), FromMe = false, ID = "OLD1", Participant = s_other.ToString() },
				Vote = new E2E.PollEncValue { EncPayload = ByteString.CopyFrom(RandomNumberGenerator.GetBytes(48)), EncIV = ByteString.CopyFrom(RandomNumberGenerator.GetBytes(12)) },
			},
		};
		await DeliverAsync(Session, "V1", s_group, s_member, vote, ct);

		Assert.Equal(
			Received(Session, "V1", s_group, s_member, new PollVoteContent { Poll = new MessageKey { Id = MessageId.From("OLD1"), Sender = s_other }, IsUndecryptable = true }),
			await received.NextAsync(ct)
		);
	}

	[Fact(Timeout = Timeout)]
	public async Task Vote_OnAPollWithoutItsSecret_ThrowsPollSecretNotFound_AndSendsNothing()
	{
		var ct = TestContext.Current.CancellationToken;
		const string Session = "polls-vote-no-secret";
		var client = await ConnectedAsync(Session, 8, ct);

		var refused = await Assert.ThrowsAsync<WhatsmeowException>(() =>
			client.SendPollVoteAsync(s_group, new MessageKey { Id = MessageId.From("OLD1"), Sender = s_member }, ["Pizza"], cancellationToken: ct)
		);
		Assert.Equal(WhatsmeowErrorCode.PollSecretNotFound, refused.ErrorCode);

		var next = await client.SendTextAsync(s_peer, "next", cancellationToken: ct);
		Assert.Equal(next.Id.Value, (await NextAsync(Session, 0, 1, ct))[0].Message.Id);
	}

	[Fact(Timeout = Timeout)]
	public async Task SendPollAndVote_ARepeatOfTheirIdsReturnsTheFirstResult_AndSendsNothing()
	{
		var ct = TestContext.Current.CancellationToken;
		const string Session = "polls-repeat";
		var client = await ConnectedAsync(Session, 9, ct);
		var pollId = MessageId.New();
		var voteId = MessageId.New();

		var poll = await client.SendPollAsync(s_peer, s_lunch, pollId, cancellationToken: ct);
		var vote = await client.SendPollVoteAsync(s_peer, new MessageKey { Id = pollId }, ["Pizza"], voteId, ct);

		Assert.Equal(poll, await client.SendPollAsync(s_peer, s_lunch with { Question = "Dinner?" }, pollId, cancellationToken: ct));
		Assert.Equal(vote, await client.SendPollVoteAsync(s_peer, new MessageKey { Id = pollId }, ["Sushi"], voteId, ct));
		// The id is the send's, whatever kind: a text with the poll's id is answered from the poll's record.
		Assert.Equal(poll, await client.SendTextAsync(s_peer, "hi", pollId, cancellationToken: ct));

		var next = await client.SendTextAsync(s_peer, "next", cancellationToken: ct);
		var sent = await NextAsync(Session, 0, 3, ct);
		Assert.Equal<MessageId>([pollId, voteId, next.Id], sent.Select(static s => MessageId.From(s.Message.Id)));
	}

	private static ImmutableArray<byte> Hash(string option) => [.. SHA256.HashData(Encoding.UTF8.GetBytes(option))];

	private static E2E.PollCreationMessage.Types.Option Option(string name) => new() { OptionName = name };

	/// <summary>s_lunch as WhatsApp sends it, with its secret.</summary>
	private static E2E.Message PollMessage(uint maxSelectable) =>
		new()
		{
			PollCreationMessage = new E2E.PollCreationMessage
			{
				Name = "Lunch?",
				Options = { Option("Pizza"), Option("Sushi"), Option("Tacos") },
				SelectableOptionsCount = maxSelectable,
			},
			MessageContextInfo = new E2E.MessageContextInfo { MessageSecret = ByteString.CopyFrom(RandomNumberGenerator.GetBytes(32)) },
		};

	/// <summary>A LID of the session's own, without a device: whatsmeow names the account by it in a LID chat.</summary>
	private static Jid Lid(int n) => Jid.Parse($"90000049{n}@lid");

	private static MessageReceived Received(string session, string id, Jid chat, Jid sender, MessageContent content, bool isFromMe = false) =>
		new()
		{
			SessionId = SessionId.From(session),
			Id = MessageId.From(id),
			Source = new MessageSource { Chat = chat, Sender = sender, SenderAlt = null, RecipientAlt = null, IsFromMe = isFromMe },
			PushName = null,
			Timestamp = Deliveries.At,
			Content = content,
		};

	private Task<IWhatsAppClient> LinkedAsync(string session, int n, CancellationToken ct) => SessionSetup.LinkedAsync(fixture, session, $"{Lid(n).User}:1@lid", ct);

	private Task<IWhatsAppClient> ConnectedAsync(string session, int n, CancellationToken ct) => SessionSetup.ConnectedAsync(fixture, session, $"{Lid(n).User}:1@lid", ct);

	/// <summary>Connects a linked session whose handlers are registered, so its events flow to them.</summary>
	private static async Task ConnectAsync(IWhatsAppClient client, CancellationToken ct)
	{
		await client.ConnectAsync(ct).ConfigureAwait(false);
		var watch = StateWatch.Start(client, ct);
		await using (watch.ConfigureAwait(false))
			await watch.ReachesAsync(new SessionState.Connected(), new SessionState.Connecting()).ConfigureAwait(false);
	}

	private async Task DeliverAsync(string session, string id, Jid chat, Jid sender, E2E.Message message, CancellationToken ct, bool isFromMe = false) =>
		_ = await fixture
			.Fake.DeliverMessageAsync(
				new DeliverMessageRequest
				{
					SessionId = session,
					Id = id,
					Chat = chat.ToString(),
					Sender = sender.ToString(),
					IsFromMe = isFromMe,
					MessageTime = Timestamp.FromDateTimeOffset(Deliveries.At),
					Message = message,
				},
				cancellationToken: ct
			)
			.ConfigureAwait(false);

	private async Task DeliverVoteAsync(string session, string id, Jid chat, Jid voter, MessageId poll, Jid pollSender, string[] options, CancellationToken ct) =>
		_ = await fixture
			.Fake.DeliverPollVoteAsync(
				new DeliverPollVoteRequest
				{
					SessionId = session,
					Id = id,
					Chat = chat.ToString(),
					Sender = voter.ToString(),
					PollId = poll.Value,
					PollSender = pollSender.ToString(),
					Options = { options },
					MessageTime = Timestamp.FromDateTimeOffset(Deliveries.At),
				},
				cancellationToken: ct
			)
			.ConfigureAwait(false);

	/// <summary>The session's next <paramref name="count"/> message nodes after the entry <paramref name="after"/>, past the receipts of what was delivered.</summary>
	private async Task<IReadOnlyList<Sent>> NextAsync(string session, ulong after, int count, CancellationToken ct)
	{
		var entries = new List<Sent>(count);
		while (entries.Count < count)
		{
			var next = (await fixture.Fake.WaitForSentAsync(new WaitForSentRequest { SessionId = session, After = after }, cancellationToken: ct).ConfigureAwait(false)).Sent;
			after = next.Index;
			if (next.Message is not null)
				entries.Add(next);
		}

		return entries;
	}
}
