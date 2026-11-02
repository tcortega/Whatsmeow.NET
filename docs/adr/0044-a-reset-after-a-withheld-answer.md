# 0044. A reset after a withheld answer

- Status: Accepted
- Date: 2026-11-02
- Evidence: issue #91, its plan review, the #10 live runs in ARCHITECTURE §14, and the tests named below
- Amends: invariant 1 (the handler also wakes the session's redelivery), [ADR 0013](0013-whatsmeow-config-follows-mautrix-no-fork.md) (a reset mautrix does not make), [ADR 0022](0022-outbox-event-stream-and-dispatcher.md) (a withheld answer is no longer left to the next login)

## Context

A handler that fails withholds its stanza's answer: whatsmeow sends no receipt for a decrypted message whose handler failed (message.go:459-462, 489-495) [observed], so the event is not lost, and WhatsApp delivers it again.
Issue #91: when it does so is not bounded by anything the sidecar does.
The facts the design had to follow:

- WhatsApp delivered a withheld message again at each of 4 logins in three live runs, 0.6 to 1.4 s after the login began, from the account's other companion and from another account; it never did on 3 connections that stayed up 9 to 10 minutes [observed: ARCHITECTURE §14, the #10 runs of 2026-11-02].
  Whether it ever does on a connection that stays up longer than 10 minutes is [unknown]; that only a login brings it back is [inferred] from those runs.
- whatsmeow gives the handler no way to answer later: the event carries no node, and a receipt needs one (receipt.go:280-297) [observed]; sending it through `DangerousInternals` would mean keeping whatsmeow's nodes ourselves, a fork in all but name (ADR 0013).
- whatsmeow runs a client's handlers in order and stops at the first that fails (client.go:1008-1011) [observed], so a later handler, the session slice's own included, never sees the failure.
- `ResetConnection` closes the socket and forces whatsmeow's own reconnect: `Disconnected` is dispatched off the node queue and `autoReconnect` starts (client.go:612-631, 712-724), and its first try waits `AutoReconnectErrors × 2 s`, which a successful login set to 0 (client.go:653, connectionevents.go:165) [observed]. The state machine already maps that to `Reconnecting`, then `Connected` (§7.4).
- mautrix-whatsapp `5dd621f` does nothing after a handler failure, and calls `ResetConnection` only to reset every login's network connection (pkg/connector/connector.go:249-253; handlewhatsapp.go:78) [observed]. The reset is ours.

## Decision

- **The signal.** `session.NewClient` gets a `withheld` callback for each session. `cmd/whatsmeow-net`'s `recording` wraps each of the three handlers it registers (Events, Chat state, History) so that one that returns false calls it. The callback is a non-blocking send on the session's one-slot channel; a pending wake absorbs the next.
  The session slice still knows no other slice, and the Events and History slices are unchanged.
- **The redelivery.** One goroutine per session, from its creation to its deletion or the sidecar's end (`session/redelivery.go`). After a wake it waits a settle delay of 5 s, so that a burst of failed writes, as a store fault makes, is one reset, takes in the wakes of that delay, and resets the connection with whatsmeow's `ResetConnection`.
  It holds the session's `ops` for it, so no Connect, Disconnect, Logout, Pair or delete runs meanwhile, and resets only a session logged in to WhatsApp (`Connected` or `CaughtUp`): one `Connecting` or `Reconnecting` logs in soon anyway, and one the application disconnected gets the stanza at its next `Connect`. Each reset is logged at warn, with the session id only.
- **The rate limit.** After a reset, a cool-down of 1 min before the next one. An answer withheld again within it makes the next reset wait it out and doubles it, up to 30 min; one with nothing withheld brings it back to 1 min. A transient failure is delivered again about 5 s after it, plus a login, 1.1 to 1.4 s in the #10 runs [observed: §14]; a failure that never clears settles at one reset every 30 minutes, 48 a day.
  The durations are constants, not options.
- **Any failure counts.** A handler returns false only on a failed write or a panic, so the reset follows any false, also for a kind whose answer whatsmeow sends regardless (§14 question 4); such a reset brings nothing back and costs what one costs. No list of gated kinds is kept for it.
- **The fake.** The fake WhatsApp keeps a client's unanswered message stanzas and delivers them again, in order, after each login (`Connect`, `ResetConnection`, a released held login), never while the connection stays up, as measured; it models messages only, since receipts and group notifications were not measured.
  Its `ResetConnection` dispatches `Disconnected`, then reconnects and logs in at once, unless a `Disconnect` came first.
  `DeliverMessageRequest.withhold_first_answer` makes the sidecar's Events handler fail the first recording of the message, as a failed write would: in fake mode `cmd/whatsmeow-net` wraps that handler with the fake's `Failing`, inside the redelivery's wrapper, so the FullCycle runs the real signal, worker, reset and state machine.
- **Invariant 1** names it: the handler wakes the session's redelivery without waiting.

## Consequences

- What a reset costs, each time:
  - three `ConnectionStateChanged` events for the application, `Reconnecting`, `Connected` and, once the offline sync arrives, `CaughtUp` [observed: the FullCycle, and the state machine];
  - every `Online` channel of the session closes, so a paced send still queued fails without having sent anything [observed: messaging/send.go:247, 292-298];
  - a send already handed to whatsmeow may end `OUTCOME_UNKNOWN`, which is never retried (invariant 10) [inferred: messaging/send.go:392-395];
  - the session's app state recovery and history worker restart with the new connection [inferred: ADR 0031, ADR 0035].
  How long the session is not logged in on real WhatsApp, and when it is caught up again, is [unknown] until the live test below runs.
- Whether WhatsApp also brings the message back on a connection that stays up beyond 10 minutes stays [unknown]; the reset comes long before that would matter.
- A failure that never clears resets the connection every 30 minutes for as long as it lasts; how WhatsApp treats a device that reconnects that often is [unknown].
- Tests:
  - the worker on a fake clock (`testing/synctest`), in `internal/session/redelivery_test.go`: one reset 5 s after a burst, the cool-down doubling to its cap, back to its base, no reset outside `Connected` and `CaughtUp` or after a delete, a `withheld` that never waits;
  - the wrapper, in `cmd/whatsmeow-net/recording_test.go`: the Events and the History handler each wake it when a write fails;
  - the fake, in `internal/fakewhatsapp/redelivery_test.go`: the redelivery at a login only, the reset, and `withhold_first_answer`;
  - the FullCycle `Events/WithheldRedeliveryTests.cs`: a withheld message reaches the handler again within 30 s, after `Reconnecting` and `Connected`, with no reconnect by the application;
  - the live `Live_AWithheldAnswer_TheSidecarResetsTheConnection_TheMessageComesAgainWithinAMinute`, which logs the reset's cost on real WhatsApp; the owner runs it.
