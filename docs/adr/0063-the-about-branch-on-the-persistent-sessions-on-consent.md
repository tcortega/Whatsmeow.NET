# 0063. The about branch on the persistent sessions, a text-only change and restore on consent

* Status: Accepted
* Date: 2026-11-08
* Evidence: #36 AC6, ADR 0062's branch 5, root's approval of the offline About plan (R1-R6), whatsmeow at `307a2edf117d`, upstream main `79dc2a8`, upstream PRs #1219 (closed) and #1331 (open), and the sidecar (lines below)
* Amends: [ADR 0060](0060-a-live-test-control-and-a-fresh-companion.md) (a second control service, and the `livetest` tag in the contacts slice), [ADR 0062](0062-the-contacts-fullcycle-on-the-persistent-sessions.md) (branch 5, which failed until "the owner consents and an ADR adds the control"), docs/guidelines/tests.md (Live WhatsApp)

## Context

ADR 0062's branch 5 fails `about_not_authorized` with no stimulus: changing the business account's about text is an account-wide profile change, and the owner has not consented to it.
Root separated preparing the branch from running it: the code may exist and refuse, while a real change still needs the owner's consent, which this ADR does not supply.

What whatsmeow offers at the pin `307a2edf117d`:

* `SetStatusMessage(ctx, types.SetStatusInput)` sends only the MEX mutation `9152604461510864` with `{"input": status}`, and checks nothing of the answer but GraphQL errors (user.go:160-172, newsletter.go:172-240, its "TODO check output result?") [observed: source].
  Upstream main `79dc2a8` has the same function: `user.go` last changed on 2026-10-26, before the pin [observed: GitHub commits API].
* `SetStatusInput` is `{Text *string; Emoji *SetStatusEmoji, omitempty; Duration jsontime.Seconds "ephemeral_duration_sec"}` (types/user.go:191-199) [observed: source].
  `Duration` has no `omitempty`, so a text-only call sends `ephemeral_duration_sec: 0` [observed: source].
  What 0 means to WhatsApp (no expiry, or unchanged), and whether a call without an emoji clears one already set, is [unknown].
* `GetUserInfo` reads only the `<status>` node's content as the text (user.go:257-258), and nothing else of it, so no emoji, expiry or time it was set [observed: source]; whether WhatsApp's answer carries them at all is [unknown].
  It ignores whether that content was there, so an about that is unset, empty or not given (a privacy setting, say) is the same `""` [observed: source], which the client maps to `About = null` (`ContactMapping.cs:28`) [observed: source].
* A `status` notification dispatches `UserAbout` with the `<set>` text, the sender and the time, nothing else (notification.go:453-470, routed at 522) [observed: source].
  A `mex` notification dispatches only newsletter and account reach-out events (notification.go:415-450), while WhatsApp's query list names a `TextStatusUpdateNotification` (argo/name-to-queryids.json:244) [observed: source]; so a text status change announced that way would dispatch nothing [inferred].
  Whether changing the business account's about makes WhatsApp send the peer's companion a `status` notification at all is [unknown].
* The query list also names `GetTextStatus` (argo/name-to-queryids.json:89), which no whatsmeow function calls [observed: grep].
* Closed PR #1219, superseded by upstream commit `662b012` that moved `SetStatusMessage` to the MEX mutation, says only the MEX call updates the about; open PR #1331 (no review, no checks) says the opposite: the mutation is account-gated, fails `400 CRITICAL` on some accounts, and only the legacy `<iq xmlns="status">` updates what contacts see [observed: the PRs' texts]. Which holds for the business account is [unknown].
* whatsmeow waits 75 s for an IQ's answer by default (request.go:150) [observed: source].

So the protocol as we have it reads and writes the about **text** only.
A restore through it can be checked only as "the text `GetUserInfo` reads equals the text read before the change"; an emoji or expiry the original had, and the time it was set, are neither read nor put back [inferred: from the facts above].

## Decision

* **A second control service, `whatsmeow.livetest.v1.LiveContactService`**, beside ADR 0060's `LiveTestService` in `proto/whatsmeow/livetest/v1/livetest.proto`, with one RPC, `SetAbout`: a session sets its account's about text with whatsmeow's `SetStatusMessage`, the text only, no emoji and no duration.
  A service of its own, since gRPC registers a service once and the groups slice serves the other; each slice registers its own.
  It refuses an empty text, `INVALID_ARGUMENT`, before whatsmeow: an empty about is never what the branch sets or restores.
  A GraphQL error comes back as `WHATSAPP_ERROR` with its codes only, never its message, which might echo the text [unknown].
* **Served only by a livetest build**, as ADR 0060's: `sidecar/internal/contacts/livetest_on.go` registers it, its `livetest_off.go` twin nothing, so a release build answers `UNIMPLEMENTED`; `cmd/whatsmeow-net` calls the contacts slice's register function in every build; `tools/check-repo` allows the `livetest` tag in `sidecar/internal/contacts/` too.
  `build/ci.sh`'s existing checks already cover it: the release build may link no `livetest_*` file but `livetest_off.go` and not the control's package, and no shipped assembly may contain `Whatsmeow.LiveTests.Control`.
  `SetStatusMessage` stays excluded from the contract in `sidecar/parity.excluded`.
