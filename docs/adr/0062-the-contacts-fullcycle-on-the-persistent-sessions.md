# 0062. The Contacts FullCycle on the persistent sessions, each branch with its own verdict

* Status: Accepted
* Date: 2026-11-08
* Evidence: #36 AC6, #153, the owner's direction to finish Contacts on the existing sessions with no new pairing, root's approval of the existing-session plan, the two checkpoint reviews and root's dispositions of them, root's #153 measurement on the existing sessions of 2026-11-08, whatsmeow at `307a2edf117d` and the sidecar (lines below)
* Amends: [ADR 0060](0060-a-live-test-control-and-a-fresh-companion.md) (its Context's "the saved name's expected value needs a fact from the owner", now taken from the owner on opt-in), [ADR 0061](0061-a-push-name-first-sight-on-a-persistent-store.md) (its diagnostic never calls `GetUserInfo` on the persistent peer; this test does, on its own opt-in), docs/guidelines/tests.md (Live WhatsApp)

## Context

#36 AC6 asks the Contacts FullCycle to pass live with the fake-mode assertions.
The owner directed that it be finished on the existing sessions: no QR code, no new companion, no logout or deletion of a persistent session, no direct edit of a store, and no profile change without his consent.
ADR 0060's fresh companion is therefore not run again.

What the persistent stores allow, for the peer (the owner's personal account) receiving from the live session (his business account):

* A push name and a business name change dispatch only when the stored value changes; a row with no value counts as changed (user.go:679-736, store/sqlstore/store.go:625, 646) [observed: source].
  For the business name, whatsmeow compares the row of the answer's JID, and writes the other form only after a change (user.go:248, 270, 713-725) [observed: source].
  That the answer keys an entry under the JID asked was seen for one query of the live account's own LID [observed: ARCHITECTURE §14, the #10 run]; for another account's LID it is [unknown] until measured.
* Both of the peer's rows of the business have its push name and its business name, before connecting and after catch-up [observed: root's #153 measurement of 2026-11-08, 03:36:14 and 03:38:39 UTC, `measurement.log`; earlier, ADR 0061's run of 2026-11-06 for the push name and the #36 preflight of 2026-11-06 for at least one business name row].
  So neither change can come on these stores without a real rename, which the owner has not authorized.
* The profile read's answer carries no verified business name at the pin `307a2edf117d` (#153; user.go:252-271) [observed: source and two live runs], so that branch's answer check fails there.
  An isolated measurement build of the pin with the reviewed fix answered the name for both forms, equal to the stored rows, with no business name change in 60 s [observed: root's #153 measurement]; that build is not the shipped pin.
* Querying by a form whose row already has the name is not a non-spending pre-check: when the answer's name differs from the cached one, whatsmeow stores it and writes the other form too (user.go:713-725), so it can spend the other row's first sight [inferred: source].
* A picture change arrives as a notification with no stored-value comparison (notification.go:93-117) [observed: source]; the run's own group photo needs no consent (ADR 0060).
* About needs the business account's about text changed, an account-wide change the owner has not consented to.
* The peer's phone number row of the business has a saved name and its LID row none [observed: root's #153 measurement; the #36 preflight, `saved_persistent_pn=true`].
  `Contact.SavedName` is the app state contact action's full name (`internal/contacts/contacts.go:282`) [observed: source]; that it equals the name the owner reads on the contact card is [inferred].
  Its expected value is a fact only the owner has, never a store's (docs/guidelines/tests.md Banned); the owner's answer is still pending.
  Which form an app state contact mutation names is WhatsApp's choice [unknown], so the LID row's saved name has no expected value.

A one-run pass of every branch is therefore not reachable on these stores: the push name branch cannot pass, and the business name branch can only check its answer.

## Decision

* **One live test on the persistent sessions, on opt-in only**: `LivePersistentContactsTests.Live_ThePersistentPeer_SeesTheContactChangesAsTheFakeModeChecks_WhereItsStoreLeavesAFirstSight`, `NeedsPeer` and `NeedsContactsFullCycle` (`WHATSMEOW_LIVE_CONTACTS_FULLCYCLE`).
  Its first line fails `contacts_opt_in_missing` without the variable, and the next `contacts_not_alone` when a test of the run already used the peer; root launches it alone, with a filter on its one method.
  It pairs nothing, logs nothing out, deletes nothing and changes no profile.
* **Before any WhatsApp network use**, with local calls that create no session, it reads both accounts and the live sidecar's whatsmeow version, and refuses `contacts_pin_known_bad` when the version is `307a2edf117d`, and `contacts_same_account` when the two sessions name the same account by either form.
  A version other than the known-bad one is not thereby fixed: a run still needs root's reviewed provenance of a pin with #153's fix.
* **The receiver of every branch is the peer; the subject is the live account.** The subject's traffic is counted on the peer's client before either session connects (`Support/ContactsTraffic.cs`); its handlers run before any later wait's for the same event (`EventPipeline.RunAsync`, registration order) [observed: source].
* **The branches**, in `IntegrationTests/Support/ContactsCycle.cs`, which `ContactsCycleTests` checks in fake mode, each failure kept while the next runs and all thrown at the end, so the test passes only when every branch passed; a cancellation stops it, no later branch starts, and it is thrown:
  * Branch 2: read the subject's rows; fail `businessname_origin_unclear` if the subject sent anything, or a business name change of it came, since the count started; read the profile by the first form whose row has no business name, else by the phone number.
    On an unspent row the change is awaited, or-null, before any verdict, and a dropped answer still logs, booleans only, whether the change came and named the JID asked; on a spent row none is awaited.
    Then, in order: exactly one entry for the JID asked (`businessname_answer_keyed`), a verified name in it (`businessname_answer_null`), an unspent row (`businessname_precondition_spent`), the change, no message, undecryptable message or other business name change of the subject by the time it was taken (`businessname_origin_unclear`), and `ContactChecks.BusinessName` with the answer's name and the JID asked.
  * Branch 1: ADR 0061's Addendum step with its guards: the live session sends a named unavailable presence, then a text; the rows are read at the branch's start and again just before the send.
    The text must be the expected record and carry a push name that is neither empty nor a dash (`pushname_stimulus_missing`); the sender's row must have had none in both reads (`pushname_precondition_spent`); no push name change may come before the send, and by the time the change is taken the text must be the subject's only message since the branch began, with no undecryptable one (`pushname_origin_unclear`); the change must equal `ContactChecks.PushName`.
  * Branches 3 and 4: ADR 0060's group photo set and removed, the peer the member's device (`LiveGroupPhoto`, moved unchanged out of the fresh companion test).
  * Branch 5: fails `about_not_authorized` with no stimulus, until the owner consents and an ADR adds the control.
  * Branch 6, read back: the peer's phone number row of the subject, compared whole with the saved name the owner stated (`WHATSMEOW_LIVE_PEER_SAVED_NAME`), the push name this run's text carried and the verified name this run's answer carried. A missing one fails it, `savedname_input_missing`, `readback_pushname_unanchored` or `readback_businessname_unanchored`; the store's value never stands in. The LID row is logged as booleans and not compared: its saved name has no expected value. This is narrower than the fake-mode FullCycle's whole contact list, and a pass shows the row equals this run's values, not that this run wrote them.
* **No failure prints a name.** Records are compared whole with their own equality; a mismatch says which fields differ, a count says how many, never a value.
* **Before and after.** It logs, booleans and forms only, the live store's rows of the peer, and how many other rows of the peer's name the business. It leaves the live session unavailable once it connected, as ADR 0061 does. After the body, whatever it did, three checks run, each on its own 30 s bound (`Support/AfterChecks.cs`), and their failures are thrown with the body's, the body's first: each database lists the same sessions with the same ids, accounts and devices, none logged out (`postflight_identity_changed`, `postflight_logged_out`), and no pairing file is left in the output directory (`postflight_pairing_output`), which the live sidecar's build empties before the baseline (`LiveSidecar.BuildAsync`); that shows what is left, not that no file came and went.
* **Its timeout** adds up its bounded operations, with a deliberate work allowance for the ones that have no bound of their own here (local reads, the profile query, the run group's creation with its joined waits and any leftover groups' closing, the control and send calls, the row reads): the allowance (300 s), two connects each with its catch-up (2 x (120 s + 600 s)), the branches' waits (60 s + 75 s + 60 s; branch 1's two waits are armed before its send, so each may take the send's 75 s and a watch), the picture's two waits (2 x 60 s), and five operations on a 30 s bound (the photo's removal, the presence, the three checks), 2,205 s.
* **When it runs.** Only once a main pin carries #153's fix and root has reviewed its provenance; not on a measurement build.
* No contract, public API, protocol, sidecar or invariant changes.

## Consequences

* Branch 1 fails `pushname_precondition_spent` on these stores, by design; its change stays observed only on the fresh companion of 2026-11-07 [observed: the one-day record], a different store and run from this read back.
* Branch 2 fails `businessname_precondition_spent` on these stores after checking the answer; its change stays [unknown] live, checked by the fake-mode FullCycle and the source alone.
* So this test is red on these stores, and #36 AC6 for Contacts is not met by it alone. Whether AC6 can close with those changes unobserved on one store is the owner's decision; this ADR does not make it.
* A run would spend the first sight of a row the profile is read by, if one had none; a later run then fails `businessname_precondition_spent`.
* A change carries no message id, so attribution is not atomic: a change stored before a read and delivered after it is still taken as the stimulus's [inferred]; the reads and the counts narrow that window, nothing here closes it.
* The fake-mode tests prove the cycle's bookkeeping and guards, not #153 nor what WhatsApp answers for another account's LID: the fake answers the verified name itself and passes the directory's LID as the other form.
* One text stays in the business and peer chat.
