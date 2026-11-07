# History under a one-day limit, 2026-11-07

Evidence for [#42](https://github.com/tcortega/Whatsmeow.NET/issues/42), the history half of its memory and history criterion, and for [#36](https://github.com/tcortega/Whatsmeow.NET/issues/36), the Contacts FullCycle's push name branch: one fresh pairing with `RequestFullSync` and a `FullSyncDaysLimit` of 1.
It is the one-option change against the seven-day [bounds record](0042-history-bounds-2026-11-06.md), whose run stays the baseline; the earlier records stay as they were.
It records delivered bounds; that an option caused them is [inferred] at most.

## Scope and method

[observed: `records.jsonl` `run_start`] A `livetest` sidecar build at product commit `2768e554814883fcbccc1bdcfe60d43118ce2acd`, version `0.0.0-alpha.0.84`, whatsmeow `307a2edf117d`, Go 1.27.3, protocol 1, binary SHA-256 `1562085906d6d01c55de31d32e6ae9200442f573d4361d1238641ee8396f49a5`; a test build, never a release.

[observed: ADR 0060, `LiveFreshCompanionTests.cs`] The same live test as the baseline paired a fresh companion of the peer account by QR code, on a new database, in a sidecar of its own.
[observed: root's private preflight and postflight identifiers] The account is the owner's personal one, Alice in the earlier #42 records; the identifiers stay private.

[observed: `records.jsonl` `run_options`] It requested `RequestFullSync = true` and `FullSyncDaysLimit = 1`, and left the size limit and the quota unset.
The resolved values, a required full sync, 1 day, no size limit and a 10,240 MB quota, are read from the source as in the baseline, not captured on the wire.

[observed: `LiveHistoryBounds.cs`, `analysis.json` `age_basis`] The record is the baseline's, plus five counts per batch: messages dated after the pairing, up to 1 day before it, over 1 to 3, over 3 to 7, and over 7 days, each upper bound included.
Ages are at the `paired` row's UTC time, the test machine's clock, against WhatsApp's message times.
They say what was delivered, not what any option made eligible.

## The run

[observed: `analysis.json`, `records.jsonl`] The capture is valid and complete: no problem, no progress failure, no write failure, no invalidation and no excluded row.
The device paired at 01:38:36.049 UTC.
History began 0.926 s after `Paired` on the monotonic clock; a hold from 0.929 to 1.952 s (1.023 s) kept the record's events until branches 2 and 1 ended (ADR 0060).
The last history event came at 66.005 s and the window closed `quiet_window` at 666.005 s, not censored.
A quiet window is observed silence, not proof that the phone had nothing more to send.

[observed: `postflight-checks.json`, `records.jsonl`] The fresh identity was verified and the fresh device's logout verified; all 12 postflight checks are true, among them unchanged live, peer and retained identities and devices, the retained database byte for byte unchanged, and no fresh device left linked.

## History delivered

[observed: `records.jsonl`, recomputed independently and equal to `analysis.json`] Entries are delivered entries, not unique messages; message times are UTC; observed is when the record's handler saw the type's first and last batch, in seconds after `Paired` on the monotonic clock (`MonoNs`), the clock of the window's close.

| Sync type | Batches | Entries | Oldest message | Newest message | Oldest, days | Up to 1 d | 1 to 3 d | 3 to 7 d | Over 7 d | Observed |
| --- | ---: | ---: | --- | --- | ---: | ---: | ---: | ---: | ---: | --- |
| `InitialBootstrap` | 4 | 997 | 2024-07-01 20:44:17 | 2026-11-07 01:36:53 | 858.204 | 300 | 98 | 48 | 551 | 1.981 to 2.033 s |
| `Recent` | 205 | 51,134 | 2024-07-01 20:44:17 | 2026-11-07 01:36:53 | 858.204 | 3,076 | 2,365 | 2,640 | 43,053 | 11.368 to 65.008 s |
| `Full` | 1 | 23 | 2026-11-06 02:52:19 | 2026-11-06 22:03:10 | 0.949 | 23 | 0 | 0 | 0 | 66.005 s |
| Total | 210 | 52,154 | | | | | | | | |

No message was dated after the pairing.
[observed: `records.jsonl`] All 4 `InitialBootstrap` batches and 201 of the 205 `Recent` batches hold a message more than 1 day old; 46 `Recent` batches hold messages 1 to 7 days old.
16 progress events: 1 each for `NonBlockingData`, `InitialBootstrap`, `InitialStatus`, `PushName` and `Full`, 11 for `Recent`; none of the client's type `Unknown`.

### Skips and counts

[observed: `analysis.json` `skips`] The client logged no unreadable history message and no event skipped unread: 0 and 0, the .NET side only.
[observed: `records.jsonl` `history_progress`] The sidecar's progress events count 37 skipped: 20 in `InitialStatus`, 3 in `InitialBootstrap` and 14 in `Recent` (baseline: 45, as 30, 2 and 13).
Which of the reasons the bounds record lists each one is, is [unknown].
[observed: the same rows] Their `MessageCount` totals 52,075 against 52,154 delivered entries, 79 more delivered, all in `Recent` (baseline: 82); history poll votes would explain a net figure like this [inferred], as in the bounds record.

## What the one-day limit limited

[observed: `42-next-decision-result.md` §4, in the durable evidence directory, provenance below] The outcome was pre-registered before the run: the file's birth time is 2026-11-06 22:21:27 UTC, 3 h 17 min before the pairing [observed: `stat` of the durable copy, whose source timestamps root preserved].
This run matches its row "`Full` oldest at most 1 day, and `Recent` has 1 to 7 day messages".

* [observed: this run] Under a 1-day limit, `Full` stayed within 1 day (oldest 0.949 days), while the same run delivered 5,005 messages 1 to 7 days old in `Recent` and 146 in `InitialBootstrap`.
* [observed: the baseline] Under a 7-day limit, at the same pin, `Full` went back 3.187 days.
* [inferred, one pairing per days-limit value at this pin] So "the days limit bounds `Full`" is the supported reading; it is not proven.
  Messages delivered in `Recent` or `InitialBootstrap` show the account had 1 to 7 day content in this run; whether WhatsApp counted any of it eligible for `Full` is [unknown].
* [observed: this run and the baseline] In these two runs the days limit did not cap the ages `InitialBootstrap` and `Recent` delivered: under 1 day, 697 of 997 and 48,058 of 51,134 entries were more than 1 day old; under 7 days, both went back 858 days.
  Any other effect of the limit on them is [unknown].
* [observed: the bounds record's table, this run, the 2026-11-05 record and its raw `echobot.log` and `driver.log`] On Alice's account, `Full` arrived in each of the four full-sync pairings, at two pins, and in none of the one defaults pairing on 2026-11-05; Bob's defaults pairing that day, another account, had no `Full` either.
  This contrast is apart from the same-pin days comparison: the pairings differ in pin, time, harness and the days limit too, so that `RequestFullSync` causes `Full` is [inferred].
* [unknown] `FullSyncSizeLimitMegabytes` and `StorageQuotaMegabytes`: never explicitly requested or varied in any #42 run; the quota resolved to whatsmeow's default 10,240 MB each time, read from the source.
* Not claimed: wire behaviour, why `Full` had 23 entries against 119, why this run's delivery was shorter, or the completeness of WhatsApp's history.

### Against the baseline

| | Baseline, 2026-11-06 20:55:41 | This run, 2026-11-07 01:38:36 |
| --- | --- | --- |
| Days limit | 7 | 1 |
| Full sync, size, quota | true, unset, unset | same |
| whatsmeow | `307a2edf117d` | same |
| Commit, version | `377cac2`, `0.0.0-alpha.0.79` | `2768e55`, `0.0.0-alpha.0.84` |
| Record | no ages | ages |
| Hold | none | 1.023 s |
| `InitialBootstrap` batches, entries, oldest days | 4, 998, 858.008 | 4, 997, 858.204 |
| `Recent` batches, entries, oldest days | 253, 63,346, 858.008 | 205, 51,134, 858.204 |
| `Full` batches, entries, oldest days | 1, 119, 3.187 | 1, 23, 0.949 |
| Last history event, window close | 572.003 s, 1,172.003 s | 66.005 s, 666.005 s |
| Sidecar skips, .NET skips | 45, 0 | 37, 0 |

### Confounds

* Time: 4.7 hours later, at another time of day.
  The counts differ; that alone does not show the account's content changed, and what WhatsApp held `Full`-eligible at each pairing is [unknown].
* Account and phone state: another fresh pairing of the same account after the earlier ones; their effect is [unknown].
* Harness: 5 commits from `377cac2` to `2768e55`, 21 files, none under `sidecar/` [observed: `git diff --stat`]; they add the days variable, the age counts, and the named presence before the push name text, which made the hold.
* The hold kept the record's events for 1.023 s; it stops no download (ADR 0060), and the baseline had none.
* The baseline has no age counts, so the within-run control exists for this run only.
* One pairing per days-limit value, and `Full` came in one batch each time.

## Contacts branches (#36)

[observed: `159-branches-sanitized.log`, root's extract of the run's live log lines, forms and booleans only; root's check of the private `results.xml` and runner log, summarized in `report.md`]

| Branch | Result |
| --- | --- |
| 2, business name | failed, `NotNullException` at `BusinessNameAsync` (01:38:36.522), #153 |
| 1, push name | passed (01:38:38.001) |
| 3 and 4, group picture set and remove | passed (01:38:44.221) |
| 5, about | not run: no authorized stimulus (ADR 0060) |
| 6, saved name read back | not run: no expected value from the owner (ADR 0060) |

* [observed: the log] Before branch 1, the companion's rows of the business read, for the LID and the phone number alike, saved False, push False, business True, and the store paired each form with the other.
  The live session's named unavailable presence was sent, the text carried a push name, from a LID with a phone number beside it, and the branch passed.
* [observed: `LiveFreshCompanionTests.PushNameAsync`] A pass means the sender's row had no push name before the text, and the `ContactUpdated` push name change equalled `ContactChecks.PushName` in full, as in fake mode.
* [inferred] This fits the named presence hypothesis of the [stimulus record](0036-pushname-stimulus-2026-11-06.md) without proving it: the presence always came right before the text.
  What a text from the phone carries stays [unknown].
* [observed: `report.md`, root-verified `results.xml`] The test as a whole failed, 1 of 1, on branch 2 only; this is not a Contacts FullCycle pass.

## Against #42's acceptance criteria

* The sample, generic host, `AddWhatsmeow`, two keyed sessions, DI echo handlers, public API only: met, ADR 0049, #109.
* CI builds the sample and echoes one message per session on a fake-mode sidecar: met, `Samples/EchoBotTests.cs`.
* Two real phones, each session echoes on its own, a logout or disconnect of one does not stop the other: met through the disconnect, in the [2026-11-05 record](0042-hosted-sample-2026-11-05.md#live-exchanges-and-isolation).
  The accounts were the owner's, authorized as such, not dedicated test numbers.
* Receipt to handler with and without history pending: met in the [timing record](0042-history-timing-2026-11-06.md#the-completed-run), at `56e5ad0581ce`; not measured again since.
* Telemetry: met in the 2026-11-05 record, `whatsmeow.process` with a `whatsmeow.send` child and four metrics, by the coordinator's 2026-11-06 reading of the span names ([timing record](0042-history-timing-2026-11-06.md#against-42s-acceptance-criteria)).
* Memory with two sessions: met by the [2026-11-05 samples](0042-hosted-sample-2026-11-05.md#sidecar-resident-memory), a local `CGO_ENABLED=1` build, no capacity claim.
  What the history options limited: met under the pre-registered reading, above.
  For each option the runs set, what it did or did not limit is `[observed]` per sync type, its cause `[inferred]` (one pairing per days-limit value; four full-sync against one defaults pairing of Alice for `RequestFullSync`), and the options never requested are `[unknown]`.
* Every Phase 3 issue closed: #42 is the milestone's only open issue [observed: milestones API, 2026-11-07].

Every criterion now maps to evidence; closing #42 is the owner's decision.

## Evidence provenance

Root retained the files privately under run identifier `42-days1-20261107`, identical in `~/Library/Application Support/Whatsmeow.NET/evidence/42-days1-20261107`; they hold aggregates and timestamps only.
[observed: `manifest.json`, `receipt.json`] The manifest's hashes match the files, and the receipt's SHA-256 and line count, 238, match `records.jsonl`.
The analyzer, run again on `records.jsonl` with `receipt.json` beside it as `records.jsonl.receipt.json`, gives `analysis.json` byte for byte.
The branch lines are in `159-branches-sanitized.log` beside that directory, identical in both places.
The pre-registration is `42-next-decision-result.md` in `~/Library/Application Support/Whatsmeow.NET/evidence/`, root's byte-identical copy with its source timestamps.

| Artifact | SHA-256 |
| --- | --- |
| `records.jsonl` | `cc2fcfeaa0b337946600b65a9b4eb831f0f838c44db0951df3cf932fbf56d88a` |
| `receipt.json` | `d47e36e31a62135a7fd89aba25a4209d03e8d402228b68eb58dbe690b87eb6d0` |
| `analysis.json` | `1fe6ddaec8c7027ea35b66008ecaa163fb615585e272e29255065c6519066a16` |
| `postflight-checks.json` | `fcbbe5de03b39205c3c42ad792e8332cf2d8e7abd82d8a35ef0b079276ddc669` |
| `report.md` | `312555787838be332eebfec58e0c32523836ce53240ecce89a22a96e835aa8ec` |
| `manifest.json` | `f9fff2f9e5fff67bfa7d3b431f6717e9ec19db6147e7fc54d3e00b6601433074` |
| `159-branches-sanitized.log` | `5e485182ed99a59bb6c2539b6c6a7d8ddcc69f0535014b35e5dab12e85773abe` |
| `42-next-decision-result.md` | `c7cf475ec5c60d9e33a6bb3c501d54763563ba9fbf5b66abf9fafc8e08ac140e` |
| `analyze-histbounds.py` v3.1 | `fc3e4978c0cd8bad7c5d8c36941bedfee65619c7970617296742f6ecd194d18a` |
