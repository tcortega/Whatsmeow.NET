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
  The `ci` gate needs every leg to succeed; only `packaging` may be skipped, as it is on a pull request.
  A failed or cancelled `dotnet` or `external` leg uploads its fixture log.
  `packaging` stays on main and on demand (ADR 0056, 0058).
- **Token rotation on Windows: the writer retries.**
  Measured on the same desktop, .NET 10.0.14 [observed: #174, whose body has the program; `ExternalTokenTests` on win-x64, run 42865441309]: `File.Move` and Go's `os.Rename` (`MoveFileEx` [observed: Go 1.27.3 `internal/syscall/windows/syscall_windows.go:357`]) fail with access denied while the client holds the file, and `File.Move` also while a Go `os.Open` holds it; a rename with `FILE_RENAME_FLAG_POSIX_SEMANTICS` lands while the client holds it, but fails with a sharing violation while Go holds it, since Go opens without `FILE_SHARE_DELETE` [observed: `syscall_windows.go:395`]; a writer retrying `File.Move` against a client reading in a loop landed 500 of 500 renames, every 1 ms the slowest after 15 failures and 237 ms, every 10 ms after 9 failures and 142 ms, and the reader never saw a partial token.
  On Linux and macOS the rename lands while either side holds the file, and the same loop saw no partial token [observed: #174].
  The client already opens the file only for the read, and an unreadable token already waits for the restart backoff; neither read can be shorter, and nothing in the product changes.
  The guide tells Windows writers to retry on error 5 or 32 only, about every 10 ms for a few seconds, then fail loudly; waiting is safe because the old token stays whole and accepted until a client presents the new one (ADR 0046), and `ExternalTokenTests` pins per OS what holds: the rename lands while the client holds the file on Linux and macOS, and on Windows it fails, the old token stays whole, and it lands once the client's read ends.
- `LiveHistoryBoundsTests` reads the file sharing write, as a reader beside a live writer must on Windows.
- `RestartTokenTests` keeps "some call failed in each restart" in embedded mode, on every OS.
  In External mode, where the fixture restarts the standalone sidecar inside `CrashAsync`, it asserts instead that calls started after the client logged the sidecar gone (`ExternalSidecarLost`) and before it counted the next launch.
  Its claim is unchanged: every failure is unreachable (`Unknown`) and none is refused `Unauthenticated`.
  In External mode on Windows such a call may wait out the slow refusal and reach the next sidecar at the same address, with the token from the same file [inferred: no call failed in 6 of 6 runs, #172]; ADR 0046 already scoped "an endpoint of its own" to embedded launches.
  Mutations [observed: macOS, #174]: with no call made, embedded mode fails "0 failed"; with the lost record's name misspelt, External mode fails "0 calls started after the client saw it go"; ADR 0045's mutation 2, a fixed socket name (the earlier socket removed first), is red in 3 of 3, 24 `Unauthenticated` each.
- `build/release-test.sh` gives jq its input on stdin, and builds its packages with `zip`, or with `7z` where there is no zip, and says so when neither is on PATH.
  On macos-26 the jobs' `bash` is `/bin/bash` 3.2, where the script's empty array was unbound under `set -u`, a `case` inside `$(...)` did not parse, and the rehearsal guard's `*[A-Z]*` matched lowercase; they are rewritten for 3.2, `*[[:upper:]]*` for the last [observed: this PR's first macOS `contract` run, reproduced and then green with `/bin/bash` 3.2 on macOS].
- The first win-x64 run of this PR found two more, both test-side [observed: #174's first CI run]:
  the fake's `CompleteOfflineSync` read the Sent log's index after dispatching the event, so a history download the event released could land first and tie it (`HistoryTests` "downloaded at 498, before the offline sync completed at 498"); it now reads the index before the event, and `TestCompleteOfflineSyncReturnsTheIndexFromBeforeTheEvent`, whose handler sends at once, failed before the change.
  And `TestConcurrentRecordsNeverLetAReaderSkipASeq` hit its 30 s bound after 37 of 40 commits on the runner's loaded disk (ADR 0040); the bound guards against a hang, so the test takes 2 minutes of its own.
  On an idle Windows desktop a commit took p50 about 2 ms and at most 11 ms [observed: #174, ADR 0040]; whether the runner's `RUNNER_TEMP` disk is faster than its `TEMP` is [unknown], so the go job's temporary directory stays as it is.
- AGENTS.md states the host prerequisites: on every OS jq, zip or 7z and unzip for the release scripts' tests; on Windows also Git Bash, a C compiler for `go test -race`, and the Visual Studio Build Tools C++ workload for Native AOT.

## Consequences

- A change that breaks macOS or Windows fails its pull request, instead of waiting for the next platform pass.
- Each pull request runs twelve core jobs instead of four, eight of them on the slower macOS and Windows runners, which may queue longer [observed: GitHub's macOS arm64 capacity annotation on #174's jobs] and take concurrent job slots from every other run [inferred].
- One win-x64 `external` run hung after a crash test restarted the standalone sidecar, cause unknown: issue #176. The fixture now writes the standalone sidecar's stderr to its log when it stops.
