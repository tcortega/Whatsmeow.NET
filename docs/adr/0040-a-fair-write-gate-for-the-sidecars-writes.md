# 0040. A fair write gate for the sidecar's writes

- Status: Accepted
- Date: 2026-11-02
- Evidence: issue #89; GitHub Actions runs 41488816188 (the loop), 41492555548 (the bench) and 41497698795 (the loop with the gate), win-x64 `windows-2025`; ARCHITECTURE §14 "Measured for writes under load (#89)"; the `internal/store` tests named below
- Amends: ARCHITECTURE §7.3 (the handler's only wait)

## Context

`TestConcurrentRecordsNeverLetAReaderSkipASeq` (4 writers, 50 records each, one live reader) timed out on win-x64 CI.
A temporary loop ran the sidecar's Go suite 48 times on six `windows-2025` runners, with each `Record` timed, and the test failed in 5 runs [observed: run 41488816188].
Two things were behind it.

- **Commits are slow on a loaded Windows disk.** Under the suite's load the outbox package's commits took p50 153 ms, p90 634 ms and up to 3.0 s in a failed run, against about 4 ms on the same runner idle; the goroutine dump of a failure shows a writer in `Tx.Commit` → `walFrames` → `_winSync` [observed]. That is the disk under `synchronous(FULL)`, which invariant 2 needs (§5), and it is not ours to change.
- **A writer starves while the others commit.** Every writer of the sidecar takes SQLite's write lock with `BEGIN IMMEDIATE` (`_txlock=immediate`, §5) on its own pooled connection, and one that finds it taken waits in SQLite's busy handler, which sleeps 1, 2, 5 ... up to 100 ms between tries (`sqliteDefaultBusyCallback`, modernc v1.61.1, built with `HAVE_USLEEP`) [observed: source]. A writer that just committed begins again at once, so a sleeping one rarely finds the lock free [inferred: from the source and the waits below].
  In the suite's runs one `BeginTx` waited up to 27.6 s, near the 30 s `busy_timeout`, while the longest commit took 7.6 s (4.2 s in the tests' snapshots); even isolated, with no commit over 0.26 s, one waited up to 4.4 s [observed: 48 suite runs and 120 isolated runs].

The starvation is not the test's: every slice's writes and whatsmeow's own store writes share that pool and that busy handler.
So with several sessions online on a slow disk, an event handler can wait seconds for its turn while others commit, and past the `busy_timeout` its write fails, it returns false, and the stanza waits for WhatsApp's redelivery [inferred: no user report; the mechanism is the same].

A bench on the same runners took the lock 200 times from 4 writers, idle and while the suite ran, 24 rounds [observed: run 41492555548]:

| How writers take the lock | Total | Longest wait for the lock (median of the rounds) |
| --- | --- | --- |
| SQLite's busy handler (as today) | 1.2-11.3 s | 0.84-8.78 s (3.66 s) |
| a Go `sync.Mutex` before `BEGIN` | 1.0-15.2 s | 0.04-4.86 s (0.45 s) |
| one pooled connection (`SetMaxOpenConns(1)`) | 1.0-14.6 s | 0.07-1.60 s (0.85 s) |
| one writer, no contention: one commit | 1.0-12.1 s for 200 | 0.01-0.92 s |

The disk sets the total; how the lock is handed over sets the longest wait.

## Decision

- **Every write the sidecar itself makes goes through one gate, `store.Writer`, which hands the write lock over in arrival order.** A writer queues in Go before `BEGIN`, and the one that finishes hands the gate straight to the oldest waiter, so a writer waits for the writers ahead of it and no one else [observed: `TestTheGateHandsItselfOverInArrivalOrder`].
  Each of those holds it for its `BEGIN`, which still waits for whatsmeow's own writes, up to the `busy_timeout`, and its commit. So the bound is the writers ahead × (that `BEGIN` wait plus a commit), not a number of commits alone.
  It is held from `BEGIN` to `COMMIT` or `ROLLBACK` only, also when the function panics, which rolls back before the gate is handed on [observed: `TestAPanicInAWriteLeavesNoOpenTransaction`]: `Writer.Tx` for a transaction, `Writer.Exec` for one statement. Reads do not take it; WAL readers never wait for the writer.
  Ahead of an event handler can sit any write of the sidecar: a history batch of up to 1 MiB (one message alone up to 15 MiB), an `Ack` that deletes many events, a `DeleteSession` whose cascade takes the session's rows.
