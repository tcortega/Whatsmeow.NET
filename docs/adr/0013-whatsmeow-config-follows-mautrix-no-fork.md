# 0013. whatsmeow configuration follows the maintainer's production bridge; gaps are fixed upstream, never in a fork

- Status: Accepted
- Date: 2026-10-31
- Evidence: [ARCHITECTURE §14](../ARCHITECTURE.md#spike-results-2026-10-31) questions 8 and 4, "Other whatsmeow claims"
- Amended by: [ADR 0064](0064-the-verified-name-through-whatsmeows-usync-until-upstream-fixes-it.md) (#153's gap is bridged in the sidecar until the pin carries its upstream fix)

## Context

The whatsmeow pin is `56e5ad0581cee858000f856b1a98dbd03cc5b14f`, the merge commit of tulir/whatsmeow#1312 (2026-10-30 19:55:14 UTC) [observed: GitHub API, `repos/tulir/whatsmeow/pulls/1312` `merge_commit_sha`, 2026-10-31].
Every "at the pin" below means that commit.

Many ack kinds are sent by whatsmeow regardless of our handler, so the delivery guarantee covers only the gated kinds ([ADR 0003](0003-at-least-once-delivery-synchronousack-sqlite-outbox.md), Question 4).
Question 8 asked whether to extend it to the ungated kinds through an upstream PR or a fork.

Question 8 [observed: §14 Spike results, Question 8]:

- whatsmeow's maintainer runs it in production in mautrix-whatsapp.
- mautrix-whatsapp `5dd621f` (2026-10-30) is pinned to whatsmeow `20261026125351-1fbae988179a`; that is mautrix's own pin, not ours.
- At `5dd621f` it sets `AddEventHandlerWithSuccessStatus`, `SynchronousAck = true`, `EnableDecryptedEventBuffer` (on when `PortalEventBuffer == 0`, which we take to be the default [inferred]), `ManualHistorySyncDownload = true`, `AutomaticMessageRerequestFromPhone = true`, and `InitialAutoReconnect` (default `true`) (`pkg/connector/client.go:81-93`, `example-config.yaml:66`).
- Its handler returns real success for messages, receipts, undecryptable messages, calls, media retries and group changes, and lets whatsmeow decide which acks that gates (`pkg/connector/handlewhatsapp.go:78-132`).
- The maintainer recommends `SynchronousAck` plus `EnableDecryptedEventBuffer` against losing a decrypted-but-undispatched message (tulir/whatsmeow#1093), and notes that hitting that is "very rare unless the program is running on a mobile phone".
- Gaps get fixed upstream: tulir/whatsmeow#1312 made `server_sync` fetch app state before acking under `SynchronousAck`, and it is our pin.

All six `Client` fields in [ARCHITECTURE §7.2](../ARCHITECTURE.md#72-whatsmeow-configuration-mandatory) exist at the pin, re-checked by hand; `store.DeviceProps`, `store.SetOSInfo` and `store.SetWAVersion` are package-level [observed: §14 Spike results, "Other whatsmeow claims"].
"The zombie-socket bug" is not in the code; the mechanism is the keepalive loop, which forces a reconnect only when auto reconnect is on (`keepalive.go:46`) [observed: same, starred].

## Decision

Phase 0 verdict:

- Question 8: go: no fork, because the maintainer runs this configuration in production and fixes real gaps upstream, as #1312 did.

Design:

- whatsmeow is configured as in [ARCHITECTURE §7.2](../ARCHITECTURE.md#72-whatsmeow-configuration-mandatory), following mautrix-whatsapp: `SynchronousAck`, `EnableDecryptedEventBuffer`, `AddEventHandlerWithSuccessStatus`, `ManualHistorySyncDownload`, `AutomaticMessageRerequestFromPhone`, `InitialAutoReconnect`.
- Where we differ, §7.2 says why: `UseRetryMessageStore = true`, because mautrix defaults it to `false` and we choose retry receipts that survive a restart.
- `EnableAutoReconnect = true`, with an `AutoReconnectHook` that caps and jitters the backoff.
- `store.DeviceProps`, `SetOSInfo` and `SetWAVersion` are set once at startup, never per session.
- `AutomaticMessageRerequestFromPhone` recovers an undecryptable message as a gated `events.Message`, with the same message ID [inferred: retry resends the original message; not traced].
- No fork of whatsmeow.
- When a test proves a gap, we report it upstream and fix it there.

## Consequences

- The ungated kinds stay best effort, and the docs say so ([ARCHITECTURE §1](../ARCHITECTURE.md#1-goals-and-non-goals), invariant 2).
- The weekly whatsmeow bump stays possible, because there is no fork to rebase.
- §14's whatsmeow facts are true at the pin only and are re-checked on every bump, never from memory.
- mautrix-whatsapp is AGPL [observed: [RESEARCH.md](../RESEARCH.md#six-earlier-wrappers-already-made-the-mistakes-worth-avoiding) "Six earlier wrappers"], so we take its configuration as reference and copy no code.
