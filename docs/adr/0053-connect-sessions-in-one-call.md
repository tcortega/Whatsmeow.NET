# 0053. Connect the sessions in one call

- Status: Accepted
- Date: 2026-11-04
- Evidence: issues #77 and #127, PR #78's win-x64 soaks and the tests named below
- Amends: [ADR 0020](0020-sessions-and-the-store.md) (how the client reconnects after a restart); `error.proto` (an `ErrorReason` sent as a field)

## Context

After a sidecar restart the client connected again, one `Connect` call after another, every session the application had connected that came back `Disconnected` (ADR 0020).
Each `Connect` commits the session's `Connecting` state before it returns (`session.(*Service).transition`, §7.4), a sync of the database under `synchronous(FULL)`.
On win-x64, with the shared fake sidecar of a full suite run, that was about 106 calls, 111-262 ms each on a cold runner and 0.8-2.2 s each in one crash collection run, and the ready line to `SidecarRestarted` took up to 27.7 s, always in the first run on a fresh runner [observed: PR #78 run 41490492321, fixture timelines].
One restart timed out at 120 s with no attempt failed [observed: PR #78 run 41486885606]; that it was this phase, and that its time is the same cold-disk flush that made the restart's own startup miss `StartupTimeout`, is [inferred] (ARCHITECTURE §14, restarts on Windows).
The reconnect has no deadline, so a slow one never fails the attempt [observed: `WhatsmeowSidecar.LaunchAsync`].

## Decision

- **`SessionService.ConnectSessions`** connects several sessions, each as `Connect` does, and answers with one `ConnectSessionResult` per session, in the request's order, once every connect call ended.
  It records every `Connecting` state in one `transitionAll`, so in one commit, then runs whatsmeow's `ConnectContext` for each, one after another, in id order.
  If that commit fails, each session's `Connecting` is recorded on its own, as `Connect` records it, so one session whose write fails gets `INTERNAL` and goes back to its previous state while the others connect.
  A session id given twice fails the call `INVALID_ARGUMENT` (field `session_ids`), before any session is looked at, since it would wait for itself; any other failure is the session's result, never the call's: one of `Connect`'s reasons, `INVALID_ARGUMENT` for a malformed id, or `INTERNAL`.
- **One connect call at a time.** mautrix-whatsapp connects its logins one after another at startup (mautrix-go `bridgev2/bridge.go:422-430`, `0907c3c`; mautrix-whatsapp `pkg/connector/client.go:244`, `5dd621f`) [observed: source], and so did this client's reconnect before this ADR [observed].
  Whether WhatsApp treats a burst of handshakes from one host differently is [unknown], and no live test measures it.
  What the restart gains here comes from the single commit, not from concurrency: the fake WhatsApp's `ConnectContext` returns at once, with no I/O (`fakewhatsapp/client.go:81-110`) [observed], so concurrent connect calls could not have shortened the soak's reconnects [inferred].
  Running them at once would be possible: `ConnectContext` locks only its own client's socket (client.go:541-604), and each client has its own HTTP transport (client.go:272-278) [observed: source at the pin]; it waits for a measurement against real WhatsApp.
- **Locks.** The batch takes each session's ops in id order, so two calls that share sessions never wait for each other in a cycle; a session whose ops another call holds (a `Disconnect`, a `Logout`) delays the sessions after it, as it delayed the client's loop before. There is no `TryLock`: a busy session is waited for, not skipped.
  Each session's ops are released when its own connect call ended, and those with nothing to connect at once.
- **A result carries the reason as an `ErrorReason` field**, `ERROR_REASON_UNSPECIFIED` for a session that connected or was connecting or connected already.
  This is the one place the enum travels as a field instead of in `ErrorInfo`: a status answers for one call, and this call answers for many sessions.
- **No message in a result.** A status message goes through `server.toStatus`, which redacts phone numbers, JID user parts and URL queries (ADR 0039); a free-text field would not.
  The sidecar logs each failed session with its error instead, through its logger, whose writer redacts at info and above, and the client logs the session and its `ErrorCode` (`ReconnectRefused`).
- **The client reconnects with one `ConnectSessions` call** for the sessions it would have connected one by one; it checks once, just before the call, that the application did not disconnect one meanwhile.
  A failed call is logged for each session (`ReconnectFailed`), as a failed `Connect` was.
* **Hosted startup uses the same batch** for its eligible declarations (#127, ADR 0034).
  Every successful result starts that session's events and remembers it for a restart, including a session an external sidecar already kept online.
  After processing all successes, the first failed result in request order fails host startup with the same public exception subtype and error code as `Connect`.
  The result has no status detail or metadata, so its exception uses fixed text and any unavailable metadata stays unknown.
- `Connect` is `ConnectSessions` with one session (`connectAll`), so the two never differ.

## Consequences

* Hosted startup emits one actual `ConnectSessions` RPC and no single `Connect` RPCs, connects the paired declarations, starts their events and remembers them across a crash [observed: `HostingBatchTests.HostStart_ConnectsPairedDeclarationsInOneCall_AndKeepsTheirEventsAcrossACrash`].
  The commit measurement remains the Go endpoint test below: it measures the batch's Connecting transitions, not every transaction during host startup.
  No hosted wall-clock speedup was measured.
* A failed declared session does not prevent later declarations connecting, while host startup still throws `NotConnectedException` with `ConnectionFailed` [observed: `HostingBatchTests.HostStart_OneDeclaredConnectFails_TheOthersConnectBeforeTheStartReportsFailure`].

- A reconnect commits once however many sessions it connects [observed: `TestConnectSessionsRecordsEveryConnectingStateInOneCommit`, 1 commit for 20 sessions, 20 with a commit each (mutation)].
- The connect calls run one at a time, in id order [observed: `TestConnectSessionsConnectsOneAtATimeInIdOrder`, red when they run at once].
- One session whose state cannot be recorded fails alone [observed: `TestConnectSessionsConnectsTheOthersWhenOneStateCannotBeRecorded`, a trigger on one session's row; red with the batch's error given to every session].
- A session deleted after the lookup and before its ops were taken is `SESSION_NOT_FOUND` in its result, and the others connect [observed: `TestConnectSessionsReportsASessionDeletedBeforeItsLockAsNotFound`, red without the check under the ops].
- A session no longer waits for the others' connect calls to be disconnected or logged out [observed: `TestConnectSessionsLetsGoOfASessionOnceItsOwnConnectEnded`, red when every session's ops are held until the last call ends].
- `SidecarRestart_ConnectsAgainEverySessionTheApplicationConnected` checks the client side end to end: three sessions connected again after a crash, none logged as failed.
- On win-x64, with the connect calls still at once, 36 suite and crash collection runs, 936 restarts, had no failed run or restart attempt, and the ready line to `SidecarRestarted` took at most 548 ms, against up to 27.7 s before [observed: PR #78 runs 41850964765 and 41490492321]. That the 120 s case was this phase stays [inferred]: the soak before also had no failure.
- An External-mode client of a standalone sidecar older than this gets `UNIMPLEMENTED` for `ConnectSessions` and logs `ReconnectFailed` for each session; a hosted first start fails instead: acceptable before the first published tag, when `protocol` stays 1 (ADR 0015); after it, such an RPC comes with a `protocol` bump.
