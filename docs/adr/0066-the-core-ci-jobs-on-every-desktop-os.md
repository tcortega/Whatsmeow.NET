# 0066. The core CI jobs on linux-x64, osx-arm64 and win-x64 again

- Status: Accepted
- Date: 2026-11-10
- Evidence: issue #172 (the Windows platform pass at `ca4915c`) and PR #174's body (the rename measurements); `ci.yml` history (`e68dbd4`, PR #95); the tests named below
- Amends: PR #95's Linux-only CI; [ADR 0056](0056-aot-smoke-on-every-target-rid.md) and [ADR 0058](0058-jit-install-on-every-target-rid.md) ("PR CI stays Linux only", for the core jobs); [ADR 0046](0046-external-mode.md) (the `external` job on linux-x64 only); [ADR 0045](0045-a-channel-per-launch.md) (what keeps `RestartTokenTests` from passing vacuously)

## Context

Since `e68dbd4` (PR #95, 2026-11-02) `contract`, `go` and `dotnet` ran on linux-x64 only, "until the code is complete", and `external` was added on linux-x64 only after it [observed: `ci.yml`].
osx-arm64 and win-x64 were to come back in one platform pass.
That pass ran on a Windows 11 desktop (10.0.26200, NTFS) at `ca4915c`: `go` and `package` passed, and `contract`, `dotnet` and `external` failed with four bugs no CI run could have caught [observed: #172]:

1. `ExternalTokenTests` and the External-mode guide claimed a rename over the token file works on Windows while the client reads it; `File.Move` fails there with access denied [observed: #172].
2. `LiveHistoryBoundsTests` read the bounds file with `File.ReadAllLines`, which Windows refuses while the writer holds it with `FileShare.Read` [observed: #172].
3. `RestartTokenTests` in External mode asserted that a call fails during each restart; on Windows a connect to a closed loopback port is refused after about 2 s, and the standalone sidecar is back in about 1 s, so none did [observed: #172].
4. `build/release-test.sh` gave jq a process substitution, a `/proc/<pid>/fd` path a native `jq.exe` cannot open, and needed `zip`, which the windows-2025 runner lacks [observed: #172].

## Decision

- `ci.yml` runs `contract`, `go`, `dotnet` and `external` on linux-x64 (`ubuntu-26.04`), osx-arm64 (`macos-26`) and win-x64 (`windows-2025`), on every trigger, pull requests included, with `fail-fast` off.
  The `ci` gate needs every leg.
  `packaging` stays on main and on demand (ADR 0056, 0058).
- **Token rotation on Windows: the writer retries.**
  Measured on the same desktop, .NET 10.0.14 [observed: #174]: `File.Move` and Go's `os.Rename` (`MoveFileEx` [observed: Go 1.27.3 `internal/syscall/windows/syscall_windows.go:357`]) fail with access denied while the client holds the file, and `File.Move` also while a Go `os.Open` holds it; a rename with `FILE_RENAME_FLAG_POSIX_SEMANTICS` lands while the client holds it, but fails with a sharing violation while Go holds it, since Go opens without `FILE_SHARE_DELETE` [observed: `syscall_windows.go:395`]; a writer retrying `File.Move` every millisecond against a client reading in a loop landed 500 of 500 renames, the slowest after 15 failures and 237 ms, and the reader never saw a partial token.
  On Linux and macOS the rename lands while either side holds the file, and the same loop saw no partial token [observed: #174].
  The client already opens the file only for the read, and an unreadable token already waits for the restart backoff; neither read can be shorter, and nothing in the product changes.
  The guide tells Windows writers to retry, and `ExternalTokenTests` pins per OS what holds: the rename lands while the client holds the file on Linux and macOS, and on Windows it fails, the old token stays whole, and it lands once the client's read ends.
- `LiveHistoryBoundsTests` reads the file sharing write, as a reader beside a live writer must on Windows.
- `RestartTokenTests` counts the calls that started after the client logged the sidecar gone (`SidecarExited`, `ExternalSidecarLost`) and before it counted the next launch, and fails when a restart had none; it no longer requires one of them to fail.
  Its claim is unchanged: every failure is unreachable (`Unknown`) and none is refused `Unauthenticated`.
  In External mode on Windows such a call may wait out the slow refusal and reach the next sidecar at the same address, with the token from the same file; ADR 0046 already scoped "an endpoint of its own" to embedded launches.
  With the lost record's name misspelt, the test fails "No call started while sidecar 1 restarted" [observed: macOS, External mode].
- `build/release-test.sh` gives jq its input on stdin, and builds its packages with `zip`, or with `7z` where there is no zip, and says so when neither is on PATH.
- AGENTS.md states the Windows host's prerequisites: Git Bash, jq, zip or 7z, a C compiler for `go test -race`, and the Visual Studio Build Tools C++ workload for Native AOT.

## Consequences

- A change that breaks macOS or Windows fails its pull request, instead of waiting for the next platform pass.
- Each pull request runs twelve core jobs instead of four, eight of them on the slower and costlier macOS and Windows runners.
