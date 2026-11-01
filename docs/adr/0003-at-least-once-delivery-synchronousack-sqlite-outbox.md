# 0003. At-least-once delivery through `SynchronousAck` and a SQLite outbox

- Status: Accepted
- Date: 2026-10-31
- Evidence: [ARCHITECTURE §14](../ARCHITECTURE.md#spike-results-2026-10-31) questions 3 and 4, "Other whatsmeow claims"

## Context

The first goal is no lost messages ([ARCHITECTURE §1](../ARCHITECTURE.md#1-goals-and-non-goals)).
With default settings whatsmeow acks messages before the application has persisted them [observed: [RESEARCH.md](../RESEARCH.md#at-least-once-delivery-requires-synchronousack-plus-a-sqlite-outbox) "At-least-once delivery requires SynchronousAck plus a SQLite outbox"].
WhatsBox, the closest .NET prior art, is at-most-once with no replay [observed: RESEARCH.md "Six earlier wrappers"].

whatsmeow's handler model at the pin, re-checked by hand [observed: §14 Spike results, "Other whatsmeow claims", starred items]:

- Most handlers run synchronously and in order on a node queue per client, but connection events are dispatched on their own goroutines; "one queue per client" as RESEARCH.md states it is refuted.
- On a server-side `LoggedOut`, whatsmeow deletes the device rows concurrently with dispatching the event; "whatsmeow has already deleted the device rows" is refuted.

When the 2048-node queue is full, ordering is no longer guaranteed (`client.go:900`), and a node running for about 5 minutes is left in the background (`client.go:954-970`) [observed: source review at the pin, [ARCHITECTURE §7.3](../ARCHITECTURE.md#73-the-event-handler-contract-critical); not re-checked by hand].

Which acks a handler can withhold, from a source review at the pin; items marked * were re-checked by hand [observed: §14 Spike results, Question 4]:

- Withholdable: decrypted `*events.Message` and `*events.FBMessage`, newsletter plaintext messages, non-retry receipts*, `w:gp2` group notifications*.
- Acked regardless of the handler: undecryptable messages* (`message.go:424-437`), unavailable messages, missing-secret `msmsg` (nacked), already-processed messages, retry receipts* (`receipt.go:34-38`), calls, app state, `hist_sync` receipts, and every notification type except `w:gp2` and `server_sync`*.
- A handler panic counts as success*.
- In a grouped receipt with several participants, only the last participant's handler result decides the ack* (`receipt.go:29-31`).
- How soon the server redelivers an unacked message is [unknown].

SQLite under contention, 8 sessions and 10,400 mixed ops, 3 runs each [observed: §14 Spike results, Question 3]:

- modernc v1.61.1 parses `_txlock=deferred|immediate|exclusive` (`sqlite.go:426`).
- WAL, deferred, no `busy_timeout`: 7,886-9,568 `SQLITE_BUSY` (about 90%).
- WAL, deferred, `busy_timeout(5000)`: 574-678, all in the read-then-write transaction.
- WAL, `_txlock=immediate`, `busy_timeout(5000)`: 0.
- `synchronous(FULL)` costs about 2-5x over `NORMAL` (0.7-1.9 s vs 2.0-3.2 s for the 10,400 ops), still with 0 BUSY.

## Decision

Phase 0 verdicts:

- Question 3: go, because modernc supports `_txlock=immediate`, and with it plus `busy_timeout(5000)` the contention workload saw 0 `SQLITE_BUSY`.
- Question 4: go, with the guarantee scoped to the gated kinds, because whatsmeow acks many other kinds regardless of the handler.

Design:

- whatsmeow runs with `SynchronousAck = true`, `EnableDecryptedEventBuffer = true` and `AddEventHandlerWithSuccessStatus` ([ADR 0013](0013-whatsmeow-config-follows-mautrix-no-fork.md)).
- The handler maps the event, runs one short `INSERT INTO outbox … ON CONFLICT DO NOTHING` transaction, signals waiting streams without blocking, and returns `true` only after the commit.
- It returns `false` on a DB error or a panic, using its own `recover()`.
- It never calls gRPC, never waits on .NET, never downloads media, and never returns `false` because no consumer is connected.
- The outbox assigns a durable `seq`; `StreamEvents(after_seq)` replays from a per-consumer cursor, and `AckEvents(up_to_seq)` moves it.
- .NET acks by watermark: the highest `seq` at or below which every event has finished all its handlers.
- The consumer contract is at-least-once, and `MessageId` plus `SessionId` is the dedupe key.
- The guarantee is scoped to the gated kinds above; the other kinds are best effort, and we never claim otherwise.
- SQLite is `modernc.org/sqlite` with the [ARCHITECTURE §5](../ARCHITECTURE.md#5-technology-baseline) DSN: `foreign_keys(1)`, WAL, `busy_timeout(5000)`, `synchronous(FULL)`, `_txlock=immediate`.
- FULL is chosen because invariant 2 needs the outbox row to survive power loss, not only a process crash [inferred: from SQLite's documented WAL durability, not measured here].
- The `LoggedOut` handler never reads, writes or deletes the device rows; it only writes the outbox transition.
- `UndecryptableMessage` goes into the outbox like any event; the recovered message is tied to it by the dedupe key if it carries the same `MessageId` [inferred; verify in the Phase 1 live test].

## Consequences

- Invariants 1, 2 and 3 ([ARCHITECTURE §13](../ARCHITECTURE.md#13-invariants)) follow from this record.
- Handlers must be idempotent; this is documented on every handler interface.
- After a crash of either process, a repeat only follows the last acked cursor and carries the same `MessageId` and `SessionId`; nothing is lost ([ARCHITECTURE §15](../ARCHITECTURE.md#15-roadmap) Phase 1 exit).
- Kill-either-process integration tests prove it ([ADR 0011](0011-integration-tests-by-default.md)).
- Writes pay the `synchronous(FULL)` cost measured above.
- The ack-gating facts are true at the pin only and are re-checked on every whatsmeow bump.
