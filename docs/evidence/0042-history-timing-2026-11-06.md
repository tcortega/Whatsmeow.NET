# History and live traffic timing, 2026-11-06

Evidence for [#42](https://github.com/tcortega/Whatsmeow.NET/issues/42): receipt to handler for live messages, measured with history pending and without, on a freshly paired session.
It follows the [2026-11-05 record](0042-hosted-sample-2026-11-05.md), which stays as it was.
Eight live runs are recorded below, the failed ones included; only the last one completed the measurement.

## Scope and method

[observed: `provenance.json`, `go-provenance.txt`] Every run used one diagnostic build: client and sidecar from product commit `7be34e3d5dc02176048914980bf194c6fd50f939` with a temporary timing patch, version `0.0.0-alpha.0.999`, never a release.
The sidecar reports Go 1.27.3, `CGO_ENABLED=0`, `darwin/arm64`, `-trimpath` and whatsmeow `56e5ad0581ce`.
Its module is `(devel)` with no VCS fields, so `vcs.modified` is not reported, which is not the same as false.
Every run was at whatsmeow `56e5ad0581ce`; #148 proposes a bump to `307a2edf117d`, unmerged when this was written, and no run used it.
[observed: the upstream diff between the two] That bump changes app state, its key share logging and `server_sync` handling, and the history sync schema: a new sync type, `RECENT_CONVERSATIONS_ON_DEMAND`, and the omitted-conversation fields replaced (`WAWebProtobufsHistorySync.proto`).
[inferred] The live message path from the callback to the handler has no changed file; whether the history delivery measured here behaves the same at that pin is [unknown].

[observed: the diagnostic's source] The start of a sample is a native timestamp at entry to the sidecar's Events callback for the decrypted message, before mapping and the outbox.
Its end is a native timestamp in .NET after the exact controlled-message filter, just before the sample's compiled `EchoHandler` runs.
So it measures callback receipt to handler entry: it excludes the wire, whatsmeow's own queueing and decryption, and the echo's send.
Both processes read macOS `CLOCK_MONOTONIC` with 1,000 ns resolution; the times are reported in milliseconds to the microsecond, not to the nanosecond.
[observed: `root-freshpair-clock-before.log`, `root-freshpair-clock-after.log`] 1,000 cross-process brackets passed before and after the completed run (median width 13,000 and 12,000 ns, maximum 4,765,000 and 5,974,000 ns); a bracket includes scheduling and is not subtracted from any sample.

[observed: the diagnostic's source] A history interval wraps one whole delivery of a history blob in the sidecar: download, parsing, waiting for the client, recording and the finish.
A pending sample is eligible when its receipt is strictly inside an interval of the receiving session that ended ok; a receipt inside an interval that retried a failed download is not.
A quiet sample is eligible after ten minutes with no history mark of the session, an empty queue and no open interval.
Each exchange must have one send, one receipt, one handler entry and one returned echo; nothing is retried, and every failure is kept.

[observed: `driver-freshpair/Program.cs`] The receiver paired with `RequestFullSync = true` and `FullSyncDaysLimit = 7`, the other two options at their defaults; the earlier drivers set the same two [observed: their `Program.cs`].
The owner authorized a personal account as Alice, the receiver, and a Business account as the sender, not dedicated test numbers [observed: the coordinator's record of the owner's authorization].
The sender ran in its own sidecar on its persistent store [observed: `driver-freshpair/Sender.cs`].

## The runs

[observed: `root-docs-sanitized.json`; each file's SHA-256 is in the provenance table] Times are the file stamps, UTC.

| Run | Purpose | Outcome |
| --- | --- | --- |
| 11:48:36 | Receiver | No event recorded; the run failed with its details suppressed, as the driver does by design [observed: the coordinator's run record, the driver's source]; the reason is [unknown] |
| 11:57:26 | Receiver | Same: no event, byte for byte the same file as 11:48:36 |
| 12:04:22 | Receiver, first fresh pairing | Paired; 187 history batches over 15 intervals; the 5 pending sends failed at once, so nothing was measured with history pending |
| 12:18:33 | Error diagnostic, retained session | 1 send, failed with `NotConnected` [observed: `root-error-diagnostic.log`]; no history |
| 12:25:19 | Readiness, retained session | 1 pending send with no history running, not eligible; 5 quiet samples eligible, median 0.687 ms, maximum 10.645 ms [observed: `root-ready-analysis.json`] |
| 13:28:25 | On-demand history, retained session | 5 requests returned, none followed by history within 120 s; no peer response was instrumented |
| 14:28:39 | On-demand history with peer responses, retained session | 1 request with the chat as received, no history and no peer response within 120 s; the second form was skipped, the chat already being in it [observed: `root-ondemand2-analysis.json`] |
| 15:54:51 | Receiver, second fresh pairing | Completed: below |

[inferred] The 12:04:22 sends failed because the sender connected only after `Paired`.
That run did not record why: its driver records any exception as a failed send [observed: the driver's source].
The 12:18:33 run recorded `NotConnected` directly, and the 15:54:51 driver checks the sender's state before the QR and before each batch.
The retained session brought no history on any reconnect [observed: the 12:18:33 to 14:28:39 rows].
Why the on-demand requests got nothing is [unknown]; what was checked offline is in the 13:28:25 and 14:28:39 handoffs, not repeated here.

## The completed run

[observed: `root-freshpair-analysis.json`] Capture valid: no drop, no clock error, no unexpected exit or restart.
The sender was ready before the QR and before each batch; the paired device was verified as a new device of Alice's account before any probe; it was logged out afterwards, and the cleanup passed.

| Phase | Exchanges, each one send, receipt, handler and echo | Eligible | Median | Maximum |
| --- | ---: | ---: | ---: | ---: |
| Pending | 5 of 5 | 3 | 2.250 ms | 2.446 ms |
| Quiet | 5 of 5 | 5 | 1.250 ms | 10.616 ms |

The three eligible pending samples are 0.390, 2.250 and 2.446 ms; their handlers all entered before their interval ended.
The other two pending receipts, 0.392 and 0.866 ms, came while no interval was open, so they are kept and not compared.
The quiet samples are 1.010, 1.059, 1.250, 1.650 and 10.616 ms.
The pending median is above the quiet one, and the quiet maximum above every pending sample.
[inferred] With three and five samples this supports one thing: handlers for live messages ran while history was being delivered, within milliseconds of receipt, instead of waiting for the drain to end.
It does not establish a cause, a threshold, or behaviour under other volumes, devices or load.

## History delivered

[observed: `root-docs-sanitized.json`] Entries are delivered entries, not unique messages; the timestamps are those of the messages, not of their arrival.

| Pairing | Batches | Entries: `InitialBootstrap` / `Full` / `Recent` | Oldest message | Newest message |
| --- | ---: | --- | --- | --- |
| 12:04:22 | 187 | 998 / 104 / 45,330 | 2024-07-01 20:44:17 | 2026-11-06 12:05:31 |
| 15:54:51 | 207 | 998 / 119 / 50,414 | 2024-07-01 20:44:17 | 2026-11-06 15:57:58 |

Both pairings of the same account requested a full sync limited to seven days, and both delivered messages from 2024-07-01.
The capture keeps no per-type timestamps and no batch arrival times, so what the seven-day limit changed, and whether any option limited this delivery, is [unknown].
These counts sit beside the 2026-11-05 defaults at other times and account states; they are not a controlled comparison.
`FullSyncSizeLimitMegabytes` and `StorageQuotaMegabytes` were not varied: [unknown].
No sidecar memory was sampled on 2026-11-06; the [2026-11-05 samples](0042-hosted-sample-2026-11-05.md#sidecar-resident-memory) stand.

## Against #42's acceptance criteria

* The sample and its fake-mode check: ADR 0049.
* Telemetry: seen in the 2026-11-05 record, under the names `whatsmeow.process`, which covers the event's handlers, with a `whatsmeow.send` child, and four metrics.
  The criterion names receive, handle and send spans; the API has no separately named receive or handle spans [observed: `Tracing.cs`], so whether these names meet it is open for the owner.
* Two sessions echoing on their own, and a disconnect of one not stopping the other: the 2026-11-05 record; while Alice was locally disconnected, Bob's send got `Delivered` and Bob stayed connected or caught up [observed: that record].
  A phone-side logout of one session while the other keeps echoing was not run [unknown].
  The accounts were the owner's personal and Business ones, not dedicated test numbers.
* Receipt to handler with and without history pending: this record.
* Memory with two sessions: the 2026-11-05 samples, without a capacity claim.
  What the history options limited: the delivered volumes above, the limiting effect [unknown].
* Every Phase 3 issue closed: #42 is the milestone's only open issue [observed: milestones API, 2026-11-06], so this holds once #42 closes.

## Evidence provenance

The raw records stay private: they contain account data and are not attached.
These SHA-256 hashes identify them [observed: `root-docs-sanitized.json`].

| Artifact | SHA-256 |
| --- | --- |
| `live-records-20261106T114836369401Z.jsonl` | `373147508e158041d8419c4f1330cc29e11580dfa2407c662310409b70f81df8` |
| `live-records-20261106T115726992070Z.jsonl` | `373147508e158041d8419c4f1330cc29e11580dfa2407c662310409b70f81df8` |
| `live-records-20261106T120422876796Z.jsonl` | `2b7b73f08a7a92e5a2ee2311d2310b1157ab3b27d1fa6c2932c7e0b30e1d70e6` |
| `live-records-20261106T121833999343Z.jsonl` | `f768a963b8a0b88c2bc1da21cebcbc6da7075f9027e3ec0908273a7460c5b7df` |
| `live-records-20261106T122519015406Z.jsonl` | `17630d65184f4d9f149b452f7b05095869cd7db77063b7a7d3d709a9c75b367a` |
| `live-records-ondemand-20261106T132825931298Z.jsonl` | `07c2f1f5714e30205e5d6fd2d22b1232be0298b3892cf514fe75b30984704815` |
| `live-records-ondemand2-20261106T142839510635Z.jsonl` | `3c90d63e29c260345903f5272179a1de1ac59e722d568d5ea4a8da53353209d5` |
| `live-records-freshpair-20261106T155451379909Z.jsonl` | `131aececa947f0d2dc12e325ed7735db2c11d0ee745d92ac9c9e83205587c0f7` |
