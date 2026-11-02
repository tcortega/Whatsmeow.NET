# 0035. History sync that never delays live traffic

- Status: Accepted
- Date: 2026-11-02
- Evidence: issue #37, its plan review, ARCHITECTURE §14 "Measured for History sync (#37)", and the tests named below
- Supersedes: the wording of invariants 1 and 2

## Context

Issue #37 adds the History sync slice: the account's history, which WhatsApp sends after pairing in blobs, and more on request, delivered without ever delaying live traffic (ARCHITECTURE §15, Phase 3 exit).
whatsmeow runs with `ManualHistorySyncDownload` (§7.2), so it deletes nothing itself, and the sidecar owns the download, the parsing and the deletion.
Reading whatsmeow at the pin and mautrix-whatsapp `5dd621f` showed facts the design had to follow [observed: ARCHITECTURE §14 "Measured for History sync (#37)"]:

- A notification arrives as a decrypted `*events.Message` from the account's own phone, on the node queue, and is gated: a handler that fails withholds its delivery receipt and keeps its plaintext in whatsmeow's buffer, which dispatches it again later (message.go:459-497, 545-552, 884-907).
- The `hist_sync` receipt is sent on a goroutine before any handler runs, whatever the handlers return, and again on a redispatch (message.go:899-906, 1172-1178).
- `DownloadHistorySync` holds a whole blob in memory, inline or downloaded, decompressed and parsed (message.go:783-825).
- `DeleteMedia` deletes a blob with a DELETE whose URL query carries the media connection's auth; an inline blob has no path and nothing to delete (upload.go:305-359).
- `OfflineSyncCompleted` is dispatched on the node queue after what the session missed offline, once for each offline `ib` (connectionevents.go:72-90), while `Connected` is dispatched on a goroutine of its own after two IQs (connectionevents.go:183-205), so the offline sync can come first [inferred: from the source].
- whatsmeow's media client has no timeout of its own, and a notification of type `NO_HISTORY` or `MESSAGE_ACCESS_STATUS` gets no special reading: without a path or an inline payload it fails `Download` with `ErrNoURLPresent` (download.go:222-223).
- `store.DeviceProps`, which holds the history options, is process-wide and sent only in the registration payload (store/clientpayload.go:124-183).
- On main before this slice, the Events slice recorded every notification as a live `MessageReceived`, its inline payload included, which has no bound below a gRPC message's 16 MiB.
- The outbox is one seq space per session, and the .NET reader reads it in order through a window of 64 events (ADR 0022): without a bound, a backlog of history recorded ahead of a live message would hold that message out of the dispatcher.

## Decision

- **The queue.** Schema step 5, `history_blob`: one row per notification by its message id, with how many of its messages, in the blob's order, are recorded or skipped, whether its progress event is, and its failed attempts; a session's rows go with it.
  `LoggedOut` and `PairSuccess` delete the session's rows: a re-paired session is another account, whose blobs come anew.
  The slice's whatsmeow handler, attached after the Chat state slice's, writes a notification from the account's own phone there in one short transaction (`ON CONFLICT DO NOTHING`, so a redispatch is a no-op), wakes the session's worker without waiting, and returns true only after the commit.
  The Events slice never records a notification.
