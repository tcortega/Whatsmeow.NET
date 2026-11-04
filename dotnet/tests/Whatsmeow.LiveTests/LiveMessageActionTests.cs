using Whatsmeow.Events;
using Whatsmeow.Messaging;
using E2E = Whatsmeow.Proto.WAWebProtobufsE2E;

namespace Whatsmeow.LiveTests;

/// <summary>
/// Message actions (#30) between the live account and the peer, through real WhatsApp: each action the peer gets is
/// compared whole with the fake-mode tests' records (<see cref="LiveChecks.Received"/>), and the questions #30 left
/// [unknown] go to the log for ARCHITECTURE §14.
/// </summary>
[Collection(nameof(Live))]
public sealed class LiveMessageActionTests(LiveSidecar live, LivePeer peer)
{
	/// <summary>Past whatsmeow's 20 minute <c>EditWindow</c>, which nothing at the pin reads.</summary>
	private static readonly TimeSpan s_lateEdit = TimeSpan.FromMinutes(21);

	/// <summary>The late edit's timeout: the wait, and the work around it.</summary>
	private const int LateEditTimeout = 1_800_000;

	/// <summary>A reaction to the live account's own message: the peer gets it naming the message by its id and its sender.</summary>
	[LiveFact(NeedsPeer = true)]
	public async Task Live_ReactToItsOwnMessage_ThePeerGetsTheReactionNamingIt()
	{
		var ct = TestContext.Current.CancellationToken;
		var (client, other, sender, to) = await AccountsAsync(ct);
		var target = await TextToPeerAsync(client, other, to, "wmn react", ct);

		var id = MessageId.New();
		var arrived = LiveChecks.ArrivalAsync(other, id, ct);
		_ = await client.SendReactionAsync(to, new MessageKey { Id = target }, "👍", id, ct);
		var got = await LiveChecks.WithinAsync(arrived, "the reaction");

		var content = new ReactionContent { Target = new MessageKey { Id = target, Sender = got.Source.Sender with { Device = 0 } }, Emoji = "👍" };
		LiveChecks.Equal(LiveChecks.Received(other, got, sender, id, content), got);
	}

	/// <summary>An edit of the live account's own message: the peer gets the new text as an edit of it.</summary>
	[LiveFact(NeedsPeer = true)]
	public async Task Live_EditItsOwnMessage_ThePeerGetsTheEdit()
	{
		var ct = TestContext.Current.CancellationToken;
		var (client, other, sender, to) = await AccountsAsync(ct);
		var target = await TextToPeerAsync(client, other, to, "wmn edit", ct);

		var id = MessageId.New();
		var arrived = LiveChecks.ArrivalAsync(other, id, ct);
		_ = await client.EditMessageAsync(to, target, "wmn edited", id, ct);
		var got = await LiveChecks.WithinAsync(arrived, "the edit");

		var content = new EditContent { Target = new MessageKey { Id = target, Sender = got.Source.Sender with { Device = 0 } }, Content = new TextContent { Text = "wmn edited" } };
		LiveChecks.Equal(LiveChecks.Received(other, got, sender, id, content), got);
	}

	/// <summary>A revoke of the live account's own message: the peer gets it naming the message.</summary>
	[LiveFact(NeedsPeer = true)]
	public async Task Live_RevokeItsOwnMessage_ThePeerGetsTheRevoke()
	{
		var ct = TestContext.Current.CancellationToken;
		var (client, other, sender, to) = await AccountsAsync(ct);
		var target = await TextToPeerAsync(client, other, to, "wmn revoke", ct);

		var id = MessageId.New();
		var arrived = LiveChecks.ArrivalAsync(other, id, ct);
		_ = await client.RevokeMessageAsync(to, new MessageKey { Id = target }, id, ct);
		var got = await LiveChecks.WithinAsync(arrived, "the revoke");

		LiveChecks.Equal(LiveChecks.Received(other, got, sender, id, new RevokeContent { Target = new MessageKey { Id = target, Sender = got.Source.Sender with { Device = 0 } } }), got);
	}

