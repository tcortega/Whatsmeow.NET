# 0053. Connect the sessions in one call after a restart

- Status: Accepted
- Date: 2026-11-04
- Evidence: issue #77, PR #78's win-x64 soaks and the tests named below
- Amends: [ADR 0020](0020-sessions-and-the-store.md) (how the client reconnects after a restart); `error.proto` (an `ErrorReason` sent as a field)

## Context

After a sidecar restart the client connected again, one `Connect` call after another, every session the application had connected that came back `Disconnected` (ADR 0020).
Each `Connect` commits the session's `Connecting` state before it returns (`session.(*Service).transition`, §7.4), a sync of the database under `synchronous(FULL)`.
On win-x64, with the shared fake sidecar of a full suite run, that was about 106 calls, 111-262 ms each on a cold runner and 0.8-2.2 s each in one crash collection run, and the ready line to `SidecarRestarted` took up to 27.7 s, always in the first run on a fresh runner [observed: PR #78 run 41490492321, fixture timelines].
One restart timed out at 120 s with no attempt failed [observed: PR #78 run 41486885606]; that it was this phase, and that its time is the same cold-disk flush that made the restart's own startup miss `StartupTimeout`, is [inferred] (ARCHITECTURE §14, restarts on Windows).
The reconnect has no deadline, so a slow one never fails the attempt [observed: `WhatsmeowSidecar.LaunchAsync`].

## Decision

- **`SessionService.ConnectSessions`** connects several sessions, each as `Connect` does, and answers with one `ConnectSessionResult` per session, in the request's order, once every connect call ended.
  It records every `Connecting` state in one `transitionAll`, so in one commit, then runs whatsmeow's `ConnectContext` for each at once: it locks only its own client's socket (client.go:541-604), and each client has its own HTTP transport (client.go:272-278) [observed: source at the pin].
  A session's ops are taken in id order and released when its own connect call ended.
  A session id given twice fails the call `INVALID_ARGUMENT` (field `session_ids`), since it would wait for itself; any other failure is the session's result, never the call's.
- **A result carries the reason as an `ErrorReason` field**, `ERROR_REASON_UNSPECIFIED` for a session that connected or was connecting or connected already.
  This is the one place the enum travels as a field instead of in `ErrorInfo`: a status answers for one call, and this call answers for many sessions.
- **No message in a result.** A status message goes through `server.toStatus`, which redacts phone numbers, JID user parts and URL queries (ADR 0039); a free-text field would not.
  The sidecar logs each failed session with its error instead, through its logger, whose writer redacts at info and above, and the client logs the session and its `ErrorCode` (`ReconnectRefused`).
- **The client reconnects with one `ConnectSessions` call** for the sessions it would have connected one by one; it checks once, just before the call, that the application did not disconnect one meanwhile.
  A failed call is logged for each session (`ReconnectFailed`), as a failed `Connect` was.
- `Connect` is `ConnectSessions` with one session (`connectAll`), so the two never differ.

## Consequences

- A reconnect commits once however many sessions it connects [observed: `TestConnectSessionsRecordsEveryConnectingStateInOneCommit`, 1 commit for 20 sessions, 20 with a commit each (mutation)].
- The connect calls run at once [observed: `TestConnectSessionsOpensTheConnectionsConcurrently`, red when they run in order].
  The client's reconnect used to open one session's connection after another, as mautrix-whatsapp's `StartLoginsForUsers` does (mautrix-go `bridgev2/bridge.go:422-430`, `0907c3c`); whether WhatsApp treats a burst of handshakes from one host differently is [unknown], and no live test measures it.
- A session no longer waits for the others' connect calls to be disconnected or logged out [observed: `TestConnectSessionsLetsGoOfASessionOnceItsOwnConnectEnded`, red when every session's ops are held until the last call ends].
- `SidecarRestart_ConnectsAgainEverySessionTheApplicationConnected` checks the client side end to end: three sessions connected again after a crash, none logged as failed.
- Whether this ends the 120 s case on win-x64 is measured by PR #78's soak, not by these tests.
- Before the first published tag, `protocol` stays 1 (ADR 0015).