- **A worker per session, after the offline sync.** `OfflineSyncCompleted` starts the session's worker, once its previous one ended; nothing else does, so a notification that arrives before the session caught up waits in the queue, and one queued before a restart waits for the next offline sync.
  A second `OfflineSyncCompleted` while the worker of this connection runs, or still waits, only wakes it.
  A worker started before the session is online waits on `session.Up`, a channel the Sessions slice closes once the session is `Connected` or `CaughtUp`, for at most 3 minutes, never polling; then it ends with a debug line, and the next offline sync starts another.
  It runs while the session is online (`session.Online`'s channel) and ends at `Close`.
  A download that fails while the session leaves online, or after whatsmeow's socket is gone (`IsConnected`), is offline: not counted, and the worker waits for the next wake or the end of the connection.
  Any other failure, a store error, is tried again in the loop after a backoff from 1 second doubling to 1 minute, on the slice's clock; a queued notification that does not parse is reported `INVALID` and leaves the queue, and so does a blob whose delivery panics, logged with the panic's type and stack, never its value, which may carry a JID.
  It takes the blobs in the order they arrived; one download, parse and cut into batches runs at a time across the sessions, because a blob is held whole in memory, and each download is bounded by 5 minutes, a timeout counted as a failed attempt [inferred: five minutes is far above a blob at the default limits on a slow link; unmeasured].
- **Live first: pacing by acks, and a lane of its own.** The worker records the next history event of a session only while fewer than 4 of its history events are kept in the outbox, that is not acked by every consumer of the session (`outbox.Pending` over the history key prefix, woken by `outbox.Acked`).
  So a live message is recorded behind at most 4 history events, and the .NET client runs every history event in one lane of its own, in order, one at a time, never in a chat's lane.
  A live message in any chat, the history's own included, reaches its handler while a history handler still runs.
  A session with no consumer, or one that stopped acking, keeps 4 history events and no more: the outbox grows by the window, not by the history [observed: `TestTheWorkerKeepsAtMostAWindowOfHistoryUntilTheConsumersAck`, 4 rows and 95,094 payload bytes after a blob of 2,560 messages], so no consumer expiry is needed for history.
  A separate stream with its own cursor was rejected: two new RPCs and a second acking dispatcher, and an abandoned consumer would keep the whole history.
- **Batches.** A blob's messages are parsed with `ParseWebMessage` and mapped with the Events slice's `events.MessageEvent`, so a history message carries what a live one does, `view_once` included, then cut in blob order into batches of at most 256 messages and 1 MiB as an event, a larger message alone; one that alone exceeds 15 MiB is skipped and counted.
  Status updates are not delivered: a blob's `statusV3Messages` count in `skipped_count`, and statuses are a non-goal (ARCHITECTURE §1).
  A `NO_HISTORY` or `MESSAGE_ACCESS_STATUS` notification without a blob is reported by a progress event of its type with no messages and no failure, not `INVALID`, and leaves the queue with nothing to download or delete.
- **Keys and crash safety.** A batch is keyed `h`, the notification's id and the blob index of its first message; the blob's progress event `h`, the id and `p`.
  Each batch is recorded in the transaction that moves `messages_done` to the index after its last message; the progress event in the one that sets `recorded`.
  A sidecar that stops with a blob half done downloads it again on the next offline sync and records from its next message, so no message is lost and none is recorded twice, also when the next sidecar's mapping cuts the batches elsewhere: a batch's size depends on its mapped messages, and resuming by a batch's number would skip or repeat messages; a batch delivered again by the outbox carries the same messages.
  The progress event's counts are the whole blob's, as the sidecar that finishes it maps it.
- **Deleted only once durable.** The blob is deleted from WhatsApp's server only after its last batch and its progress event committed, then taken from the queue; a crash in between deletes it on the next run without a download.
  A failed delete is logged without its error, whose text carries the auth, and not tried again: the row is dropped [inferred: WhatsApp expires a blob it keeps; how soon is unmeasured].
- **Failures.** 403, 404 and 410 are `EXPIRED`; a missing URL, a failed check, a blob that does not decompress or parse are `INVALID`; each is reported in the blob's progress event, in the transaction that takes it from the queue, and the next blob goes on.
  Any other failure while online is counted and tried again a minute later, three times in all, then reported `UNAVAILABLE` [inferred: mautrix-whatsapp waits a minute after a failed blob; how long WhatsApp keeps one is unmeasured].
  An offline failure is not counted.
  `SendHistorySyncServerErrorReceipt`, which asks the phone to upload a blob again, is not used.
- **Older history.** `history.proto` `HistoryService.RequestHistory`: whatsmeow's `BuildHistorySyncRequest`, sent as a peer message to the account with an id of whatsmeow's form, as mautrix-whatsapp sends it.
  Its errors are a send's (ADR 0025, `messaging/send.go` `classify`): 429 is `RATE_LIMITED`, WhatsApp's other answers to an IQ or the node are `WHATSAPP_ERROR` with the code, and only a request that may have gone out with no answer is `OUTCOME_UNKNOWN`; none is retried, and asking again only repeats messages.
  An `INVALID_ARGUMENT` names the field, never the chat's JID.
  The answer arrives as history events of type `ON_DEMAND`.
- **History options.** `WhatsmeowSidecarOptions.History`: full sync, the full sync's days and size limits, and the storage quota, each set on its own, passed as `--history-*` flags and set on `store.DeviceProps` once at startup.
  They are process-wide and apply to every session paired while the sidecar runs with them, never to one paired before; what WhatsApp makes of each is [unknown] (RESEARCH item 16), and #42's live run records it.
- **The `hist_sync` receipt** keeps whatsmeow's default: sent before the handlers, so the phone hears of a notification whatever the sidecar does with it.
  The notification itself is gated on its delivery receipt, which goes out only once it is queued.
  What the phone does when a notification is never receipted, or receipted twice, is [unknown].
- **.NET.** `HistoryReceived`, a batch of `HistoryMessage` records with element-wise equality, and `HistorySyncProgressed`, in `Whatsmeow.History`; `HistoryMessage`'s content is mapped by `ContentMapping.ToContent` with the message's `view_once`, the live messages' mapping (ADR 0033), so a media message arrives with its `MediaRef` as a live one does.
  `IWhatsAppClient.RequestHistoryAsync(oldest, source, oldestTimestamp, count = 50)`, the shape of `RequestMediaRetryAsync` and `StarMessageAsync`, so a `HistoryMessage`'s or a `MessageReceived`'s fields pass as they are.
  A history message without a usable id, chat or sender is skipped and logged, and the rest of its batch arrives.
- **Fake controls.** `CompleteOfflineSync`, `DeliverHistorySync` (the blob in a file of the fake's media directory, so it survives a kill, inline, or none) and `GetPairingDeviceProps`; the fake client's `DownloadHistorySync`, `DeleteMedia` (`Sent.media_delete`), and whatsmeow's own `ParseWebMessage` and `BuildHistorySyncRequest`.

## Invariants 1 and 2

ARCHITECTURE §13 says an invariant changes only with an ADR first; this section is that ADR's reason for each.

- **Invariant 1** said the handler only writes to the outbox or the ring, deletes a settled media retry request, and wakes the app state recovery.
  The History slice's handler writes a notification to `history_blob`, wakes or starts the session's worker, and deletes the session's rows at a logout or a pairing.
  It is the same kind of work as an outbox write: one short local SQLite transaction, bounded by `busy_timeout`, with no wait on .NET, the network or media; the wake is a non-blocking send on a channel of one, and the start a goroutine.
  The alternative, recording the notification as an outbox event and the queue row in its transaction, would put a payload with no size bound on the stream, which is what this slice takes off it.
  The invariant now names the history queue and the wake; its meaning, that the handler never blocks on .NET, the network or media, is unchanged.
- **Invariant 2** said a gated stanza is answered only after it is durably in the outbox.
  A history notification is a gated stanza, a decrypted message, and is answered with its delivery receipt only after it is durably in `history_blob`, a table of the same database, written in a committed transaction before the handler returns.
  Its messages reach the outbox later, and the blob is deleted from WhatsApp's server only after they are durable there.
  Its `hist_sync` receipt is the one answer whatsmeow sends regardless, on a goroutine before any handler runs (message.go:899-906), so the invariant names it among the answers it never claims.
  The invariant now says "in the outbox, or for a history sync notification in the history queue"; its meaning, that nothing of a gated kind is answered before it is durable, is unchanged.

## Consequences

- History waits while a session's consumers do not ack: with no consumer it never drains, and its blobs stay on WhatsApp's server, where they may expire and then be reported `EXPIRED`.
- The drain rate is bounded by the window and the .NET ack interval of a second: at most 4 batches of 256 messages a second per session, about 1,000 messages [inferred: from the constants]. Locally the 13 History integration tests, the two-session backlog of 1,536 messages each and the 20 MiB blob among them, ran in 13.3 s [observed: osx-arm64, one run].
- Memory: one blob in flight across the sessions, compressed, decompressed, parsed and mapped, plus, for each online session whose consumers have not taken a blob's batches yet, that blob's batches still to record, at most one blob's mapped messages per session [inferred: from the code]; a blob's size is [unknown].
- A batch that exceeded a gRPC message could not exist, and a message above 15 MiB is lost, counted in `SkippedCount`.
- Tests: `internal/history` (whatsmeow-free, a stub with a real whatsmeow client that never connects, on a real store: the queue, the offline-sync gate, batches and progress, the window and the outbox growth, a restart mid-blob, a crash before the delete, each failure, retries, offline, one download at a time, the batch bounds, skipped messages and no JID in the log, RequestHistory and its errors, a second offline sync, an offline sync before `Connected` and the give-up, the backoff, a queued notification that does not parse, a stalled download, a restart under another mapping, the queue cleared at a logout or a pairing, one parse at a time, a failure as the client goes offline, a panic, statuses, and a notification without a blob), `internal/events/history_test.go`, `internal/outbox/pending_test.go`, `internal/store/history_blob_test.go`, `cmd/whatsmeow-net/history_test.go`, `internal/fakewhatsapp/history_test.go`, and `Whatsmeow.IntegrationTests/History/` (the FullCycle, the offline-sync gate, the backlog with two sessions, an expired blob, a notification without a blob, on-demand history, a 20 MiB blob, the options, and the kill mid-blob).