	/// <summary>
	/// A reply to the peer's message that mentions the peer: the peer gets the quote as of its own message (no sender),
	/// with the quoted text, and the mention as sent.
	/// </summary>
	[LiveFact(NeedsPeer = true)]
	public async Task Live_ReplyToThePeersMessageMentioningIt_ThePeerGetsTheQuoteAndTheMention()
	{
		var ct = TestContext.Current.CancellationToken;
		var (client, other, sender, to) = await AccountsAsync(ct);
		var question = await TextFromPeerAsync(client, other, "wmn the question", ct);

		var id = MessageId.New();
		var text = $"@{to.User} wmn the answer";
		var options = new SendOptions
		{
			ReplyTo = new MessageReply { Id = question.Id, Sender = question.Source.Sender with { Device = 0 }, QuotedText = "wmn the question" },
			Mentions = [to],
		};
		var arrived = LiveChecks.ArrivalAsync(other, id, ct);
		_ = await client.SendTextAsync(to, text, id, options, ct);
		var got = await LiveChecks.WithinAsync(arrived, "the reply");
		live.Write($"live: the reply quoted the peer's message as from a {LiveChecks.Form(question.Source.Sender)}, and mentioned it by its phone number");

		LiveChecks.Equal(
			LiveChecks.Received(other, got, sender, id, new TextContent { Text = text }) with
			{
				ReplyTo = new QuotedMessage { Id = question.Id, Sender = null, Content = new TextContent { Text = "wmn the question" } },
				Mentions = [to],
			},
			got
		);
	}

	/// <summary>
	/// #30: a key that names the phone-number chat of a chat WhatsApp may hold by LID. The live session gets the peer's
	/// message in the chat form WhatsApp chose, and reacts to it naming the chat and the peer by phone number; the peer
	/// gets the reaction to its own message. Then the live account sends to the peer's LID chat and revokes that message
	/// naming the phone-number chat. Each form goes to the log; whether the phones match the targets is the owner's.
	/// </summary>
	[LiveFact(NeedsPeer = true)]
	public async Task Live_AReactionAndARevokeNamingThePhoneNumberChat_ThePeerGetsEachOnItsTarget()
	{
		var ct = TestContext.Current.CancellationToken;
		var (client, other, sender, to) = await AccountsAsync(ct);
		var (_, lid) = await peer.AccountAsync(ct);
		var theirs = await TextFromPeerAsync(client, other, "wmn react by phone number", ct);
		live.Write($"live: the peer's message came in a {LiveChecks.Form(theirs.Source.Chat)} chat, from a {LiveChecks.Form(theirs.Source.Sender)}");

		var reactionId = MessageId.New();
		var reacted = LiveChecks.ArrivalAsync(other, reactionId, ct);
		_ = await client.SendReactionAsync(to, new MessageKey { Id = theirs.Id, Sender = to }, "👍", reactionId, ct);
		var reaction = await LiveChecks.WithinAsync(reacted, "the reaction naming the phone-number chat");
		live.Write($"live: the peer got the reaction keyed by phone number in a {LiveChecks.Form(reaction.Source.Chat)} chat");
		LiveChecks.Equal(LiveChecks.Received(other, reaction, sender, reactionId, new ReactionContent { Target = new MessageKey { Id = theirs.Id }, Emoji = "👍" }), reaction);

		Assert.True(lid is { IsLid: true }, "the peer session has no LID, so there is no LID chat to send to");
		var target = await TextToPeerAsync(client, other, lid.Value, "wmn revoke by phone number", ct);
		var revokeId = MessageId.New();
		var revoked = LiveChecks.ArrivalAsync(other, revokeId, ct);
		_ = await client.RevokeMessageAsync(to, new MessageKey { Id = target }, revokeId, ct);
		var revoke = await LiveChecks.WithinAsync(revoked, "the revoke naming the phone-number chat");
		live.Write($"live: the peer got the revoke keyed by phone number, of a message sent to its LID chat, in a {LiveChecks.Form(revoke.Source.Chat)} chat");
		LiveChecks.Equal(
			LiveChecks.Received(other, revoke, sender, revokeId, new RevokeContent { Target = new MessageKey { Id = target, Sender = revoke.Source.Sender with { Device = 0 } } }),
			revoke
		);
	}

