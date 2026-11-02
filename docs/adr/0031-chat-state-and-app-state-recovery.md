# 0031.
Chat state and app state recovery

- Status: Accepted
- Date: 2026-11-02
- Evidence: issue #38, its plan review, ARCHITECTURE §14 "Measured for Chat state (#38)", and the tests named below
- Supersedes: the wording of invariant 1; "`client.Chats`" in ARCHITECTURE §3

## Context

Issue #38 adds the Chat state slice: muting, pinning, archiving and marking chats read, starring messages, and the chat state events of the app state sync.
These live in WhatsApp's app state, whose copy on a device can get out of step with WhatsApp's (a mismatching LTHash), after which every write to that collection fails (RESEARCH "Six earlier wrappers", wuzapi).
Reading whatsmeow at the pin, mautrix-whatsapp `5dd621f` and wuzapi's `appstate_recovery.go` showed facts the design had to follow [observed: ARCHITECTURE §14 "Measured for Chat state (#38)"]:

- whatsmeow holds its app state lock (`appStateSyncLock`) for a whole fetch (appstate.go:46-47) and dispatches `AppStateSyncError` while it holds it (appstate.go:77, 167).
  A handler that fetched there would deadlock.
- A `server_sync` notification is fetched on the node queue under `SynchronousAck` (notification.go:481-483, 73-89).
  The lock is taken there too.
- A write rejected with a 409 has whatsmeow apply the conflicting patches and retry once itself (appstate.go:607-623).
  When they do not verify against the copy, it fails with both `ErrAppStateUpdate` and the MAC sentinel, and dispatches `AppStateSyncError` on the caller's goroutine, without the lock (appstate.go:613, 167).
  A write taken by WhatsApp whose sync after it fails carries only the sentinel (appstate.go:627-629).
- A full sync deletes the version first and rebuilds the hash from the snapshot (appstate.go:48-52, appstate/decode.go:258-311); a snapshot that does not verify fails it too (appstate/decode.go:287-294).
- `EmitAppStateEventsOnFullSync` is false by default and mautrix does not set it; without it a full sync dispatches no chat state event (appstate.go:70-72, 114-116), so `FromFullSync` is never true.
- App state is acked whatever the handlers return: `FetchAppState` ignores the dispatch results (appstate.go:36-38), and the echo of a write comes on a goroutine (appstate.go:631-635).
- A write needs the latest app state key, which the phone shares once after pairing (message.go:827-861); without one whatsmeow fails with an error that has no sentinel (appstate.go:557-562).
  The key is one indexed row read (store/sqlstore/store.go:437, 466-473).
- mautrix-whatsapp and wuzapi both recover a collection when a sync of it fails, from a goroutine, with a guard against loops (mautrix: one full sync until a success, handlewhatsapp.go; wuzapi: an hour).

## Decision

- **Contract.** `chatstate.proto`: `ChatStateService.SetChatState`, one RPC with a `oneof` of mute, pin, archive, read and star, and `ChatStateEvent` as a new `StreamEventsResponse` case.
  The patches are whatsmeow's `appstate.Build*`.
  A mute end at or before the Unix epoch is `INVALID_ARGUMENT`: whatsmeow reads 0 or less as forever (appstate.go:271-275), so it would mute for good.
  A mute end is an absolute time in milliseconds, as whatsmeow writes and reads it (appstate/encode.go:52-69, appstate.go:269-276) [inferred: what WhatsApp's other clients write is unmeasured; mautrix reads seconds; the live check is #10's].
  A star names its sender only for a group message from someone else, "0" otherwise [inferred: whatsmeow writes "0" for the chat's own user, appstate/encode.go:246-253, and its `Star` event has a sender only in groups, types/events/appstate.go:55].
  Archive and mark read send no message range, so whatsmeow sends now as the last message's time; whether the phone honours that is [unknown], the live check is #10's.
- **Online, with a key.** The RPC takes the client from `session.(*Service).Online` and type-asserts it to its own small `Client`, INTERNAL when it does not fit.
  A session whose device has no app state key yet is `NOT_CONNECTED`, with a doc line: it is not connected to its account's app state, and whatsmeow's own error would be INTERNAL.
- **Recovery of a write.** A write rejected with `ErrAppStateUpdate` and a MAC sentinel (`ErrMismatchingLTHash`, `ErrMismatchingPatchMAC`, `ErrMismatchingContentMAC`, `ErrMismatchingIndexMAC`) gets a full sync of its collection (`FetchAppState(ctx, name, true, false)`) on the call's goroutine, and is sent once more.
  WhatsApp rejected it, so its outcome is known, and invariant 10 does not apply.
  A second rejection is final.
  A write taken whose sync failed is done, on the first send as on the second; the collection is synced again all the same.
  A write taken whose sync failed for another reason (no answer, no connection) is `OUTCOME_UNKNOWN` or `NOT_CONNECTED`: whatsmeow wraps that sync's error without a sentinel that would tell it from the write's own (appstate.go:627-629).
  A missing key (`ErrKeyNotFound`) is not out of step: whatsmeow asks the phone itself (appstate.go:164-166).
- **Recovery of a failed sync.** The slice's own whatsmeow handler, attached by `cmd/whatsmeow-net` after the Events slice's, only signals on `AppStateSyncError`: it notes the collection and sends on the session's 1-buffered channel, dropping the send when one is pending, and returns true.
  One worker goroutine per session, started by the first signal or write and owned by the slice, takes `Online`'s client and channel, and fully syncs each noted collection.
  It ends when that channel closes (the session left Connected and CaughtUp, or was deleted) and at `Close`, which `cmd/whatsmeow-net` calls after the sessions closed.
  It takes the noted collections in order and syncs one at a time.
- **Every full sync on the slice's own context.** A full sync, the worker's or a write's, runs on the slice's context, not a caller's, bounded by 2 minutes and ended when the session leaves online (`Online`'s channel); a write's runs on a goroutine `Close` waits for.
  Callers only wait for it, each until its own call ends: a write that started one and gave up does not end it, or fail it, for the writes that joined it.