- **It is the store's, not a global.** `store.Open` creates it with the database (its migration runs before the store is handed out, with nothing to contend with), and every slice that writes is given it with the database; `forbidigo` rejects `ExecContext`, `Exec`, `BeginTx`, `Begin`, `Conn`, `Prepare` and `PrepareContext` on a `*sql.DB` outside `internal/store` and tests, so a write that skips it fails lint. It is a backstop: reads stay on the `*sql.DB` (banning them flags 7 reads in 5 slices), so a `QueryContext` or `QueryRowContext` that writes gets past it.
- **A wait ends with its context.** A writer whose context ends leaves the queue and gets the context's error, wrapped; one that was handed the gate as its context ended passes it on, so the gate is never lost [observed: `TestAWriterWhoseContextEndsLeavesTheQueue`, `TestACancelledWriterNeverKeepsTheGate`].
- **An event handler's write gives up after the `busy_timeout`.** whatsmeow gives a handler no context, so the writes the handlers make, the Events slice's outbox write, the History slice's queue writes and the media retry delete, get a deadline of `store.BusyTimeout` (30 s, the pragma's). Past it the write fails, the handler returns false, the answer is withheld, and WhatsApp redelivers the stanza at the session's next login (§7.3, #91) [observed: `TestAHandlerWriteStuckBehindTheGatePastItsDeadlineGivesUpAndWithholds`, `TestAQueueWriteStuckBehindTheGatePastItsDeadlineGivesUpAndWithholds`]. That is the bound the `busy_timeout` alone gave before the gate.
- **A write inside a write fails at once.** The gate is not reentrant, and a nested write would wait for itself. `Writer.Tx` gives its function a context that marks the gate held, and a `Tx` or `Exec` on that context fails with `store.ErrNestedWrite` instead of waiting [observed: `TestAWriteInsideAWriteFailsAtOnce`]. That is why `outbox.Record`'s extra writes get the transaction's context.
- **Scope: the sidecar's writes only.** whatsmeow's own store writes (`sqlstore`, through dbutil on the same `*sql.DB`) keep taking the lock through SQLite's busy handler and `busy_timeout`, as before: they go through whatsmeow's code, which this gate cannot reach without a fork (ADR 0013). With the gate, only one of the sidecar's writers competes with them at a time.
- **Not one connection.** `SetMaxOpenConns(1)` would cover whatsmeow's writes too, but its longest wait was longer than the gate's in the median round, 0.85 s against 0.45 s (`database/sql` hands a freed connection to a random waiter: Go 1.27.3 `database/sql/sql.go:1554, 3716`), it puts every read behind the writer, and a read on the database inside a transaction would wait for itself.
- **Durability does not change.** `synchronous(FULL)` and one commit per recorded event stay (§5, invariant 2).

## Consequences

- §7.3: the handler's waits are the gate, then SQLite's `busy_timeout` against whatsmeow's writes, and the write gives up after the `busy_timeout` in all.
- A writer's worst wait is now the writers ahead of it × (the `BEGIN` wait against whatsmeow's writes plus a commit), on any disk, where it was however long the others kept the lock; the total is still the disk's.
- Measured with the gate, maxima over every variant of the loop (4 × 10 plain and `-tags fakewhatsapp`, 4 × 50 plain, and the T2 runs): a wait at the gate up to 4.29 s, a commit up to 4.89 s (6.34 s in the slow-commit lines, which see commits after a test's last snapshot), a `Record` up to 7.25 s, a `BeginTx` up to 0.08 s; before, a `BeginTx` up to 27.61 s [observed: runs 41497698795 and 41488816188].
- A slice that writes takes a `*store.Writer` beside its `*sql.DB`; a new write site uses `Tx` or `Exec`, or lint fails.
- `TestConcurrentRecordsNeverLetAReaderSkipASeq` records 4 × 10 instead of 4 × 50: 200 commits alone took up to 27 s of its 30 s bound on a loaded runner, and it still kills the mutation it is for, a seq taken from a counter in memory before the transaction instead of from SQLite inside it, in 9 of 20 local runs at 4 × 10 with the gate (19 of 30 at either size before it). `TestAReplacedStreamWhoseClientStoppedReadingEnds` records 8 events of 64 KiB instead of 64 of 32 KiB, and takes a new bound after them: its 64 commits ran inside the bound of the stream it checks, and took up to 24.8 s of the 30 s.