	/// <summary>
	/// #30: a reply whose quote names no participant, as the API never sends it (a reply without a sender names the
	/// account), so it goes as a raw message. The peer reads a quote that names no user as no reply, as the fake-mode
	/// reply tests do; how the peer's phone shows it is the owner's (<see cref="Live_Replies_TheOwnerSaysHowThePeersPhoneShowsEachQuote"/>).
	/// </summary>
	[LiveFact(NeedsPeer = true)]
	public async Task Live_AReplyWithNoParticipant_ThePeerGetsItWithNoQuote()
	{
		var ct = TestContext.Current.CancellationToken;
		var (client, other, sender, to) = await AccountsAsync(ct);
		var question = await TextFromPeerAsync(client, other, "wmn quoted without participant", ct);

		var id = MessageId.New();
		var arrived = LiveChecks.ArrivalAsync(other, id, ct);
		_ = await SendReplyWithoutParticipantAsync(client, to, question.Id, "wmn quoted without participant", "wmn reply without participant", id, ct);
		var got = await LiveChecks.WithinAsync(arrived, "the reply with no participant");

		LiveChecks.Equal(LiveChecks.Received(other, got, sender, id, new TextContent { Text = "wmn reply without participant" }), got);
	}

	/// <summary>
	/// #30, the owner at the peer's phone: one reply naming the participant, as the API sends it, and one naming none.
	/// The owner says whether the peer's phone shows the quote on each.
	/// </summary>
	[LiveFact(NeedsPeer = true, NeedsOwner = true)]
	public async Task Live_Replies_TheOwnerSaysHowThePeersPhoneShowsEachQuote()
	{
		var ct = TestContext.Current.CancellationToken;
		var (client, other, _, to) = await AccountsAsync(ct);
		var question = await TextFromPeerAsync(client, other, "wmn quote me", ct);
		var reply = new MessageReply { Id = question.Id, Sender = question.Source.Sender with { Device = 0 }, QuotedText = "wmn quote me" };

		_ = await client.SendTextAsync(to, "wmn reply 1 of 2, naming the participant", options: new SendOptions { ReplyTo = reply }, cancellationToken: ct);
		_ = await SendReplyWithoutParticipantAsync(client, to, question.Id, "wmn quote me", "wmn reply 2 of 2, naming no participant", MessageId.New(), ct);

		_ = await live.AskOwnerAsync(
			"On the peer's phone, in the chat with the live account: replies 1 and 2 of 2 to \"wmn quote me\". Does each show the quote, and does tapping it go to that message?",
			ct
		);
	}

	/// <summary>
	/// #30: an edit 21 minutes after the message, past whatsmeow's 20 minute window. Whether WhatsApp refuses the send,
	/// and whether the peer gets it, goes to the log; an edit the peer gets is compared as any edit is.
	/// </summary>
	[LiveFact(NeedsPeer = true, Timeout = LateEditTimeout)]
	public async Task Live_AnEditAfterTheTwentyMinuteWindow_WhatWhatsAppDoesWithItIsRecorded()
	{
		var ct = TestContext.Current.CancellationToken;
		var (client, other, sender, to) = await AccountsAsync(ct);
		var target = await TextToPeerAsync(client, other, to, "wmn late edit", ct);
		live.Write($"live: waiting {s_lateEdit.TotalMinutes:F0} min to edit the message");
		await Task.Delay(s_lateEdit, LiveClock.Time, ct);

		var id = MessageId.New();
		var arrived = LiveChecks.ArrivalAsync(other, id, ct);
		try
		{
			_ = await client.EditMessageAsync(to, target, "wmn edited late", id, ct);
		}
		catch (WhatsmeowException e)
		{
			live.Write($"live: WhatsApp refused the edit {s_lateEdit.TotalMinutes:F0} min after the message: {e.ErrorCode}{(e is WhatsAppErrorException w ? $" {w.Code}" : "")}");
			return;
		}

		var got = await LiveChecks.OrNullAsync(arrived);
		live.Write(
			got is null
				? $"live: WhatsApp took the edit {s_lateEdit.TotalMinutes:F0} min after the message, and the peer did not get it within {LiveChecks.Watch.TotalSeconds:F0} s"
				: $"live: WhatsApp took the edit {s_lateEdit.TotalMinutes:F0} min after the message, and the peer got it"
		);
		if (got is null)
			return;
		var content = new EditContent { Target = new MessageKey { Id = target, Sender = got.Source.Sender with { Device = 0 } }, Content = new TextContent { Text = "wmn edited late" } };
		LiveChecks.Equal(LiveChecks.Received(other, got, sender, id, content), got);
	}

