# 0025. Messaging: sends recorded before the hand-off, a paced queue, receipts through the outbox

- Status: Accepted
- Date: 2026-11-01
- Evidence: issue #9, its orchestrator notes and plan review, ARCHITECTURE §14 "Measured for Messaging (#9)", and the tests named below
- Amends: [ADR 0021](0021-pairing.md) (the feature groups, decided here for every slice)
- Uses: [ADR 0028](0028-contacts-blocklist-and-lid-resolution.md) (`session.(*Service).Online` and `events.Mapper`, which #33 landed first)

## Context

Issue #9 sends text and raw messages, marks messages read, and delivers receipts, without ever sending a message twice by accident (ARCHITECTURE §7.5, §13 invariants 2 and 10).
`message.proto` (#3, #21) already fixed the contract: a send is idempotent per (session, client message id), recorded before it is handed to whatsmeow, and a repeat while the first is in flight waits for it.
Reading whatsmeow at the pin for it showed facts the design had to follow [observed: ARCHITECTURE §14 "Measured for Messaging (#9)"]:

- `SendMessage` writes the node and then waits up to 75 s for WhatsApp's answer; a cancel returns `ctx.Err()` (send.go:438-455).
  After the write, a dropped connection makes `retryFrame` wait 5 s for a reconnect and resend the same frame, then fail with `DisconnectedError` or an unwrapped `ErrNotConnected` (send.go:457-463, request.go:185-216).
- Before the write, every failure is a distinct error: `ErrNotLoggedIn` (send.go:207-211), `ErrRecipientNotOnWhatsApp` (send.go:351-357), an `IQError` of the LID lookup, the group members or the devices (send.go:310-351, sendDM 875-918, sendGroup 814-840), and a wrapped `ErrNotConnected` when there is no socket (client.go:981-983, send.go:840, 921).
- An answer with an `error` attribute is `ErrServerReturnedError` with the code only in its text (send.go:468-470); whether WhatsApp dropped such a message is [unknown].
- `IQError` carries no delay (errors.go:182-197), and `ErrIQRateOverLimit`'s `Is` compares the text too (errors.go:229-240).
- whatsmeow makes ids as `3EB0` plus 18 uppercase hex digits (send.go:41, 47-61); whether WhatsApp accepts others is [unknown].
- A non-retry receipt is acked only when no handler failed (receipt.go:21-43, 115-128); a retry receipt is acked in the retry handler regardless (receipt.go:34-38).

## Decision

- **Feature members, not groups.** Messaging is `IWhatsAppClient.SendTextAsync`, `SendRawAsync` and `MarkReadAsync`, as pairing is members of the client (ADR 0021).
  A `client.Messages` group would need a second public interface, against the one interface exception (ADR 0012), or a sealed class that `FakeWhatsAppClient` cannot substitute.
  This is the rule for every later feature slice (media, presence, contacts, groups, chat state): members on `IWhatsAppClient`, no groups.
- **The raw API** `SendRawAsync(Jid, Whatsmeow.Proto.WAWebProtobufsE2E.Message, ...)` is `[Experimental("WMN1001")]`: its type changes with every whatsmeow bump (§8.3).
  WMN0001-0003 are the build's own checks.
- **Through `Online` (ADR 0028).** A send and a read receipt take the session's client from `session.(*Service).Online`, and Messaging asserts the two whatsmeow methods it needs on it (`SendMessage`, `MarkRead`), as pair.go does; the channel `Online` returns, closed when the session stops being `Connected` or `CaughtUp` or is deleted, is what fails the queued sends.
- **Receipts through an `events.Mapper` (ADR 0028).** Messaging's `Receipt` maps `*events.Receipt` under the key `r\0chat\0sender\0type\0<unix seconds>\0id...` (the ids last), so WhatsApp's redelivery of an unacked receipt keeps its seq, and the ack waits for the commit (invariant 2).
  A receipt without ids, which whatsmeow never dispatches (receipt.go:85-88), is not recorded.
- **Ids are strict.** A send's `client_message_id` must be `^3EB0[0-9A-F]{18}$`, else `INVALID_ARGUMENT` (field `client_message_id`); `MessageId.New()` makes one.
  Loosening the rule later is additive; #10 measures whether WhatsApp takes other ids.
- **The send record.** `sent_message(session_id REFERENCES session ON DELETE CASCADE, client_message_id, chat, message_time, whatsapp_error)`, schema step 3.
  A send is inserted, pending, right before `SendMessage`; a success records its chat and timestamp; a failure before the write deletes the row, so a repeat sends; an error answer records its code.
  A pending row is an unknown outcome: a repeat, also after a crash, fails with `OUTCOME_UNKNOWN` and sends nothing (invariant 10).
  Rows stay until the session is deleted; pruning waits until it is measured to matter.
- **One send per id at a time.** An in-memory flight per (session, id): a repeat waits for it, returns its outcome, or sends itself when the first sent nothing.
- **The classification** (`messaging.classify`): only what provably fails before the write is "sent nothing": `ErrNotLoggedIn` (reported `LOGGED_OUT`: the session was online, so its device was deleted meanwhile), `ErrRecipientNotOnWhatsApp`, an `IQError` (429 is backed off, any other is `WHATSAPP_ERROR` with its code), and a wrapped `ErrNotConnected`.
  `ErrServerReturnedError` is `WHATSAPP_ERROR` with its code, recorded and repeated, never resent with that id.
  So `WHATSAPP_ERROR` has two repeat rules, told apart by when it came, not by its metadata, which is the code either way.
  An `IQError` came before the message was written: nothing was sent, the row is deleted, and a repeat of the id sends.
  `ErrServerReturnedError` answered the message itself: a repeat of the id fails with the same error and sends nothing, and since whether WhatsApp kept the message is [unknown], sending it again takes a new id.
  Either way a repeat of an id never sends a message twice, so repeating one is always safe.
  Everything else (a timeout, a context end, a `DisconnectedError`, an unwrapped `ErrNotConnected`, a failed write) is `OUTCOME_UNKNOWN`.
  A repeat that waited gets `OUTCOME_UNKNOWN` without the first caller's context error.
- **Repeats before fail-fast.** A repeat is answered from its record first, so a sent message's result comes back even while the session is offline; only then does a new send need `Online`, and it never waits for a connection.
- **The per-session queue.** FIFO, one send in flight, paced by a GCRA token bucket (burst 5, then one per second).
  No jitter: send pacing tuning is Phase 2, and ARCHITECTURE §7.5's "and jitter" waits for it.
  A queued send fails at once when its call ends, its session stops being online (`NOT_CONNECTED`, `LOGGED_OUT` or `TEMPORARILY_BANNED`, as the new state says, or `SESSION_NOT_FOUND` once it is deleted), or the slice closes; it sent nothing and leaves no record.
  An IQ 429 is tried again after 1 s, 2 s and 4 s while the wait and one second more for the try fit the call's deadline, then fails with `RATE_LIMITED`; the queue waits meanwhile, which keeps the order.
- **Up-front validation.** `SendMessage` checks `proto.CheckInitialized` itself: whatsmeow's `proto.Marshal` would refuse the same message (send.go:1261), and refusing it in the slice sends nothing for sure.
- **MarkRead** is not queued or deduped: a read receipt is not a message, and sending it again changes nothing.
  A group chat needs the sender.
- **.NET.** `SendResult { Id, Chat, Timestamp }` in `Whatsmeow.Messaging`; `ReceiptReceived { Source, MessageIds, Timestamp, Type }` and `ReceiptType` in `Whatsmeow.Events`, in the chat's lane; `RecipientNotFoundException` for `RECIPIENT_NOT_ON_WHATSAPP`.
- **The fake WhatsApp** gains `SendMessage` and `MarkRead` with whatsmeow's order of checks, a held send that waits 5 s for a reconnect it never gets once its connection ends, and the controls `DeliverReceipt`, `RateLimitNextSends`, `HoldNextSend` and `ReleaseSend`; a send to a phone number takes its LID as whatsmeow's does, from the store, else from `GetUserInfo` on the directory #33's `RegisterUser` fills, with whatsmeow's error texts; its log gains the message, read and ack entries.

## Consequences

- A send cancelled by its caller after the hand-off surfaces as `OperationCanceledException` to that caller, and as `OutcomeUnknownException` to every repeat.
- A send that was queued when its connection dropped can still race the state change and reach whatsmeow without a socket; it fails with `NOT_CONNECTED`, and nothing is recorded.
- A pending record never resolves on its own: an application that sees `OutcomeUnknownException` decides itself whether to send a new message with a new id.
- `IQError` timeouts before the write are `ErrIQTimedOut`, a plain error, so they read as unknown: conservative, and rare [inferred].
- Live, for #10: a send to the owner's own number ("Message yourself") checks the LID redirect, the timestamp and a repeat; one send with a non-`3EB0` id measures the id rule.
  Which receipts the own phone sends is [unknown].
- Tests: `internal/messaging` (record before the hand-off, repeats, in-flight wait, the classification table, backoff, order and pacing, a session leaving, validation, MarkRead, the receipt mapper), `internal/store/sent_message_test.go`, `internal/fakewhatsapp/send_test.go`, and `Whatsmeow.IntegrationTests/Messaging/` (the FullCycle, every exception, and the two crash tests).
