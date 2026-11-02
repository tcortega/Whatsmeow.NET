# 0047. Poll votes from history sync, as history messages after their poll

- Status: Accepted
- Date: 2026-11-02
- Evidence: issue #96, its plan review, and the tests named below
- Uses: [ADR 0035](0035-history-sync-that-never-delays-live-traffic.md) (batches, keys, counts), [ADR 0041](0041-polls-and-decrypted-poll-votes.md) (`PollVote`, `PollVoteContent`)
- Amends: ADR 0041, "A history message's vote is not decrypted (#96), so it reads as undecryptable", and its first consequence

## Context

Issue #96 delivers the votes a history sync blob carries on a poll.
#31 delivers live votes only, so an application that got a poll from history never learned how anyone voted before this device was linked.
Reading whatsmeow at the pin and mautrix-whatsapp `5dd621f` showed:

- A poll's `WebMessageInfo` carries its votes as `pollUpdates`, each a `PollUpdate` with the vote's own key (`pollUpdateMessageKey`), a plaintext `PollVoteMessage`, `senderTimestampMS`, `serverTimestampMS`, `unread` and `metadata` [observed: WAWebProtobufsWeb.proto:328, 598-605].
  That its `selectedOptions` are SHA-256 hashes, as a live vote's decrypted ones are (msgsecret.go:369-376), is [inferred].
- Neither whatsmeow nor mautrix reads `pollUpdates` [observed: no reference outside the generated `.pb.go`], so neither says how WhatsApp fills them.
- `ParseWebMessage` keeps the whole `WebMessageInfo` in `events.Message.SourceWebMsg`, and names a history message's sender by its key: the account when `fromMe`, by its LID in a LID chat and by its phone number otherwise; else the chat in a direct chat; else the participant [observed: client.go:1043-1069].
- mautrix reads the history reactions on a `WebMessageInfo`, which have the same key and `senderTimestampMS`, with that rule, dropping one with no sender, and at the sender's time [observed: pkg/connector/backfill.go:909-930].
- The outbox dedupes on the session and the event's natural key, and a history batch's key is the notification's id and the blob index of its first message, never a message id [observed: outbox.go:179, history.go:589], so the sidecar never drops a message for its id.
  The application does: `SessionId` and `MessageId` are its dedupe key (ARCHITECTURE §8.4, `HistoryMessage`).
  `parse` takes the blob's messages conversation by conversation, in order [observed: history.go `parse`], and where WhatsApp puts a vote message among them is [unknown].
  So a blob that also carried a vote as its own encrypted message, which history does not decrypt (ADR 0041), would give the application the same id twice, one undecryptable; one that dedupes would keep whichever came first, possibly the undecryptable one.

## Decision

- **Each vote is a history message after its poll.** The Events slice's `HistoryMessage` maps a history message to its `MessageEvent`, then, for each `pollUpdates` entry in the list's order, a vote's `MessageEvent` as a live vote's: the vote's id, its voter read as `ParseWebMessage` reads a sender, at its `senderTimestampMS`, the poll as its `target`, and the selection in `poll_vote`.
  Its `message` is unset: history carries no vote message, and one made up would be WhatsApp data that never existed.
  A vote whose key names no voter is dropped, as mautrix drops such a reaction.
  `unread`, `serverTimestampMS` and `metadata` have no counterpart on a live vote and are dropped.
  So `PollVoteContent` arrives in a `HistoryReceived` as it does live, with no new contract field, record or property; the client maps a vote without a message from its target and `poll_vote` alone.
  Attaching the votes to the poll (`PollContent.Votes`) was rejected: a new message, a new record, and a second path for what the application already handles.
- **A vote is the voter's as its key names it.** Whether a key names the voter by phone number or LID is [unknown]; the sidecar passes the form on, as a live vote's sender is, without an alternate, as no history message has one.
- **Several votes of one voter** are each delivered, in the list's order; keeping the latest is the application's, as live (ADR 0041). Whether the list keeps a replaced vote is [unknown].
- **A poll and its votes are one entry.** `parse` gives one entry per blob message, the message and its votes, and `batch` never splits one: a blob processed again from a message's index (ADR 0035) delivers its votes with it. A batch holds at most 256 messages unless one entry alone has more.
  `message_count` and `skipped_count` still count the blob's messages: a vote is not one.
- **An encrypted copy is not delivered.** A blob message that is a `PollUpdateMessage` whose id some poll of the blob carries decrypted in the same chat is an empty entry: not delivered, counted as delivered, since its vote was.
  One whose decrypted vote is not in the blob is delivered as before, undecryptable.
  A copy in one blob and its poll in another are not matched; whether WhatsApp sends either is [unknown].
- **The fake WhatsApp** takes a message's `poll_updates` in `DeliverHistorySync`, as a `WebMessageInfo` carries them.

## Consequences

- The [unknown]s above (PN or LID voter keys, a replaced vote kept or not, the hashes, and whether a blob also carries vote messages) are for the history live run (#42).
- Tests: `internal/events/history_test.go` (the votes of a member's group poll and of the account's poll in a LID chat, the account's vote by phone number and by LID, a vote without a voter, a poll without votes), `internal/history` (a poll's votes in its batch, the encrypted copy of one not delivered and another chat's kept, the counts; a poll and its votes never split, from its index too), `internal/fakewhatsapp/history_test.go` (`poll_updates` in the blob) and `Whatsmeow.IntegrationTests/Messaging/PollTests.cs` (the FullCycle: a history poll's votes as `PollVoteContent` after its `PollContent`, the encrypted copy not delivered, a poll without votes as today).
