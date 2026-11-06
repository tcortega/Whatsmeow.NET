# History bounds per sync type, 2026-11-06

Evidence for [#42](https://github.com/tcortega/Whatsmeow.NET/issues/42): the history one fresh pairing delivered, per sync type, with message timestamp bounds, under `RequestFullSync` and a `FullSyncDaysLimit` of 7.
It follows the [2026-11-05](0042-hosted-sample-2026-11-05.md) and [2026-11-06 timing](0042-history-timing-2026-11-06.md) records, which stay as they were.
It records what was delivered under one configuration; it does not show what any option caused.

## Scope and method

[observed: `records.jsonl` `run_start`] The run used a `livetest` build of the sidecar at product commit `377cac2db65d55aa437c72de6e60b5571ab622b5`, version `0.0.0-alpha.0.79`, whatsmeow `307a2edf117d`, Go 1.27.3, protocol 1, binary SHA-256 `76576f1b55fa31174073ceb7fa9042a776fa15bc2f74922514bb8f1a27ef538b`.
It is a test build, never a release; it is the first #42 run at this whatsmeow pin.

[observed: `LiveFreshCompanionTests.cs`, ADR 0060] The live test `Live_AFreshCompanion_SeesTheContactChangesAsTheFakeModeChecks_AndRecordsItsHistoryBounds` paired a fresh companion of the live tests' peer account by QR code, on a new database, in a sidecar of its own.
[observed: root's private preflight and postflight identifiers] That account is the owner's personal account, Alice in the earlier #42 records; the identifiers stay private.

[observed: `LiveFresh.cs`, `records.jsonl` `run_options`] It requested `RequestFullSync = true` and `FullSyncDaysLimit = 7`, and left `FullSyncSizeLimitMegabytes` and `StorageQuotaMegabytes` unset.
The resolved values, a required full sync, 7 days, no size limit and a 10,240 MB quota, are read from the source (whatsmeow `store/clientpayload.go:131-134,156`, `cmd/whatsmeow-net/history.go:30-51`, `Sidecar/SidecarProcess.cs:229-233`), not measured on the wire.

[observed: `LiveHistoryBounds.cs`] The record (`IntegrationTests/Support/LiveHistoryBounds.cs`) keeps, per history event, its sync type, entry count and the oldest and newest message timestamp of the batch, and per blob the progress event's counts; never a JID, id, name or text.
A row's time is when the record's handler ran, not when WhatsApp sent the blob or the sidecar received it.
Its window runs from `Paired` until 10 minutes with no history event (`quiet_window`), or 30 minutes after `Paired` (`censored_bound`), whichever comes first.
A hold on the history, from its first event until the contact steps end (ADR 0060), would delay the quiet; [observed: `analysis.json` `hold: null`] no hold started, because the steps ended before the first history event.

## The run

[observed: `analysis.json`, `postflight-checks.json`, `live-summary-redacted.log`] The capture is valid and complete: `capture_valid` and `experiment_complete` true, no write failure, no invalidation, no event outside the window, no progress failure, no sidecar exit or restart.
The device paired at 20:55:41.823 UTC; the window closed `quiet_window` 1,172.003 s later, 600 s after the last history event, so it is not censored.
A quiet window is observed silence, not proof that the phone had no more history to send.

[observed: the same files] The fresh device was then verified logged out (`LoggedOut`, code 0) and deleted.
The live and peer stores' identities and devices were unchanged and in no stopping state, the retained database was byte for byte unchanged, the fresh store had no session or device left, and the QR file was removed.

[observed: `live-summary-redacted.log`, the coordinator's run record] The live test as a whole exited 1: its business name and push name branches failed with `NotNullException`, tracked in #153 and #154; its group picture set and remove passed.
Those two branches ended before the first history event [observed: `live-summary-redacted.log`, `records.jsonl`], and the history record's own checks passed regardless.

## History delivered

[observed: `records.jsonl`, recomputed independently and equal to `analysis.json`] Entries are delivered entries, not unique messages; bounds are the oldest and newest message timestamps over a type's batches, UTC.
Observed is when the record's handler saw the type's first and last batch, in seconds after `Paired`.

| Sync type | Batches | Entries | Oldest message | Newest message | Oldest, days before pairing | Observed |
| --- | ---: | ---: | --- | --- | ---: | --- |
| `InitialBootstrap` | 4 | 998 | 2024-07-01 20:44:17 | 2026-11-06 20:55:12 | 858.008 | 2.366 to 2.375 s |
| `Recent` | 253 | 63,346 | 2024-07-01 20:44:17 | 2026-11-06 20:55:12 | 858.008 | 10.016 to 571.035 s |
| `Full` | 1 | 119 | 2026-11-03 16:26:36 | 2026-11-06 13:53:33 | 3.187 | 571.425 s |
| Total | 258 | 64,463 | | | | |

[observed: `records.jsonl`] The bounds are per batch: all 4 `InitialBootstrap` batches and 229 of the 253 `Recent` batches contain a message more than 7 days older than the pairing, and the `Full` batch none.
How the messages' ages are distributed inside a batch was not captured.

[observed: `records.jsonl` `history_progress`] 18 progress events: 1 each for `NonBlockingData`, `InitialBootstrap`, `InitialStatus`, `PushName` and `Full`, and 13 for `Recent`.
`NonBlockingData`, `InitialStatus` and `PushName` delivered no batch.
No event had the type `Unspecified`, which a `RECENT_CONVERSATIONS_ON_DEMAND` blob would map to at this pin (§14 History sync).

### Skips and counts

[observed: `analysis.json` `skips`] The client logged no unreadable history message (event 41) and no event skipped unread (event 32).
[observed: `records.jsonl` `history_progress`] The sidecar's progress events still count 45 skipped: 30 in `InitialStatus`, 2 in `InitialBootstrap` and 13 in `Recent`.
That count includes status updates, messages over 15 MiB and messages whose parse panicked (§7.7); which reason each of the 45 is, is [unknown].

[observed: the same rows] The progress events' `MessageCount` totals 64,381, against 64,463 entries in the batches; 82 more entries were delivered than counted.
[inferred] History poll votes are delivered beside their poll and not counted (§7.7, ADR 0047), which would explain a difference; it was not measured here.

## Against the earlier records

[observed: the [2026-11-05](0042-hosted-sample-2026-11-05.md#default-history) and [2026-11-06 timing](0042-history-timing-2026-11-06.md#history-delivered) records, this one] Entries per type, Alice's account, each a fresh pairing:

| Pairing | Options | Pin | `InitialBootstrap` | `Full` | `Recent` |
| --- | --- | --- | --- | --- | --- |
| 2026-11-05 sample | defaults | `56e5ad0581ce` | 1,000 | none | 9,998, then 96,882 more in the controlled phase |
| 2026-11-06 12:04:22 | full sync, 7 days | `56e5ad0581ce` | 998 | 104 | 45,330 |
| 2026-11-06 15:54:51 | full sync, 7 days | `56e5ad0581ce` | 998 | 119 | 50,414 |
| 2026-11-06 20:55:41, this record | full sync, 7 days | `307a2edf117d` | 998 | 119, oldest 3.187 days | 63,346 |

The defaults delivered no `Full` entry; each full-sync pairing delivered some.
The 2026-11-06 timing record bounds its pairings' types together; the 2026-11-05 record bounds each type.
With the seven-day limit requested, `InitialBootstrap` and `Recent` still delivered messages 858 days old [observed: this record], so it did not bound those types in this run.
[inferred] The `Full` type's 3.187-day bound fits a seven-day limit on that type.
These pairings differ in time, account state, harness and pin, and change no single option, so they are not a controlled comparison.
Whether the seven-day limit caused the `Full` bound, rather than the account's recent activity or WhatsApp's choice, is [unknown].
`FullSyncSizeLimitMegabytes` and `StorageQuotaMegabytes` were never varied: [unknown].
No sidecar memory was sampled in this run.

## Against #42's acceptance criteria

* The sample and its fake-mode check: met, ADR 0049.
* Two sessions echoing on their own, and a logout or disconnect of one not stopping the other: met through the disconnect, in the 2026-11-05 record.
  A phone-side logout of one sample session while the other echoes was not run, a limitation of that evidence.
  This run's fresh device was logged out with the live and peer devices intact [observed: `postflight-checks.json`], but it is not the sample, so it adds nothing to this criterion.
  The accounts are the owner's, not dedicated test numbers.
* Receipt to handler with and without history pending: met in the [2026-11-06 timing record](0042-history-timing-2026-11-06.md#the-completed-run), at `56e5ad0581ce`; not measured again at `307a2edf117d`.
* Telemetry: met in the 2026-11-05 record, as `whatsmeow.process` with a `whatsmeow.send` child, and four metrics.
* Memory with two sessions: the 2026-11-05 samples, without a capacity claim.
  What the history options limited: partly met.
  What was delivered is now `[observed]` per sync type under one configuration (this record); what any option limited, as a cause, is [unknown], and so are the size and quota options and the completeness of the phone's history.
* Every Phase 3 issue closed: #42 is the milestone's only open issue [observed: milestones API, 2026-11-06].

## Evidence provenance

The coordinator retained the files privately under run identifier `42-bounds-20261106`; they hold aggregates and timestamps only, but are not attached.
[observed: `manifest.json`, `records.jsonl.receipt.json`] The manifest's hashes match the files, and the receipt's SHA-256 and line count, 286, match `records.jsonl`.
The analyzer, run again on `records.jsonl`, gives the same `analysis.json`.

| Artifact | SHA-256 |
| --- | --- |
| `records.jsonl` | `6c202eaa99b2860f5578987e855d11fd3f8708b2011c8c7a8ecedf841ef6fe65` |
| `records.jsonl.receipt.json` | `ed68babb96c64b2df6b335a465afa18c70d895e3fa571e3711221c57b5594c64` |
| `analysis.json` | `d86d021f7e37cf289c9e6b2eebbb19c33af29c7017353b8b6d71f174a10363e3` |
| `postflight-checks.json` | `c8f3df1e085d8e9284b52ec15ca968363ab9b20c28537240b8860f5c5e22b96b` |
| `live-summary-redacted.log` | `f25c86a5ae5db95c4dcf6f0150c24ec96ed87f90d8f2eb2923974544e34f5f32` |
| `manifest.json` | `67b36e45e071b29cb3024d7cdde8ddb21ae33316a3afcdc7cf5c62bf77447146` |
| `analyze-histbounds.py` | `f4eacfa35d26f73dcaf3decac32f3f1bc06bad04dfa7b895c3ef3c2c9de93c51` |
