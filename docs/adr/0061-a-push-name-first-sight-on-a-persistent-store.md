# 0061. A push name first sight on a persistent store, when its precondition read holds

* Status: Accepted
* Date: 2026-11-06
* Evidence: #36 AC6, the 2026-11-06 fresh companion run and PR #155 (ARCHITECTURE §14, Contacts), root's approval of the #36 push name diagnostic plan, whatsmeow at `307a2edf117d` and the sidecar (lines below)
* Amends: [ADR 0060](0060-a-live-test-control-and-a-fresh-companion.md) (its Context's "The persistent stores have both values cached", for the push name), docs/guidelines/tests.md (Live WhatsApp)

## Context

ADR 0060 checks the Contacts FullCycle's push name on a fresh companion because "the persistent stores have both values cached".
That sentence carries no evidence tag, and ADR 0060 cites no measurement of the peer store's rows of the business account [observed: ADR 0060 Context].
Whether an earlier run read those rows, and whether the peer's store has the business's push name, is [unknown] until measured.

* A message's push name is the stanza's `notify` (message.go:235), which the receiver's store does not change [observed: source].
* whatsmeow stores a push name and dispatches `PushName` (the client's `ContactUpdated`) only when it is not empty, not `"-"`, and differs from the sender's stored row; a missing row counts as changed (message.go:59-60, user.go:679-707, store/sqlstore/store.go:617-635) [observed: source].
  So the sender's row in the receiving store decides whether the change fires, not how long ago that device was linked [observed: source].
* On 2026-11-06 the live session's text reached the peer account's fresh companion with no push name [observed: ARCHITECTURE §14, Contacts].
  Whether a named presence before the send makes WhatsApp attach `notify` is [unknown]; `SendPresence` is the one stanza whatsmeow sends with the account's push name (presence.go:60-87) [observed: source].
  The 2026-11-05 run's presence test sent a named available presence from the live session (`LivePresenceTests.cs:47`, #36 comment of 2026-11-05), so a named presence a day and several connections earlier did not bring `notify` to that text [inferred].
  Each fresh rerun needs a QR scan.
* Whether WhatsApp attaches `notify` differently for a long-linked device than for a newly linked one is [unknown].
  Whether the server keeps a push name from an earlier connection is [unknown].
* `GetUserInfo` stores the business name even when its answer drops it (#153; user.go:252-270) [observed: source], so a profile read on the persistent peer would spend its store's first sight of the business name on a failing answer.

## Decision

* **One live diagnostic on the persistent sessions, on opt-in only.**
  `LiveContactsTests.Live_ThePeer_GetsTheBusinessPushNameAsTheFakeModeCheck_WhenItsFirstSightIsUnspent` runs with `NeedsPeer` and `NeedsPushNameFirstSight`, `WHATSMEOW_LIVE_PUSHNAME_FIRST_SIGHT`.
  Without the variable xUnit does not run it, and its first line fails it before anything starts when xUnit runs explicit tests anyway (`--explicit on`).
  It pairs nothing, logs nothing out, deletes nothing, changes no profile and never calls `GetUserInfo`.
  It reads the live account before connecting, which never creates a session, so a database without the live session fails without gaining one; the peer is read the same way.
* **Alone, with one method filter.**
  It must be the run's only test: another test's traffic to the peer (a presence, a group text) could store the business's push name with no ladder line to say so.
  It fails `pushname_not_alone` before anything starts when a test of the run already started the peer's sidecar.
  That guard sees only the peer's use in this process; the run alone is the operator's to keep, and root's launch always filters on this one method.
* **The ladder**, `IntegrationTests/Support/PushNameStimulus.cs`, which `PushNameStimulusTests` checks in fake mode.
  The live session, the business account, connects anew, so it has sent no presence on that connection: the sidecar sends one only for the `SendPresence` RPC (`internal/presence/presence.go:64`) [observed: source].
  It then sends the peer up to three texts: the first with no presence, the second after a named unavailable presence, the third after a named available one.
  It stops at the first text that carries a push name; none or `"-"` goes on; a presence refused `NO_PUSH_NAME` is recorded and stops it, with no text after it.
  Each text's line names its variant and what it carried, none, dash or named, never the name.
  The sender's rows are read before the first text and again just before each text, booleans only in the log.
* **The parity rule.**
  The peer store's first sight counts for #36 AC6's push name branch only when the fresh companion's precondition holds on the same reads, plus two more.
  The sender's row in the form the named text came from had no push name before the first text, nor just before the named text was sent.
  No push name change of the sender was delivered before the named text was sent.
  The change that came equals `ContactChecks.PushName` whole.
  A row that had one fails `pushname_precondition_spent`, and a change delivered first `pushname_origin_unclear`: the texts are then evidence of the stimulus, never a pass.
* **Attribution is not atomic.**
  A change stored after the read just before the named text and delivered after its send would be taken as the text's, since `ContactUpdated` carries no message id [inferred].
  The second read narrows that window to the read and the send; nothing in the test can close it.
* **No equivalence with a fresh device.**
  A pass shows the push name path on a store where whatsmeow's first sight rule held.
  It claims nothing about what a newly linked device gets, and it does not explain the 2026-11-06 fresh run's null.
* **The presence it leaves.**
  The live session ends unavailable, a deliberate postcondition, not a restore: the presence it had before is [unknown].
  The peer's presence is never touched.
* **Its timeout** is the arithmetic of its bounds, not a pad: the work (300 s), two connects each with its catch-up (2 x (120 s + 600 s), the live session's new connection, then the peer's first), the ladder (3 x (75 s for whatsmeow's send answer + 60 s for the arrival) + 60 s for the change) and two restores (2 x 30 s), 2,265 s.
* **Scope.**
  The push name branch only.
  The business name stays with the fresh companion and #153, the picture as ADR 0060, and About and the saved name stay unfulfilled, not waived.
  This does not close #36.
* No contract, public API, protocol or sidecar seam changes.

## Consequences

* A run with the opt-in spends the peer store's one first sight of the business's push name: a later one fails `pushname_precondition_spent`, by design.
  Up to three texts stay in the business and peer chat.
* A business text may carry its verified name, which whatsmeow then stores on a goroutine of its own (message.go:56-57) [observed: source]; whether WhatsApp sends one here is [unknown].
  The test logs the peer's rows of the business after the texts, booleans only, and that read may come before the goroutine's write [inferred], so a false there is not proof that no business name was stored.
  No business name event is watched for: this ADR uses no business name on the persistent store, so a late write changes no verdict.
* A named first text means the stimulus needed no presence on that connection; server retention and presence cannot be told apart then [inferred].
* A failed equality prints the push name in the local test output (`LiveChecks.Redacting` redacts JIDs, not names).
  That output stays private, and only the redacted `live:` lines are evidence.
* The fresh companion test is unchanged except that its two store pair assertions now redact their failure output, as the ladder's do.

## Addendum, 2026-11-06: the fresh companion sends a named presence before its text

* Evidence: [the 2026-11-06 stimulus record](../evidence/0036-pushname-stimulus-2026-11-06.md), root's approval of this follow-up, whatsmeow at `307a2edf117d`
* Amends: [ADR 0060](0060-a-live-test-control-and-a-fresh-companion.md) (the push name step), this ADR's last Consequence (the fresh companion test is no longer unchanged)

### Context

The ladder's one run, at 23:39 UTC on main `03f26c8`, sent two texts from the live session on a new connection: the first, with no presence before it, carried no push name, and the second, after a named unavailable presence, carried one [observed: the record].
The peer's rows already had the push name, so it failed `pushname_precondition_spent` and checked no change [observed: the record].
The fresh companion's text of 20:55 UTC carried none, with no `SendPresence` call in its log [observed: ARCHITECTURE §14, Contacts; inferred: no presence sent].
So a named presence is the one measured candidate for the fresh step's missing stimulus; that it causes `notify` is [inferred], and what a fresh device gets after one is [unknown].
At the pin, `SendPresence` always puts the stored push name in `name`, and the sidecar calls it only for the `SendPresence` RPC (presence.go:64-87, `internal/presence/presence.go:64`) [observed: source].
A received presence stores no push name (presence.go:18-58) [observed: source].

### Decision

* `LiveFreshCompanionTests`' push name step (branch 1) has the live session send a named unavailable presence immediately before its text, after the companion's rows and pairs are read and its arrival and change waits are armed, with the step's token, and logs only that it was sent.
* A refused presence fails the step with its exception, as any failed call there does; the step sends no text then.
* Nothing else changes: the precondition read, the no-push-name failure (`pushname_stimulus_missing`), the whole-record `ContactChecks.PushName` equality, and branch 2 before it.
  No cached name stands in for the text's, and About, the saved name and the business name stay as ADR 0060 and #153 leave them.
* Unavailable is the state chosen for the live session from then on, the one the other live tests leave it in, not a restore of a state the test read: its earlier presence is [unknown].
  It changes no profile.
* The ladder (`PushNameStimulus.cs`) and its fake-mode tests are unchanged.

### Consequences

* The next fresh run measures whether a fresh companion's text carries a push name after a named presence; until then it is [unknown], and #36 AC6 stays open for Contacts.
* A text that carries one fits the candidate without proving it: one run cannot tell the presence apart from anything else that differs from the 20:55 run [inferred].
