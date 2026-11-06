# 0061. A push name first sight on a persistent store, when its precondition read holds

* Status: Accepted
* Date: 2026-11-06
* Evidence: #36 AC6, the 2026-11-06 fresh companion run and PR #155 (ARCHITECTURE §14, Contacts), root's approval of the #36 push name diagnostic plan, whatsmeow at `307a2edf117d` and the sidecar (lines below)
* Amends: [ADR 0060](0060-a-live-test-control-and-a-fresh-companion.md) (its Context's "The persistent stores have both values cached", for the push name), docs/guidelines/tests.md (Live WhatsApp)

## Context

ADR 0060 checks the Contacts FullCycle's push name on a fresh companion because "the persistent stores have both values cached".
That sentence carries no evidence tag, and no run read the peer store's rows of the business account before ADR 0060 [observed: ADR 0060 Context].
Whether the peer's store has the business's push name is [unknown] until it is read.

* A message's push name is the stanza's `notify` (message.go:235), which the receiver's store does not change [observed: source].
* whatsmeow stores a push name and dispatches `PushName` (the client's `ContactUpdated`) only when it is not empty, not `"-"`, and differs from the sender's stored row; a missing row counts as changed (message.go:59-60, user.go:679-707, store/sqlstore/store.go:617-635) [observed: source].
So the sender's row in the receiving store decides whether the change fires, not how long ago that device was linked [observed: source].
* On 2026-11-06 the live session's text reached the peer account's fresh companion with no push name [observed: ARCHITECTURE §14, Contacts].
Whether a named presence before the send makes WhatsApp attach `notify` is [unknown]; `SendPresence` is the one stanza whatsmeow sends with the account's push name (presence.go:60-87) [observed: source].
Each fresh rerun needs a QR scan.
* Whether WhatsApp attaches `notify` differently for a long-linked device than for a newly linked one is [unknown].
Whether the server keeps a push name from an earlier connection is [unknown].
* `GetUserInfo` stores the business name even when its answer drops it (#153; user.go:252-270) [observed: source], so a profile read on the persistent peer would spend its store's first sight of the business name on a failing answer.

## Decision

* **One live diagnostic on the persistent sessions, on opt-in only.** `LiveContactsTests.Live_ThePeer_GetsTheBusinessPushNameAsTheFakeModeCheck_WhenItsFirstSightIsUnspent` runs with `NeedsPeer` and `NeedsPushNameFirstSight`, `WHATSMEOW_LIVE_PUSHNAME_FIRST_SIGHT`; without it the test starts nothing.
It pairs nothing, creates no session, logs nothing out, deletes nothing, changes no profile and never calls `GetUserInfo`.
* **The ladder**, `IntegrationTests/Support/PushNameStimulus.cs`, which `PushNameStimulusTests` checks in fake mode: the live session, the business account, connects anew, so it has sent no presence on that connection (the sidecar sends one only for the `SendPresence` RPC, `internal/presence/presence.go:64`) [observed: source].
It then sends the peer up to three texts: the first with no presence, the second after a named unavailable presence, the third after a named available one.
It stops at the first text that carries a push name; none or `"-"` goes on; a presence refused `NO_PUSH_NAME` is recorded and stops it, with no text after it.
Each text's line names its variant and what it carried, none, dash or named, never the name.
* **The parity rule.** The peer store's first sight counts for #36 AC6's push name branch only when the fresh companion's precondition holds on the same reads: the sender's row, read before the first text, had no push name; no push name change of the sender came before the named text was sent; and the change equals `ContactChecks.PushName` whole.
A row that had one fails `pushname_precondition_spent`: the texts are evidence of the stimulus, never a pass.
* **No equivalence with a fresh device.** A pass shows the push name path on a store where whatsmeow's first sight rule held; it claims nothing about what a newly linked device gets, and it does not explain the 2026-11-06 fresh run's null.
* **The presence it leaves.** The live session ends unavailable, a deliberate postcondition, not a restore: the presence it had before is [unknown]. The peer's presence is never touched.
* **Scope.** The push name branch only. The business name stays with the fresh companion and #153, the picture as ADR 0060, and About and the saved name stay unfulfilled, not waived. This does not close #36.
* No contract, public API, protocol or sidecar seam changes.

## Consequences

* A run with the opt-in spends the peer store's one first sight of the business's push name: a later one fails `pushname_precondition_spent`, by design. Up to three texts stay in the business and peer chat.
* A business text may carry its verified name, which whatsmeow then stores (message.go:56-57) [observed: source]; whether WhatsApp sends one here is [unknown]. The test logs the peer's rows of the business after the texts, booleans only, so a spent business name first sight on the persistent store is recorded; this ADR does not use that store for the business name.
* A named first text means the stimulus needed no presence on that connection; server retention and presence cannot be told apart then [inferred].
* A failed equality prints the push name in the local test output (`LiveChecks.Redacting` redacts JIDs, not names); that output stays private, and only the redacted `live:` lines are evidence.
* The fresh companion test is unchanged.
