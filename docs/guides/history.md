# History

After pairing, the phone sends the account's message history to the new device, in blobs the sidecar downloads.
It arrives apart from live messages, so your bot never answers an old message as if it were new ([ADR 0035](../adr/0035-history-sync-that-never-delays-live-traffic.md)).

## What you get

* `HistoryReceived`: a batch of `HistoryMessage`s, at most 256 and 1 MiB per batch, a larger message alone, with the batch's `SyncType`.
* `HistorySyncProgressed`: one per blob once its batches are delivered, with its counts, and `Failure` when the blob could not be downloaded or read.

A history message is not a `MessageReceived`, so live handlers never see it.
Poll votes in a blob follow their poll in the same batch; status updates are never delivered and are counted as skipped (§7.7).

History is at least once like live messages: a blob half delivered when the sidecar stops is downloaded again and goes on from its next message.
Two kinds of message never arrive: one over 15 MiB, and one that does not parse, a parse that panics included.
Each counts in its blob's `HistorySyncProgressed.SkippedCount`, with the status updates (§7.7).
Dedupe on `SessionId` plus `MessageId`, as for live messages.

## Live traffic first

History runs in a lane of its own, never in a chat's, so a live message never waits behind it.
The sidecar keeps at most 4 history events per session that your handlers have not finished: a slow history handler slows the history, not the live messages (§7.7).
Downloads run one at a time across all sessions, each bounded by 5 minutes.
In one live measurement on a freshly paired session, three live messages reached their handler within 2.5 ms of the sidecar's event callback while its history was being delivered [observed: the [2026-11-06 record](../evidence/0042-history-timing-2026-11-06.md#the-completed-run)]; it sets no threshold for other volumes or machines.

A session's history starts after its offline messages, once WhatsApp says the offline sync completed.
That WhatsApp sends that after every login is [inferred] (§14 History sync).
How large a blob can be is [unknown]; the sidecar holds one in memory at a time while it reads it (§7.7).

## How much the phone sends

`WhatsmeowSidecarOptions.History` is a `HistorySyncOptions`: `RequestFullSync`, `FullSyncDaysLimit`, `FullSyncSizeLimitMegabytes` and `StorageQuotaMegabytes`.
WhatsApp only reads them when a device pairs, so they apply to every session paired while the sidecar runs with them, and never to one paired before.
The [2026-11-05 live run](../evidence/0042-hosted-sample-2026-11-05.md#default-history) records default history delivery [observed].
Two pairings with `RequestFullSync` and a `FullSyncDaysLimit` of 7 still delivered messages more than two years old [observed: the [2026-11-06 record](../evidence/0042-history-timing-2026-11-06.md#history-delivered)]; what each option limits remains [unknown] (§14, #42).

## More history on demand

`RequestHistoryAsync(oldest, source, oldestTimestamp, count)` asks the phone for `count` messages before the oldest one you have in a chat; 50 is whatsmeow's recommendation.
The answer comes later as history events of type `OnDemand`, if the phone answers.
It is a send to the phone, so it fails like one: `RateLimited`, `WhatsAppError`, or `OutcomeUnknown` when it may have gone out with no answer (§7.7).
Asking again only repeats messages, which their ids dedupe.

## Failures

A blob that can no longer be downloaded (expired, gone, or corrupt) is reported in its `HistorySyncProgressed`, and the rest goes on.
Other failures are tried three times, a minute apart, then reported `Unavailable` (§7.7).
