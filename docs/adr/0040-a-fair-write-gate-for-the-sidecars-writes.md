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
  In the suite's runs one `BeginTx` waited up to 27.6 s, near the 30 s `busy_timeout`, while no commit took over 4.2 s; even isolated, with no commit over 0.26 s, one waited up to 4.4 s [observed: 48 suite runs and 120 isolated runs].

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

- **Every write the sidecar itself makes goes through one gate, `store.Writer`, which hands the write lock over in arrival order.** A writer queues in Go before `BEGIN`, and the one that finishes hands the gate straight to the oldest waiter, so with N writers none waits more than about N commits [observed: `TestTheGateHandsItselfOverInArrivalOrder`].
  It is held from `BEGIN` to `COMMIT` or `ROLLBACK` only: `Writer.Tx` for a transaction, `Writer.Exec` for one statement. Reads do not take it; WAL readers never wait for the writer.
- **It is the store's, not a global.** `store.Open` creates it with the database (its migration runs before the store is handed out, with nothing to contend with), and every slice that writes is given it with the database; `forbidigo` rejects `ExecContext`, `Exec`, `BeginTx` and `Begin` on a `*sql.DB` outside `internal/store` and tests, so a write that skips it fails lint.
- **A wait ends with its context.** A writer whose context ends leaves the queue and gets the context's error, wrapped; one that was handed the gate as its context ended passes it on, so the gate is never lost [observed: `TestAWriterWhoseContextEndsLeavesTheQueue`, `TestACancelledWriterNeverKeepsTheGate`].
  The event handler's context never ends (whatsmeow gives it none), so it waits for its turn, at most the writers ahead of it times a commit, and then SQLite's `busy_timeout` applies to `BEGIN` against whatsmeow's own writes only.
- **A write inside a write fails at once.** The gate is not reentrant, and a nested write would wait for itself. `Writer.Tx` gives its function a context that marks the gate held, and a `Tx` or `Exec` on that context fails with `store.ErrNestedWrite` instead of waiting [observed: `TestAWriteInsideAWriteFailsAtOnce`]. That is why `outbox.Record`'s extra writes get the transaction's context.
- **Scope: the sidecar's writes only.** whatsmeow's own store writes (`sqlstore`, through dbutil on the same `*sql.DB`) keep taking the lock through SQLite's busy handler and `busy_timeout`, as before: they go through whatsmeow's code, which this gate cannot reach without a fork (ADR 0013). With the gate, only one of the sidecar's writers competes with them at a time.
- **Not one connection.** `SetMaxOpenConns(1)` would cover whatsmeow's writes too, but its longest wait was longer than the gate's in the median round, 0.85 s against 0.45 s (`database/sql` hands a freed connection to a random waiter: Go 1.27.3 `database/sql/sql.go:1554, 3716`), it puts every read behind the writer, and a read on the database inside a transaction would wait for itself.
- **Durability does not change.** `synchronous(FULL)` and one commit per recorded event stay (§5, invariant 2).

## Consequences

- §7.3: the handler's waits are the gate, then SQLite's `busy_timeout` against whatsmeow's writes.
- A writer's worst wait is now about the writers ahead of it times a commit, on any disk; the total is still the disk's.
- A slice that writes takes a `*store.Writer` beside its `*sql.DB`; a new write site uses `Tx` or `Exec`, or lint fails.
- `TestConcurrentRecordsNeverLetAReaderSkipASeq` records 4 × 10 instead of 4 × 50: a seq taken from memory before the transaction fails it as often at either size (19 of 30 local runs each before the gate, mutation; 9 of 20 at 4 × 10 with it), and 200 commits alone took up to 27 s of its 30 s bound on a loaded runner. `TestAReplacedStreamWhoseClientStoppedReadingEnds` records 8 events of 64 KiB instead of 64 of 32 KiB: its 64 commits ran before the stream it bounds, and took up to 24.8 s of the 30 s.
