# 0041. Polls and votes as whatsmeow-built sends, and received votes decrypted in the event handler

- Status: Accepted
- Date: 2026-11-02
- Evidence: issue #31, its plan review, ARCHITECTURE §14 "Measured for Polls (#31)", and the tests named below
- Uses: [ADR 0025](0025-messaging-and-receipts.md) (the send queue and records), [ADR 0036](0036-testing-package-and-fake-client.md) (the fake client), [ADR 0038](0038-message-actions-replies-mentions-and-unwrap-flags.md) (targets, the account's own message, one typed-send method)
- Amends: ARCHITECTURE §13 invariant 1 (the vote's decryption reads whatsmeow's store)

## Context

Issue #31 sends polls and votes and delivers received polls and decrypted votes.
Reading whatsmeow at the pin and mautrix-whatsapp `5dd621f` for it showed facts the design had to follow [observed: ARCHITECTURE §14 "Measured for Polls (#31)"]:

- whatsmeow builds a poll (`BuildPollCreation`) as the V1 `PollCreationMessage` with a new 32-byte message secret, and a vote (`BuildPollVote`) by hashing each option name with SHA-256 and encrypting the selection with a key derived from the poll's secret, the poll's id and sender, and the voter (msgsecret.go:107-128, 369-445).
- whatsmeow stores message secrets itself: an outgoing message's in `SendMessage`, by the chat it went to and the account as it named itself there (send.go:403-410), a received one's before the message is dispatched (message.go:939-940, 964-985), and those a history sync blob carries when the blob is downloaded (message.go:806-812, 988-1050). Its store never deletes one but with the device.
- whatsmeow never decrypts a vote on its own: a vote is an ordinary gated `events.Message` with a `PollUpdateMessage`, which `DecryptPollVote` decrypts with the poll's secret from the device's store, reading nothing else (msgsecret.go:158-202, 305-320).
- Without the poll's secret both directions fail with `ErrOriginalMessageSecretNotFound` (msgsecret.go:170-171, 214-215). A poll created before this device was linked has a secret here only once a history blob that carried it was processed.
- `EncryptPollVote` names the voter by phone number when the poll's sender is a phone number, and by LID otherwise (msgsecret.go:429-432), so the poll's sender must be the form its message named.
- mautrix sends polls in V1, refuses an empty question and repeated options, reads a received limit of 0 as every option, and maps V1, V2, V3, V5 and V6, which are one type; V4 is a `FutureProofMessage` (matrixpoll.go:64-104, wa-poll.go:39-95, from-whatsapp.go:174-187).
- A history blob carries a poll's votes already decrypted, in the poll's `WebMessageInfo.pollUpdates` (WAWebProtobufsWeb.proto:328, 598-605).

## Decision

- **Two RPCs, sends like the others.** `SendPoll` and `SendPollVote` go through the same `send`: one per-session queue, the same pacing, flight, `sent_message` record and classification (ADR 0025). The `client_message_id` is the poll's or the vote's own id, so a repeat returns its first result whatever kind either send was.
  The message is whatsmeow's own: the slice's small interface gains `BuildPollCreation` and `BuildPollVote`, called in the send's turn once the session is online, so a whatsmeow change to them stays inside whatsmeow.
  A poll takes a reply and mentions as text does, on the poll's own context, as mautrix sends them.
- **Validation.** A poll needs a question, at least two options, none empty or repeated, and a maximum from 1 to the number of options; a vote's options are none empty or repeated, and no options clears the vote. Each is `INVALID_ARGUMENT`, and nothing is sent. The sidecar keeps no polls (§1), so a vote's options are never checked against its poll.
  A poll goes out with the caller's maximum as given: whatsmeow's builder keeps it, while mautrix sends 0 for every option; whether WhatsApp shows the two differently is [unknown] (#36).
- **The account's own poll.** A vote's poll is a target (ADR 0038): no sender, or the account's own JID, is the account's poll, which the sidecar names as whatsmeow named the account when it sent there (`account`, ADR 0038), so a group's addressing mode costs one group info request in the vote's turn, never on a repeat.
- **A poll without its secret here.** The vote fails before its record, with the new reason `POLL_SECRET_NOT_FOUND`, `FAILED_PRECONDITION`, on the base `WhatsmeowException` with its `ErrorCode`, and nothing is sent; a later repeat, once the secret is here, sends. A new reason, rather than `INVALID_ARGUMENT`, because the caller did nothing wrong and can say so: the account's phone can still vote.
- **Votes are decrypted in the event handler.** The Events slice's handler decrypts a vote with whatsmeow's own `DecryptPollVote`, on a never-connected whatsmeow client of the session's device, which runs nothing but the store read and AES-GCM, and `NewClient` starts nothing (client.go:267-325). That is whatsmeow's own decryption whichever client the session runs, the fake WhatsApp's too.
  Invariant 1 now lists that read: one SQLite read of whatsmeow's store and a few bytes of AES-GCM, as the media retry's decryption (ADR 0026), never the network.
  `MessageEvent` gains `poll_vote` (9), a `PollVote { selected_option_hashes, undecryptable }`, and a vote's target (7) is the poll its `PollCreationMessageKey` names, read as an action's key is.
  A vote that does not decrypt, for want of the secret or a failed check, is recorded with `undecryptable` and answered, never lost; whatsmeow's error text names JIDs (msgsecret.go:191), so it is dropped.
  A vote is a gated message, so it is at least once: a redispatch has the same natural key and keeps its seq, and the secret is never deleted, so it decrypts the same.
- **Received content.** `PollContent { Poll }` maps V1, V2, V3, V5 and V6 of type `POLL`, a limit of 0 read as every option; a quiz and V4 are `UnknownContent`, readable through `Raw`.
  `PollVoteContent { Poll, SelectedOptionHashes, IsUndecryptable }`, whose voter is the event's source, with `PollVoteContent.HashOption(name)`, the SHA-256 of the name's UTF-8, and `Selects(name)`, so the application, which holds the poll's options, tells which were selected.
  A vote whose poll key names no message is unknown content, as other malformed actions are. A history message's vote is not decrypted (#96), so it reads as undecryptable.
- **.NET.** `SendPollAsync(Jid chat, Poll poll, MessageId? id = null, SendOptions? options = null, CancellationToken cancellationToken = default)` and `SendPollVoteAsync(Jid chat, MessageKey poll, IReadOnlyList<string> selectedOptions, MessageId? id = null, CancellationToken cancellationToken = default)`, each in a `whatsmeow.send` span of type `poll` or `poll_vote` (ADR 0037).
  `Messaging.Poll { Question, Options, MaxSelectable }` is both what a send takes and what `PollContent` carries, and compares its options by content, as `PollVoteContent` compares its hashes.
- **The fake WhatsApp** stores a sent message's secret as `SendMessage` does and a delivered one's with whatsmeow's own code, builds polls and votes with whatsmeow's own builders on the session's store, and gains `DeliverPollVote`, which encrypts a vote as the voter's device would, with whatsmeow's `EncryptMsgSecret`, and names the poll by a key as the voter sees it. An undecryptable vote needs no control: `DeliverMessage` takes one as a raw message.
- **The consumer fake** records both sends, the vote's options as a copy.

## Consequences

- Votes from history sync are not delivered; #96 takes them from `pollUpdates`.
- A session linked without a LID names itself by an empty JID where whatsmeow keeps a sent poll's secret in a LID chat, as whatsmeow does, so a vote on its own poll there finds no secret; every device WhatsApp links has a LID [inferred: the live runs' sessions all had one].
- Whether WhatsApp shows a vote as replacing the voter's earlier one, and which address form a LID-addressed group needs for the poll's sender in a vote, are [unknown]; the Phase 2 exit live run (#36) records them.
- Tests: `internal/messaging/poll_test.go` (whatsmeow's own builds, the context of a reply and mentions, a vote that decrypts with the poll's secret for every poll form, a poll without its secret, an own group poll's info read once, validation, one queue and record with the other sends), `internal/events/poll_test.go` (decrypted, cleared, secretless and corrupted votes, all answered), `internal/events` (a poll's quote), `internal/fakewhatsapp/poll_test.go` (the builders, both secret stores, `DeliverPollVote`), `internal/server` (the reason), `Whatsmeow.IntegrationTests/Messaging/PollTests.cs`, `Telemetry/SpanTests.cs` (the two spans) and `Whatsmeow.Testing.Tests/PollCallTests.cs`.
