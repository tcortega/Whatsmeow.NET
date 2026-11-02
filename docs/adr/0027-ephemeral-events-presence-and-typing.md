# 0027. Ephemeral events: presence and typing outside the outbox

- Status: Accepted
- Date: 2026-11-01
- Evidence: issue #32, its plan review, ARCHITECTURE §14 "Measured for Presence (#32)", and the tests named below
- Supersedes: "a bounded in-memory ring buffer with `seq = 0`" in ARCHITECTURE §7.3; the wording of invariant 1

## Context

Online status and typing indicators are high volume and only matter for a moment.
[ADR 0022](0022-outbox-event-stream-and-dispatcher.md) left them out of the event stream for this issue, and ARCHITECTURE §7.3 planned them in a bounded in-memory ring with `seq = 0`.
Reading the pin and the merged code showed facts the design had to follow:

- whatsmeow dispatches `events.Presence` and `events.ChatPresence` on the node queue, and answers neither stanza, whatever the handlers return (presence.go:18-58; client.go:322-323, 896-903) [observed]. They are not gated, and a handler that waits on them delays every message.
- In-band with `seq = 0`, the .NET reader would set its `highest` to 0 and, on a reopen, ask for `after_seq = 0`, so held events would come again; the dispatcher's watermark would move back to 0 when the event finished and stay behind it while it ran; and ephemeral events would take the durable path's 64 pending events and 16 lanes, and the lane of their chat [inferred: from reading `SessionEvents.ReadAsync` and `Dispatcher` at dfd0bfa; the mutation that sends them in-band to the durable dispatcher fails `Burst_PresenceHandlersAreStuck_MessagesStillArriveAndWhatIsNotHeldIsCounted` and `Dispose_WhileAPresenceHandlerIsStuck_TheHandledMessagesAreStillAcked`].
- `SendPresence` refuses without the account's push name, which only the app state sync sets (presence.go:67-68, appstate.go:364-374), and sending available turns on whatsmeow's active delivery receipts (presence.go:70-75, receipt.go:287) [observed].

## Decision

- **A stream of their own.** `EventService.StreamEphemeralEvents` sends presence, chat presence and `QueueOverflowEvent`. The response has no seq, nothing on it is recorded or acked, and there is no consumer and no replacement: every open stream of a session gets every event. `StreamEvents` and its reader are unchanged.
- **The ring.** Each session has a ring of its newest 256 ephemeral events in memory (`internal/events/ring.go`), fixed until a measurement shows it needs tuning. The whatsmeow handler pushes to it under a mutex and returns true; it never waits for a stream. The events are numbered as they are pushed, and a stream reads them by number:
  - A stream starts after the newest event any stream of the session got, counted when the event is handed to the send, not after it. One opened while none is open so first gets what no stream got, including the gap between a connect and its stream; one opened while another is open starts where the furthest open one is.
  - A stream that finds events it would get already dropped, because more arrived than the ring keeps while it fell behind or none was open, first gets `QueueOverflowEvent` with how many.
  - A ring remembers its session's deletion (`outbox.Deleted`), so a session created again with the same id never gets the old one's events. The handler of a session's client is bound to the ring of that session when it is made, so a client still running after a delete pushes only into its own dead ring.
  - A restart loses the ring: ephemeral events from before it are gone.
- **The .NET side.** A second reader and a second `Dispatcher`, with its own 64 pending events and 16 lanes, and no ack (`ackAsync` null: no watermark and nothing kept for it behind an unfinished event, no timer, no stuck-watermark warning, no last ack). The ephemeral events never reach the durable dispatcher, so they cannot move, hold back or delay its watermark. Lanes are per user for presence and per chat for chat presence; the order relative to durable events is not kept. Handlers are the same: `On<T>`, `WaitForAsync`, `OnHandlerError`.
- **Public API.** `SendPresenceAsync(PresenceState)`, `SubscribePresenceAsync(Jid)` and `SendChatPresenceAsync(Jid, ChatPresenceState)` flat on `IWhatsAppClient` (ADR 0025); records `PresenceChanged`, `ChatPresenceChanged` and `EphemeralEventsDropped` in `Whatsmeow.Events`; the enums `PresenceState` and `ChatPresenceState` in `Whatsmeow.Presence`. A chat state this client does not know is skipped. The docs on `On<T>` and the records say the loss semantics: best effort, never repeated, dropped on overflow or a restart.
- **The Presence slice** (`internal/presence/`) takes the client of an online session from `session.(*Service).Online` (#33, [ADR 0028](0028-contacts-blocklist-and-lid-resolution.md)) and type-asserts it to its own small interface, INTERNAL when it does not fit, as pair.go does; `*whatsmeow.Client` and the fake's client are checked against it at compile time.
- **`NO_PUSH_NAME`**, a new `ErrorReason` (FAILED_PRECONDITION), for `SendPresence` before the push name arrived; it maps to `WhatsmeowException` with `WhatsmeowErrorCode.NoPushName`, without a subtype (§8.7).
- **No presence on connect.** mautrix-whatsapp sends unavailable after connecting and after a push-name change (pkg/connector/handlewhatsapp.go:154-156, 186-189) [observed]; this library leaves it to the application, which the docs on `SendPresenceAsync` tell to call it once after connecting.
- **Invariant 1** says the handler writes only to the outbox, or, for an ephemeral event, to the session's ring in memory. Its meaning is unchanged: the handler never blocks on .NET, the network or media.

## Consequences

- An application connected after a burst gets at most the newest 256, after `EphemeralEventsDropped`; the events can be seconds or minutes old, and carry no receive time.
- A handler that never returns on an ephemeral event holds its lane, then the 64 pending, after which the reader stops and the sidecar drops and counts; the durable events go on. What this process holds is bounded by gRPC flow control: with every presence handler stuck, a burst of 10,000 and one of 30,000 both delivered 3,046 presences after the release and counted the rest as dropped, exactly, so about 2,800 are held in the dispatcher, its channel and the transport, and the newest 256 come from the ring [observed: `Burst_PresenceHandlersAreStuck_MessagesStillArriveAndWhatIsNotHeldIsCounted` on osx-arm64, 2026-11-02; the split between the dispatcher and the transport is inferred]. A burst of 2,000 is held whole, and nothing is dropped [observed: same test].
- The fake WhatsApp answers messages with active receipts after `SendPresence(available)`, as whatsmeow does; the tests that send no presence see `inactive` as before.
- Whether WhatsApp sends presence to an account that set itself unavailable, and to a subscribe without a privacy token, is [unknown]; the Phase 2 exit live run (#36) records it.
- Tests: Go `internal/events/ephemeral_test.go` (the mapping table, never recorded, the backlog and its drop count, a second stream, a stream that falls behind while the handler never waits, deletion, a late event of a deleted session's client), `internal/presence/presence_test.go`, `internal/fakewhatsapp/presence_test.go`; .NET `Presence/PresenceTests.cs`, `PresenceProcessTests.cs` (the watermark past a stuck presence handler) and `PresenceCrashTests.cs` (a restart); `DispatcherTests.NoAck_BehindAHandlerThatNeverReturns_KeepsNothingOfTheEventsThatFinished`.
