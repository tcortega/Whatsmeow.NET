# 0068. Group commit at the write gate

- Status: Accepted
- Date: 2026-11-10
- Evidence: issue #176; GitHub Actions run 42869795091, `external (win-x64)`, its `fake-sidecar.log`; the laptop runs below (Windows 11 26200, NTFS, 4 cores pinned); the `internal/store` tests named below
- Amends: [ADR 0040](0040-a-fair-write-gate-for-the-sidecars-writes.md) (one transaction per writer; a write's context ends its statements and stops its commit); ARCHITECTURE §7.3 (the handler's wait at the gate)

## Context

On win-x64 CI one restart of the External-mode sidecar was followed by 210 s in which no ack, send or session write completed, and `MessagingCrashTests` and `PresenceCrashTests` timed out [observed: run 42869795091].
Every other restart in that run drained the acks of about 300 sessions in 2.3-5.6 s [observed: the same log].
The send that hung was waiting for its response headers, so the sidecar never answered it, while a repeat of an earlier send, which only reads its record, was answered [observed: the test's stack and log].
The acks in flight all failed at once when the next crash test killed the sidecar [observed].
So the sidecar's writes stalled and its reads did not [inferred].

A restart makes about three writes for each connected session, and all of them queue at the gate ahead of the first send.
Each session reopens its event stream, which ensures its cursor (a write).
Each session records its new connection state (a write), and the client acks it (a write).
A goroutine dump 1 s after a restart on the laptop showed 296 cursor writes and 165 acks waiting for the gate, and the gate's own counts showed up to about 570 writes queued [observed: a diagnostic build].
A cursor write that changes nothing costs 0.01 ms; a write that changes a row costs 1.8 ms, its commit's sync [observed: laptop, the sidecar's pragmas].
With one commit per write, a send behind that backlog waits for every commit before it.
With each commit held 150 ms, as a slow disk would, the backlog took about 85 s to drain, and `EventCrashTests` timed out [observed: the laptop, an injected hold on each write that changed rows].
ADR 0040 measured commits of p50 153 ms and up to 7.6 s on loaded `windows-2025` runners.
That the CI run's disk was briefly that slow is [inferred]: the sidecar started after the hang was slow too, about one ack per 1.3 s for 20 s, then normal.

## Decision

- **The writes queued behind a commit share the next one.** The writer handed the gate leads a batch.
  It begins one transaction, runs its own write, then takes the oldest waiting writes, in arrival order, and commits once.
  It never waits for more writes to arrive.
- **Limits.** A batch takes at most 64 writes, and takes no more once its writes have run for 250 ms.
  A write behind k others waits for about (k+1)/64 commits, not k+1.
  Batches stay in arrival order, so no writer is overtaken.
- **Each write keeps its own outcome.** Each write runs in a savepoint of its own (`SAVEPOINT`, then `RELEASE`).
  A write whose function fails, on any statement, or panics is rolled back to its savepoint and gets its own error; the others commit.
  A panic is raised again on the writer's own goroutine.
  Only a failure of the transaction itself (its savepoint, release or commit, or a write that ends it) fails every write of the batch, none of them kept, as one failed commit failed one write before; each innocent write's error names the cause.
  The batch then takes no more writes, so the writes still queued commit in the next one.
  A write function that ends the leader's goroutine (`runtime.Goexit`) still frees the gate, and the batch's other writes fail, none kept.
- **A write returns only once the commit holding it is durable.** Waiters are told their outcomes after `COMMIT` returned (`synchronous(FULL)`), with the connection back in the pool. So §13 invariant 2 holds: the outbox wakes a session's streams, and an ack closes its feed, after `Tx` returned.
- **Cancellation is checked only before a write joins a batch.** A write joins when a leader takes it, after `BEGIN`.
  A write whose context ended before that leaves the queue, or is skipped when taken, and never runs, as before (`TestAWriterWhoseContextEndsLeavesTheQueue`, `TestAWriteWhoseContextAlreadyEndedNeverBegins`, `TestAWriteWhoseContextEndsInItsBeginNeverRuns`).
  Once it joined, it runs on a context that never ends, carrying its caller's values, and gets its batch's outcome even if its caller's context ends meanwhile.
  Why: SQLite rolls back the whole transaction when a write statement inside it is interrupted, and modernc interrupts a statement when its context ends, so one caller's cancel would undo every write of the batch.
  The alternatives were re-running the others, which would make every write function run more than once and need to be idempotent, or failing the whole batch for one caller's cancel.
  This extends the rule a write already had once its commit started: ended during `COMMIT`, applied.
- **Closing the store waits for the batch in flight.** `Store.Close` refuses new batches from then on and waits for the one in flight, which still takes queued writes up to its limits, so the wait is bounded by its 250 ms of writes and its commit (and its `BEGIN`, which waits for whatsmeow's writes up to the `busy_timeout`, as before). Writes still queued then fail.
  So the store still never closes with a write's connection open (#124).
- **Scope does not change.** whatsmeow's own store writes stay outside the gate (ADR 0040); `BEGIN` still waits for them up to the `busy_timeout`, now once per batch.
  The event handlers keep their `store.BusyTimeout` deadline, which now bounds only their wait for the gate.
  Callers keep `Tx` and `Exec`; a function given to `Tx` must still write only through its transaction, and runs once.

## Consequences

- A restart's backlog costs about a 64th of the commits it did, so a send after a restart of 300 sessions is durable after at most 16 commits instead of 902 (`TestASendAfterA300SessionRestartWaitsForFewCommits`).
- A caller whose context ends after its write joined a batch still has its write kept.
  A send checks its call once its record is written: when the call ended meanwhile, nothing was handed to whatsmeow, so it deletes the record and ends with the call's error, never sent on an ended context and never left pending (`TestACancelWhileItsRecordIsWrittenSendsNothingAndLeavesNoRecord`).
- A write function that runs long holds every write of its batch, up to the batch's 250 ms budget, which is only checked between writes.
  A statement that never ends would hold the gate, as it would have before when its caller never cancelled.
- Tests: `internal/store` `TestASendAfterA300SessionRestartWaitsForFewCommits`, `TestAFailingWriteInABatchFailsAlone`, `TestNoWriteReturnsBeforeItsBatchCommitted`, `TestABatchTakesAtMost64Writes` (each batch's size), `TestABatchStopsTakingWritesOnceItsTimeIsSpent`, `TestCloseWaitsForTheBatchInFlight`, `TestACommitThatFailsFailsEveryWriteOfItsBatch`, `TestAWriteThatBreaksTheTransactionLosesItsBatch`, `TestAWriteThatEndsTheLeadersGoroutineFreesTheGate`, `TestAWriteWhoseContextEndsAfterItJoinedCommits`.
  ADR 0040's tests stay, apart from those for statement cancellation: `TestACancelledStatementInAWriteEndsAndTheWriteIsRolledBack` is gone, and `TestAWriteWhoseContextEndedDoesNotCommit` is now `TestAWriteWhoseContextEndsAfterItJoinedCommits`.
  `TestACancelledWriteGivesItsConnectionBackBeforeItReturns` now expects the write kept.
  `TestTheGateHandsItselfOverInArrivalOrder` checks that the writes enter in arrival order and that no writer gets a lap ahead of one still waiting, since a batch takes several writes at once.
  `TestACancelledWriterNeverKeepsTheGate` now drives the gate through `Tx`, one write per batch.
  A test-only seam, `SetBeforeCommit`, `SetCommit` and `StopTheClock` in `export_test.go`, counts, reads or fails commits and freezes the batch's clock; it is in no binary.
