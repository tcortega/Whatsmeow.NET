# 0067. The about control falls back to the legacy status IQ when WhatsApp refuses the mutation

* Status: Accepted
* Date: 2026-11-10
* Evidence: #36's live run of 2026-11-10 on main `210097c`, whatsmeow `c88388f983b4` (the business account's sidecar.log), upstream PR tulir/whatsmeow#1331 (open) and its diff, and the sidecar (lines below)
* Amends: [ADR 0063](0063-the-about-branch-on-the-persistent-sessions-on-consent.md) (the control's set path, and what branch 5 logs of a failure)

## Context

In the live run, `LiveContactService.SetAbout` failed `WHATSAPP_ERROR` on the set and on the restore, each answered in about 255 ms, and the account's own read still showed the original [observed: sidecar.log].
The status message, `set the about text: WhatsApp answered with an error`, is the one only the control's GraphQL branch produced, so WhatsApp answered the MEX mutation with a GraphQL error [observed: sidecar.log and the control's source].
Its code reached .NET as `WhatsAppErrorException.Code`, but branch 5 logged type names only and the server logs a failed call at debug, below the live sidecar's info, so the code and severity are [unknown].

* At the pin, `SetStatusMessage` sends only the MEX mutation (user.go:160-172), unchanged since `307a2edf117d` [observed: source, `diff` in the module cache].
* Open PR #1331 says that mutation is account-gated and fails `400 CRITICAL` on accounts without the feature, and that only the legacy `<iq xmlns="status" type="set" to="s.whatsapp.net"><status>text</status></iq>` changes what contacts see; it sends the mutation only for an emoji or a duration, then always the IQ, as it says WhatsApp Web does [observed: the PR's text and diff]. Whether that holds for the business account is [unknown].
* A second cause fits as well: the always-sent `ephemeral_duration_sec: 0` refused as invalid; #1219 sent 86400 for a text-only call [inferred: from #1219's diff].
* whatsmeow exports no call that sends the legacy IQ; `DangerousInternals().SendIQ` (internals.go:658) sends any info query and is marked `Deprecated: dangerous` (internals.go:36-39) [observed: source].

## Decision

* **The control tries the mutation first.** `SetAbout` calls `SetStatusMessage` with the text alone, as before; when it succeeds, nothing else is sent.
* **On a CRITICAL GraphQL error, the legacy IQ.** When the first GraphQL error's severity is `CRITICAL`, the refusal #1331 names, the control sends #1331's node for a text-only call, `<iq xmlns="status" type="set" to="s.whatsapp.net"><status>text</status></iq>`, through `DangerousInternals().SendIQ`, behind a small `iqs` interface the tests stub.
  The one suppression is `//nolint:staticcheck // SA1019` on that call, with its reason.
  The IQ's answer decides the call: answered is a success, an error goes through `classify` (an IQ error's code and keyword, without its nodes).
* **Only that refusal falls back.** Any other GraphQL error comes back `WHATSAPP_ERROR` with its code only, never its message.
  Any other failure is answered as before (`OUTCOME_UNKNOWN`, `NOT_CONNECTED`, ...) and no IQ follows: a timed-out or disconnected mutation may have been made and used whatsmeow's 75 s IQ wait, and a mutation answered in a form whatsmeow cannot read (no `<result>`, argo) may have been made too.
  It is still one `SetAbout` per set or restore, so ADR 0063's "never sent again" holds.
* **The IQ's time is what the call has left.** The live test calls `SetAbout` with a cancellation and no gRPC deadline [observed: LivePersistentContactsTests.cs:165], so the call's ctx has no deadline to give the IQ [inferred: grpc-dotnet sends no `grpc-timeout` without one]; the IQ waits whatsmeow's 75 s (request.go:150, 180-181) or until .NET's 90 s bound cancels the call, whose ctx reaches the IQ (`TestTheLiveControlsLegacyIQEndsWithACancelledCall`).
  The refusal came in about 255 ms [observed: sidecar.log], so the IQ normally ends with its own outcome; after a refusal slower than 15 s, .NET's bound may cancel it first, and the line then says `path` `unknown`.
* **One info line per set**, `live control: set the about text`, on every path once the session's client was found: `path` (`mex` or `legacy` when that call set it, `none` when nothing did, `unknown` when the last call may have: outcome unknown, cancelled or past its deadline), the mutation's classified failure as `mex`, or for any GraphQL error its `mex_code` and `mex_severity`, and the IQ's outcome as `legacy` (`answered`, or its classified error).
  Never the GraphQL error's message, which may echo the text [unknown]; the logger redacts phone numbers as every sidecar logger does (ADR 0039).
  The live sidecar logs at info, so the line reaches the run's sidecar.log.
* **Branch 5 logs WhatsApp's code.** A read or a set that failed with a `WhatsAppErrorException` names its `Code` after the type name (`failed, WhatsAppErrorException code 400`), none when it is 0, WhatsApp having sent none, in the set's failure and the restore's line; never its message.
  The exception carries no severity [observed: WhatsAppErrorException.cs], so the severity is in the sidecar's line only.
* **Test-only.** All of it is in `livetest_on.go` and the live tests' support code: a release build links none of it (`build/ci.sh`), and `SetStatusMessage` stays out of the contract (`sidecar/parity.excluded`).
* **Back to the mutation alone once a pin has #1331.** Then `SetStatusMessage` sends the legacy IQ itself, and the control's fallback, the `iqs` interface and the suppression are deleted; the bump re-checks it (ARCHITECTURE §14).

## Consequences

* One live run tells the two readings apart as far as the protocol shows them: the mutation's code and severity (#1331 names `400 CRITICAL` for an account without the feature), and whether the legacy IQ alone applies the text, which the account's own read (`GetUserInfo`, the usync `<status>`) and the peer's change then check [inferred: that these separate cause (a) from (b)].
  Measured on 2026-11-10 at main `143efda` (ARCHITECTURE §14 Contacts): the mutation's refusal was `400` `CRITICAL`, set and restore alike; the legacy IQ was answered and the account's own read showed the text it set; no peer change came within 240 s of either, for a cause still [unknown].
* A pass through the legacy path shows the IQ changed the text the account's own read and the peer see; it says nothing about the mutation's newer fields, which the branch never sets.
* The control now sends a node whatsmeow does not build at the pin; a node that differs from #1331's is caught by its test, not by whatsmeow's types.
