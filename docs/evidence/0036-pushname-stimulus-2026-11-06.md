# Push name stimulus on the persistent sessions, 2026-11-06

Evidence for [#36](https://github.com/tcortega/Whatsmeow.NET/issues/36) AC6, the Contacts FullCycle's push name branch: one run of ADR 0061's stimulus ladder on the persistent live and peer sessions.
It follows the fresh companion run of 20:55 UTC the same day ([its history record](0042-history-bounds-2026-11-06.md)), whose companion-sent text arrived with no push name (ARCHITECTURE §14, Contacts).
It records one sequence; it does not show what caused a text to carry a push name, nor what a fresh device gets.
What a fresh device got after a named presence was measured later, in the [one-day record of 2026-11-07](0042-history-days-limit-2026-11-07.md#contacts-branches-36).

## Scope and method

[observed: `result.json`] Root ran one method, `LiveContactsTests.Live_ThePeer_GetsTheBusinessPushNameAsTheFakeModeCheck_WhenItsFirstSightIsUnspent`, built from main `03f26c81e126e631c5a688cd9b67fded223cc36b`; the test assembly's SHA-256 is `fd5095f49c1775496b3b68559af228cd0ba17705f577494b07f6f65971988cab`.
[observed: `result.json` `sidecar_binary_note`] The sidecar binary's hash is [unknown]: the test fixture built and removed it before a hash was taken.
[observed: ADR 0061, `Support/PushNameStimulus.cs`] The sender is the live session, a linked companion of the business account; the receiver is the peer session; the ladder stops at the first text that carries a push name.
[observed: `live-summary.log`] Every line names forms and booleans only, never a JID, name or text.
[observed: `Support/PushNameStimulus.cs`, whatsmeow presence.go:64-87] "Presence sent" means the `SendPresence` call returned; whatsmeow waits for no answer to a presence, and names it with the stored push name on the normal WhatsApp path this sidecar uses (only a `MessengerConfig`, which the sidecar never sets, leaves the name out).
[observed: `Support/PushNameStimulus.cs`] "Carried named" means the received text's `PushName` was neither null nor `"-"`; the stanza was not captured.

## The run

All times UTC, truncated to the millisecond, from `live-summary.log` [observed].

| Time | Line |
| --- | --- |
| 23:39:30.548 | The live session reached CaughtUp 1.7 s after it was usable. |
| 23:39:55.531 | The peer reached CaughtUp 23.3 s after it was usable. |
| 23:39:55.558 | Before the first text, the peer's rows of the business read LID saved False, push True, business True, and phone number saved True, push True, business True, and the store paired each form with the other. |
| 23:39:55.570 | Just before text 1, the rows read the same booleans. |
| 23:39:56.106 | Text 1, the ladder's no presence step, carried none, from a LID with a phone number beside it. |
| 23:39:56.124 | Just before text 2, the rows read the same booleans. |
| 23:39:56.395 | Text 2, after a named unavailable presence was sent, carried named, from a LID with a phone number beside it. |
| 23:39:56.397 | The sender's LID row had a push name both before the first text and just before the named text. |
| 23:39:56.416 | After the texts, the rows read the same booleans. |

[observed: source] That the live session's connection was new and had sent no presence before text 1 holds by construction, not by the log: the test stops the live sidecar and starts it again before the ladder (`LiveContactsTests.cs:134-137`), the sidecar calls `SendPresence` only for the `SendPresence` RPC (`internal/presence/presence.go:64`), and whatsmeow never calls it itself (its other mentions are doc comments, presence.go:96, receipt.go:251).
The log's line at 23:39:30.548 saying so is that statement written by the test, not a measurement of absence.

[observed: `result.json`] The ladder sent 2 texts and stopped; the third step, a named available presence, was not run.
[observed: `result.json`] 1 test, 0 passed, 1 failed, 0 skipped, 0 errors, exit code 1, 31.679 s; the failure was `pushname_precondition_spent`.
That is the test's designed verdict when the receiver's row already holds a push name: the texts are stimulus evidence, never a parity pass (ADR 0061).
No push name change was checked, so #36's push name branch is still unfulfilled live.

[observed: `postflight-checks.json`] Root's offline postflight read every check true: the live, peer and retained stores' identities and devices unchanged, no stopping session in the live or peer store, the retained database byte for byte identical, no QR code created, and the driver exited.

## What it shows

* [observed: the log; the connection's construction from source, above] On a new connection, a text sent before any presence carried no push name, and the next text, sent after a named unavailable presence, carried one.
* [inferred] This fits the hypothesis that a named presence lets WhatsApp attach `notify` to the sender's texts (whatsmeow presence.go:60-87, message.go:235).
  It does not prove it: the order was fixed, so the presence cannot be told apart from the time since connecting or a second text.
* [unknown] Whether a fresh companion's text after a named presence carries a push name, and what a text from the phone carries.
* [unknown] Whether WhatsApp attaches `notify` differently for the peer's long-linked device than for a newly linked one (ADR 0061).
* This is not a Contacts FullCycle pass: About and the saved name stay unrun (ADR 0060), and the business name stays with #153.

## Files

The sanitized files are retained privately in `/tmp/whatsmeow-issues/36-pushname-20261106` and, identical, in `~/Library/Application Support/Whatsmeow.NET/evidence/36-pushname-20261106`.
The private originals (the xUnit results and complete logs) are root's and are not part of this record.

| File | SHA-256 |
| --- | --- |
| `live-summary.log` | `3f455d33de80abdb41cff0a0adf0ce27483be63ba27e8285336d1481300d9ab2` |
| `result.json` | `5263db1db04db6ec0518ac5e2249bc68364d5d9de27d0392ca779e55ee03e2c5` |
| `postflight-checks.json` | `cbf3feef71cd0929d9fff0ebb4519b537f0417ecf2874cc926be5a24295f162f` |
| `manifest.json` | `a4a38726c518fd002dc0d043975f2985990c14d40eba082fd09ce6b3a5f9d4ce` |

`manifest.json` lists the other three files' hashes, which match the table.