- **One full sync at a time per collection, and a cooldown after a failed one.** A write and a signal share the collection's state, and each knows how many full syncs had started when it was sent or noted.
  A running full sync is joined, and one that started since is the answer; otherwise a write starts one, cooldown or not, and a signal starts one unless the last full sync failed and started within the hour.
  So a collection a full sync cannot repair does not loop: each failed full sync dispatches another error.
  After a full sync that succeeded, the next failure is recovered at once.
  The hour is wuzapi's [inferred: no measurement says how soon a retry can succeed].
  The state goes with the worker, so a new connection gets a fresh attempt.
- **No phone recovery.** Asking the phone for a copy of the collection (`BuildAppStateRecoveryRequest`), which mautrix and wuzapi do after a failed full sync, is not done: it needs the phone online and answers later.
  A write to a collection a full sync cannot repair fails.
- **Full sync events.** `EmitAppStateEventsOnFullSync = true`, a §7.2 row of ours, not mautrix's: the issue needs the state after pairing, and after a recovery, to arrive as events marked `FromFullSync`.
  A full sync of the contacts collection then dispatches a `Contact` event per contact and stores each name in turn instead of in one batch (appstate.go:181-190, 292-297).
  The Contacts slice's mapper does not map `Contact` (ADR 0028), so nothing more is recorded and `ListContacts` reads the same names.
  How many events a full sync after pairing records is [unknown]; #10's live checklist has it.
- **Events through the outbox, unkeyed and best effort.** `Mute`, `Pin`, `Archive`, `MarkChatAsRead` and `Star` are recorded as `ChatStateEvent` by the slice's `events.Mapper`, with an empty key, as contact events are.
  A repeat (a write's echo, a full sync) carries the same value.
  A mute end of 0 or less is forever, as mautrix reads it; whatsmeow reads below 0 (appstate.go:271-275).
- **Errors.** `classify` keeps whatsmeow's sentinel or a bare copy, never its text, which carries WhatsApp's nodes (appstate.go:603-605).
  A rejected write that the recovery did not get past is `WHATSAPP_ERROR` with metadata `code` 409; another rejected update is `WHATSAPP_ERROR` without a code, which lives only in the node's text.
  On .NET both are `WhatsAppErrorException`, the 409 as its `Code` and the other with `Code` 0 ([ADR 0030](0030-groups-participants-settings-invites-and-group-events.md)).
  `ErrIQTimedOut` and a disconnect while waiting are `OUTCOME_UNKNOWN`, never retried; `ErrNotConnected` and a closed socket are `NOT_CONNECTED`.
  No new reason.
  The recovery's log lines carry the session and the collection, and the error's sentinel.
- **Invariant 1** says the handler also wakes the session's app state recovery without waiting.
  Its meaning is unchanged: the handler never blocks on .NET, the network or media.
- **.NET.** Flat members on `IWhatsAppClient` (ADR 0025): `MuteChatAsync(chat, until?)`, `UnmuteChatAsync`, `PinChatAsync`, `UnpinChatAsync`, `ArchiveChatAsync`, `UnarchiveChatAsync`, `MarkChatReadAsync`, `MarkChatUnreadAsync`, `StarMessageAsync(MessageId, MessageSource)` and `UnstarMessageAsync`, the pair `RequestMediaRetryAsync` takes, so an application can pass a `MessageReceived`'s `Id` and `Source`; `ChatStateChanged` with a `ChatStateChange` case per change in `Whatsmeow.ChatState`, its lane the chat's.

## Consequences

- **A recovery delays the node queue when a phone change lands during it.** The full sync holds whatsmeow's app state lock, and a `server_sync` notification on the node queue waits for that lock (notification.go:481-483, 79; appstate.go:46), so the messages queued behind it wait until the full sync ends, at most its bound.
  Every write already holds the same lock for its sync after the send (appstate.go:627).
  The handler never blocks; the delay is whatsmeow's, accepted.
- A collection that a full sync cannot repair stays broken for the application's writes, which fail with `WHATSAPP_ERROR` 409 after a full sync each, whatever the cooldown, and for the phone's changes, which arrive at most once an hour if a full sync then succeeds.
- Such a collection stays at version 0: a failed full sync deleted its version first (appstate.go:48-52), so every `server_sync` of it is a full snapshot fetch, on the node queue, under the app state lock (appstate.go:58-59; notification.go:481-483), until one succeeds.
- A collection whose incremental syncs keep failing after full syncs that succeed is fully synced again on each failure; the failures come from `server_sync` notifications, so their rate bounds it.
- A signal while the session is not online starts a worker that ends at once; its failed sync is recovered by the next signal or write.
- Tests: `internal/chatstate` (whatsmeow-free, over bufconn through the real `server.Server`, with a stub on a real store: each patch, validation, the key, the recovery of a write, the join and the cooldown, the worker's end, the reasons, no JID in a status or a log line; the mapper table), `internal/session` (`TestTheWhatsmeowClientEmitsAppStateEventsOnFullSync`), `internal/fakewhatsapp/chatstate_test.go`, and `ChatState/ChatStateTests.cs` on the shared fixture.