* **The consent**, `WHATSMEOW_LIVE_ABOUT_CONSENT`, consented only when its value is exactly `text-only`: it records a future permission, that the owner consented to one change of the business account's about text by the live session within the branch's bounds, said that about has no emoji and no expiry, and accepts that only the text is put back and its time changes.
  It is not a `Needs*` opt-in: without it the branch still runs and fails, never skipped.
  Setting it is a launch decision; this ADR gives no consent and no fact about the account.
* **Branch 5**, `IntegrationTests/Support/AboutChange.cs`, which `AboutChangeTests` checks in fake mode, is the step after the picture in ADR 0062's cycle:
  1. Without the consent it fails `about_not_authorized` before any read or call, as before.
  2. The live session reads its own profile by its phone number; no single entry for it fails `about_original_unkeyed`, a null or empty about `about_original_unreadable`, an about that starts with the test text's `wmn about ` `about_original_is_test_text` (taken as an earlier run's, killed before its restore [inferred], since restoring it would put test text back and pass; an owner's about that started so would be refused too), and a failed read `about_original_read_failed`; none of them changes anything.
  3. The test's text is `wmn about <UTC time>`; one equal to the original fails `about_stimulus_not_new`.
  4. The peer's wait for an about change naming the business, by either form, is armed; then one `SetAbout` with the test's text, after which the change counts as possibly made whatever the call answers: a failed or timed-out write may still apply, and it is never sent again.
  5. The live session reads its own about again: not the test's text fails `about_not_applied`; then the change must come (`about_event_missing`) and equal `ContactChecks.About` for the JID it names, compared whole.
  6. **The restore**, whenever the change may have been made, on bounds of its own that the test's token does not cancel: the peer's wait for the original text is armed, one `SetAbout` with the original, and then, whatever that call did, the live session's own read; the restore passes only when that read equals the original (`about_restore_unverified` otherwise, with the set's and the read's outcomes, type names only).
     A set that failed while the read shows the original is logged, not failed: the read is the evidence.
     Whether the restore's change came, and equalled the original, is logged, not required; a wait that ended without one, as when the receiver stopped (`ObjectDisposedException`), is logged as none by its type name, so it never hides the read's verdict or the body's failure.
  7. The body's failure and the restore's are both thrown; a restore's failure is never dropped because the body failed, unlike `LiveRestore`'s, since the about is the account's, not the run's.
* **Bounds.** Each call, a read or a set, has its own bound of whatsmeow's 75 s plus 15 s, 90 s, from the test's token in the body and from none in the restore.
  Each change wait is armed before its set and times out 2 x 90 s + the watch (60 s) after it is armed, so it covers the set, the read after it, and a watch; it runs beside them, not after.
  So the body takes at most 90 s (read) + max(90 s + 90 s, 2 x 90 s + 60 s) = 330 s, and the restore at most max(90 s + 90 s, 2 x 90 s + 60 s) = 240 s.
  Every armed wait is cancelled and awaited on every exit; it ends at its cancellation, or at the latest at its own timeout [inferred: `WaitForAsync` honours its token].
  The test's timeout adds 330 s + 240 s = 570 s to ADR 0062's 2,205 s: 2,775 s.
  Whether xUnit runs a test's `finally` after its timeout cancelled it is [unknown], and a killed process runs none; the original text is never written anywhere, so after a kill the owner sets it back from the phone.
* **No about text in any output.** Log lines and failures carry booleans, field names and exception type names only; an exception from a read or a set is never kept, its type name is, so no remote message reaches the test's result, inner exceptions and `ToString` included.

## Consequences

* A pass shows the business's about text, as `GetUserInfo` reads it, became the test's text, that the peer got that change as the fake-mode record, and that the text read back after the restore equals the one read before.
  It shows nothing about an emoji, an expiry, the time the about was set or what other contacts see; a text-only restore would lose an emoji or an expiry the original had [inferred], so the consent's statement that there are none is the owner's, not the test's.
* On the pin, the branch measures which of #1219's and #1331's descriptions holds for this account: `about_not_applied` or `about_event_missing` fail it honestly.
  A later pin with #1331 would send the legacy IQ for a text-only call; the bump re-checks these facts (ARCHITECTURE §14).
* The live session's own profile read stores its own LID pair and business name in the live store (user.go:262-275); no branch reads the live store's rows of its own account [observed: `ContactsCycle`].
* Every contact of the business account can see its about change while it lasts.
* The branch runs only in ADR 0062's test, so only once a main pin carries #153's fix: the test refuses `contacts_pin_known_bad` before any network use.
* The fake-mode tests prove the branch's order, gates, restore and privacy, not WhatsApp's behaviour: the fake's set is a directory update and a scripted notification.
