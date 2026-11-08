# 0062. The Contacts FullCycle on the persistent sessions, each branch with its own verdict

* Status: Accepted
* Date: 2026-11-08
* Evidence: #36 AC6, #153, the owner's direction to finish Contacts on the existing sessions with no new pairing, root's approval of the existing-session plan, whatsmeow at `307a2edf117d` and the sidecar (lines below)
* Amends: [ADR 0060](0060-a-live-test-control-and-a-fresh-companion.md) (its Context's "the saved name's expected value needs a fact from the owner", now taken from the owner on opt-in), [ADR 0061](0061-a-push-name-first-sight-on-a-persistent-store.md) (its diagnostic never calls `GetUserInfo` on the persistent peer; this test does, on its own opt-in), docs/guidelines/tests.md (Live WhatsApp)

## Context

#36 AC6 asks the Contacts FullCycle to pass live with the fake-mode assertions.
The owner directed that it be finished on the existing sessions: no QR code, no new companion, no logout or deletion of a persistent session, no direct edit of a store, and no profile change without his consent.
ADR 0060's fresh companion is therefore not run again.

What the persistent stores allow, for the peer (the owner's personal account) receiving from the live session (his business account):

* A push name and a business name change dispatch only when the stored value of the JID concerned changes; a missing row counts as changed (user.go:679-707, 709-736) [observed: source].
  For the business name, whatsmeow compares the row of the JID the profile was read by, and writes the other form only after a change (user.go:713-725) [observed: source].
* Both of the peer's rows of the business have its push name [observed: ADR 0061's run of 2026-11-06, the evidence record], so the push name change cannot come there.
* At least one of the peer's rows of the business has its business name [observed: the #36 preflight of 2026-11-06, `business_persistent=true`, which counts any row]; whether both do is [unknown] until read.
* The profile read's answer carries no verified business name at the pin (#153; user.go:253-271) [observed: source and two live runs], so that branch's answer check fails until a pin carries the fix.
* A picture change arrives as a notification with no stored-value comparison (notification.go:93-117) [observed: source]; the run's own group photo needs no consent (ADR 0060).
* About needs the business account's about text changed, an account-wide change the owner has not consented to.
* The peer's phone number row of the business has a saved name [observed: the #36 preflight, `saved_persistent_pn=true`]; its expected value is a fact only the owner has, never a store's (docs/guidelines/tests.md Banned).
  Which form an app state contact mutation names is WhatsApp's choice [unknown], so the LID row's saved name has no expected value.

A one-run pass of every branch is therefore not reachable on these stores unless the read before branch 2 finds a row with no business name, and the push name branch cannot pass at all.

## Decision

* **One live test on the persistent sessions, on opt-in only**: `LivePersistentContactsTests.Live_ThePersistentPeer_SeesTheContactChangesAsTheFakeModeChecks_WhereItsStoreLeavesAFirstSight`, `NeedsPeer` and `NeedsContactsFullCycle` (`WHATSMEOW_LIVE_CONTACTS_FULLCYCLE`).
  Its first line fails `contacts_opt_in_missing` without the variable, and the next `contacts_not_alone` when a test of the run already used the peer; root launches it alone, with a filter on its one method.
  It reads both accounts before connecting, which never creates a session; it pairs nothing, logs nothing out, deletes nothing and changes no profile.
* **The receiver of every branch is the peer; the subject is the live account.**
* **The branches**, in `IntegrationTests/Support/ContactsCycle.cs`, which `ContactsCycleTests` checks in fake mode, each failure kept while the next runs and all thrown at the end, so the test passes only when every branch passed:
  * Branch 2: read the subject's rows; fail `businessname_origin_unclear` if the subject sent anything, or a business name change of it came, since the count started; read the profile by the first form whose row has no business name, else by the phone number; the answer's entry must carry a verified name (`businessname_answer_null` otherwise); a spent pair of rows then fails `businessname_precondition_spent`; otherwise the change must equal `ContactChecks.BusinessName` with the answer's name and the JID asked.
  * Branch 1: ADR 0061 Addendum's step: the live session sends a named unavailable presence, then a text; the text must carry a push name (`pushname_stimulus_missing`), the sender's row must have had none (`pushname_precondition_spent`), and the change must equal `ContactChecks.PushName`.
  * Branches 3 and 4: ADR 0060's group photo set and removed, the peer the member's device (`LiveGroupPhoto`, moved unchanged out of the fresh companion test).
  * Branch 5: fails `about_not_authorized` with no stimulus, until the owner consents and an ADR adds the control.
  * Branch 6, read back: the peer's phone number row of the subject, compared whole with the saved name the owner stated (`WHATSMEOW_LIVE_PEER_SAVED_NAME`, never printed), the push name this run's text carried and the verified name this run's answer carried. A missing one fails it, `savedname_input_missing`, `readback_pushname_unanchored` or `readback_businessname_unanchored`; the store's value never stands in. The LID row is logged as booleans and not compared: its saved name has no expected value.
* **Before and after.** It logs, booleans and forms only, the live store's rows of the peer, and how many other rows of the peer's name the business. It leaves the live session unavailable, as ADR 0061 does. After the body it checks that both databases list the same sessions with the same accounts and devices, and that no pairing file was written (`postflight_identity_changed`).
* **Its timeout** is the arithmetic of its bounds: the work (300 s), two connects each with its catch-up (2 x (120 s + 600 s)), the branches' waits (60 s + 75 s + 60 s), the picture's two waits (2 x 60 s) and two restores (2 x 30 s), 2,115 s.
* **When it runs.** Only once a main pin carries #153's fix, so that the one first sight a row without a business name may still offer is spent where the shipped fix is checked; not on a measurement build.
* No contract, public API, protocol, sidecar or invariant changes.

## Consequences

* Branch 1 fails `pushname_precondition_spent` on these stores, by design; its change stays observed only on the fresh companion of 2026-11-07 [observed: the one-day record], a different store and run from this read back.
* Branch 2's change is observed live only if a row without a business name is found; otherwise it stays [unknown] live, checked by the fake-mode FullCycle and the source alone.
* So this test is red on these stores unless both first sights are found, and #36 AC6 for Contacts is not met by it alone. Whether AC6 can close with those changes unobserved on one store is the owner's decision; this ADR does not make it.
* A run spends the first sight of the row the profile is read by, if it had none; a later run fails `businessname_precondition_spent`.
* The business's traffic is counted from right after the peer's catch-up: a message delivered before it whose business name whatsmeow stores on a goroutine of its own (message.go:56-57) could change a row after the read [inferred], a window the count cannot close.
* One text stays in the business and peer chat.
* A failed equality prints names in the local test output (`LiveChecks.Redacting` redacts JIDs, not names); that output stays private, and only the redacted `live:` lines are evidence.
