# Messaging

Sending messages, and what comes back.
Every send takes the chat as a `Jid`: a user is `<phone number>@s.whatsapp.net` or `<lid>@lid`, a group `<id>@g.us`.

```csharp
var result = await client.SendTextAsync(Jid.Parse("5511999999999@s.whatsapp.net"), "hello", cancellationToken: cancellationToken);
```

`SendResult` has the message's id, WhatsApp's timestamp, and `Chat`, where it went: a message to a phone number goes to that user's LID, which `Chat` reports (§7.5).
A phone number that is not on WhatsApp fails with `RecipientNotFoundException`, and nothing is sent.

## The id makes a send safe to repeat

Each send takes an optional `MessageId`; without one the library makes a new one with `MessageId.New()`.
The sidecar records each id before it hands the message to whatsmeow, so sending the same id again never sends the message twice: it returns the first send's result, or waits for it (§7.5).
An id has to be in whatsmeow's own format, `3EB0` and 18 uppercase hex digits; whether WhatsApp accepts any other is [unknown].

When the sidecar cannot tell whether WhatsApp got a message, the send fails with `OutcomeUnknownException`.
It is never retried for you, and a repeat of its id fails the same way, so you decide whether a duplicate or a gap is worse.
A send that failed with any other error sent nothing, and the same id can go again, except the one case `SendTextAsync`'s docs describe: WhatsApp answered the message itself with an error.

To answer a message without risking a double answer after a redelivery, derive the reply's id from the message it answers, as [`EchoHandler`](../../dotnet/samples/EchoBot/EchoHandler.cs) does.

## Pacing

Each session's sends leave one at a time, in order, at a burst of 5 and then one a second by default, with a little random delay so they do not leave on a fixed beat (§7.5).
`WhatsmeowSidecarOptions.SendPacing` changes `Burst` and `Interval` for every session.
Which rates WhatsApp tolerates before it rate limits or bans an account is [unknown]; the defaults are a conservative guess.

A send never waits for a connection: on a session that is not connected it fails at once with `NotConnectedException`, and so do the sends still queued when a session drops.
WhatsApp's rate limit is retried after 1 s, 2 s and 4 s while your token allows, then fails with `RateLimitedException`.
It has no retry delay: whatsmeow reads none [observed: §8.7], and whether WhatsApp sends one is [unknown].
A send has no deadline of its own: bound it with your cancellation token.

## Replies, mentions, reactions, edits and revokes

```csharp
var options = new SendOptions
{
	ReplyTo = new MessageReply { Id = received.Id, Sender = received.Source.Sender, QuotedText = text },
	Mentions = [user],
};
```

Text and media sends take `SendOptions`: the message they reply to and the users they mention.
For a mention, put `@` and the user's number in the text too, as WhatsApp's apps do.

`SendReactionAsync`, `EditMessageAsync` and `RevokeMessageAsync` name their target by id, and by a `MessageKey` with its sender where WhatsApp needs one; a key with no sender is the account's own message.
An empty reaction removes yours.
Received ones arrive as `MessageReceived` with `ReactionContent`, `EditContent` or `RevokeContent`.
How WhatsApp answers a late edit or a revoke of someone else's message as a non-admin is [unknown] (§14 Message actions).

## Polls

`SendPollAsync` sends a `Poll` with its question, options and `MaxSelectable` (from 1 to the number of options), and `SendPollVoteAsync` votes on a poll's `MessageKey` by option names; no names clears the vote.
Votes arrive decrypted as `PollVoteContent`, which names options by the SHA-256 of their names: `PollVoteContent.HashOption` and `Selects` check them for you.
Voting needs the poll's secret, which the sidecar keeps for every poll it sent, received or got from history; without it the vote fails with `PollSecretNotFound` and sends nothing.

## Disappearing messages

`SetDisappearingTimerAsync(chat, timer)` sets a chat's timer to 24 hours, 7 days or 90 days, the values WhatsApp's phones offer, or turns it off with `TimeSpan.Zero`.
The library refuses any other value: whether WhatsApp takes one is [unknown] (§14).
A direct chat's timer goes out as a message, paced like one; a group's is a group setting.
Your later messages do not carry the timer: the library sets none on a send.

## Locations and contact cards

A received location arrives as `LocationContent`: its latitude and longitude, and the place's name and address when the message gives them.
A received contact card arrives as `ContactCardContent`, with its display name and vCard.
A live location and a message with several contact cards still arrive as `UnknownContent`; read them from `Raw` (§8.3).
The library has no send for either; `SendRawAsync` sends any message (below).

## Receipts and marking as read

`ReceiptReceived` tells you a message was delivered, read or played, with the message ids and the `ReceiptType`.
`MarkReadAsync(chat, ids, sender)` sends a read receipt; in a group, name the sender.
Whether the other side's read receipt arrives depends on settings nobody has pinned down: in the live runs none came within 60 s of the peer marking a message read [observed: §14 Phase 1 exit], and why is [unknown].

## Raw messages

`MessageReceived.Raw` and `SendRawAsync` expose WhatsApp's own protobuf messages from `Whatsmeow.Protocol.WhatsApp`.
They are `[Experimental("WMN1001")]`: those types change with whatsmeow, every week if need be, so code that uses them can break on a patch release.
