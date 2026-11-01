# 0017. Sidecar process conventions from the walking skeleton

- Status: Accepted
- Date: 2026-11-01
- Evidence: issue #4, its plan review, and the tests named below

## Context

The walking skeleton (#4) is the first code that starts, supervises and stops `whatsmeow-net`.
ARCHITECTURE §8.5, §9 and §10 described four parts of that before any code existed, and building them showed where the text did more than the issue needs or could not be tested:

- §10 asked for a freshly created socket directory per launch and also for removing a stale socket before `Listen`; with a fresh directory there is never a stale socket, so that removal would be dead code.
- §8.5 asked for Polly exponential backoff with jitter. With jitter, a test of the backoff can only assert ranges, and one local child process has no herd of clients to spread [inferred].
- §8.5 put a SIGTERM between closing stdin and the kill. The sidecar handles SIGTERM on the same path as stdin EOF (`sidecar/cmd/whatsmeow-net`), and Windows has no SIGTERM.
- §8.5 and §9 put a `grpc.health.v1` call before `GetInfo`. `GetInfo` right after the ready line already proves the socket, the token and the version.

## Decision

- **Socket directory.** The client creates it once per `WhatsmeowSidecar` with `Directory.CreateTempSubdirectory` (mode `0700` on macOS [observed: SDK 10.0.403]), passes it with `--socket-dir`, reuses it for every restart, and deletes it on dispose.
  On every launch the sidecar makes it owner-only again (on Windows an explicit protected DACL for the current user) and removes the socket a killed sidecar left behind.
  The restart test reuses the directory after a hard kill, so the stale-socket removal runs for real on all three OSes.
- **Restart backoff.** Hand-written and deterministic: 1 s, doubling, capped at 30 s, back to 1 s after a run that stayed up 60 s; a failed launch counts as an attempt.
  No jitter and no Polly here; Polly stays in the ARCHITECTURE §5 stack for later callers.
- **Stop.** The `Shutdown` RPC, then close stdin, then a 5 s grace on the `TimeProvider`, then `Kill(entireProcessTree: true)`. No SIGTERM step.
- **Health.** The sidecar serves `grpc.health.v1`, as `sidecar.proto` promises. The client does not call it during startup; `GetInfo` is the readiness and version check.

## Consequences

- `tests/Whatsmeow.Client.Tests` asserts exact delays under `FakeTimeProvider`.
- If the parent .NET process is killed hard, the empty socket directory stays in the temp directory: the sidecar removes its socket on exit, but only the client deletes the directory.
- The tether backups (`PR_SET_PDEATHSIG`, the macOS parent-PID poll, the Windows Job Object) are not built yet (follow-up issue); the stdin tether alone passes the kill-parent test on Linux, macOS and Windows.