	/// <summary>
	/// The chat with the peer's disappearing timer set to 7 days, then off: the peer gets each as the chat's new timer,
	/// as in the fake-mode timer test. Each send and arrival goes to the log; the timer is turned off after, whatever
	/// happened, even when a set was cancelled after WhatsApp took it.
	/// </summary>
	[LiveFact(NeedsPeer = true)]
	public async Task Live_TheChatsTimerSetToSevenDaysThenOff_ThePeerGetsEachTimer()
	{
		var ct = TestContext.Current.CancellationToken;
		var (client, other, sender, to) = await AccountsAsync(ct);
		var seen = new List<(MessageId Id, TimeSpan Timer, MessageReceived? Got)>();
		await LiveRestore.AfterAsync(
			live,
			async () =>
			{
				foreach (var timer in new[] { TimeSpan.FromDays(7), TimeSpan.Zero })
				{
					var id = MessageId.New();
					var arrived = LiveChecks.OrNullAsync(LiveChecks.ArrivalAsync(other, id, ct));
					await client.SetDisappearingTimerAsync(to, timer, id, ct).ConfigureAwait(false);
					var got = await arrived.ConfigureAwait(false);
					live.Write(
						$"live: the chat's timer set to {timer.TotalDays:F0} days; the peer {(got is null ? $"got nothing within {LiveChecks.Watch.TotalSeconds:F0} s" : $"got it as {got.Content.GetType().Name}")}"
					);
					seen.Add((id, timer, got));
				}
			},
			("turn the chat's timer off", t => client.SetDisappearingTimerAsync(to, TimeSpan.Zero, cancellationToken: t))
		);

		foreach (var (id, timer, got) in seen)
		{
			Assert.True(got is not null, $"the peer did not get the {timer.TotalDays:F0} day timer");
			LiveChecks.Equal(LiveChecks.Received(other, got, sender, id, new DisappearingTimerContent { Timer = timer }), got);
		}
	}

	/// <summary>Both sessions connected, the live account, and the peer's chat by phone number.</summary>
	private async Task<(IWhatsAppClient Client, IWhatsAppClient Other, (Jid Chat, Jid? Lid) Sender, Jid To)> AccountsAsync(CancellationToken ct)
	{
		var client = await live.LiveAsync(ct).ConfigureAwait(false);
		var other = await peer.ConnectedAsync(live, ct).ConfigureAwait(false);
		var sender = await live.AccountAsync(ct).ConfigureAwait(false);
		var (to, _) = await peer.AccountAsync(ct).ConfigureAwait(false);
		return (client, other, sender, to);
	}

	/// <summary>A text from the live session to <paramref name="chat"/>, once the peer got it: the message to act on.</summary>
	private static async Task<MessageId> TextToPeerAsync(IWhatsAppClient client, IWhatsAppClient other, Jid chat, string text, CancellationToken ct)
	{
		var id = MessageId.New();
		var arrived = LiveChecks.ArrivalAsync(other, id, ct);
		_ = await client.SendTextAsync(chat, $"{text} {id}", id, cancellationToken: ct).ConfigureAwait(false);
		_ = await LiveChecks.WithinAsync(arrived, $"the text \"{text}\"").ConfigureAwait(false);
		return id;
	}

	/// <summary>A text from the peer to the live account, as the live session got it.</summary>
	private async Task<MessageReceived> TextFromPeerAsync(IWhatsAppClient client, IWhatsAppClient other, string text, CancellationToken ct)
	{
		var (account, _) = await live.AccountAsync(ct).ConfigureAwait(false);
		var id = MessageId.New();
		var arrived = LiveChecks.ArrivalAsync(client, id, ct);
		_ = await other.SendTextAsync(account, text, id, cancellationToken: ct).ConfigureAwait(false);
		return await LiveChecks.WithinAsync(arrived, $"the peer's text \"{text}\"").ConfigureAwait(false);
	}

	/// <summary>A reply whose quote has the message id and its text but no participant, which only a raw message can send.</summary>
	private static Task<SendResult> SendReplyWithoutParticipantAsync(IWhatsAppClient client, Jid chat, MessageId quoted, string quotedText, string text, MessageId id, CancellationToken ct)
	{
		var message = new E2E.Message
		{
			ExtendedTextMessage = new E2E.ExtendedTextMessage
			{
				Text = text,
				ContextInfo = new E2E.ContextInfo
				{
					StanzaID = quoted.Value,
					QuotedMessage = new E2E.Message { Conversation = quotedText },
					QuotedType = E2E.ContextInfo.Types.QuotedType.Explicit,
				},
			},
		};
#pragma warning disable WMN1001 // Justification: a quote with no participant is what is under test, and the typed sends always name one
		return client.SendRawAsync(chat, message, id, ct);
#pragma warning restore WMN1001
	}
}
