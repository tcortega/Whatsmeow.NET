# 0058. Fresh JIT consumers on every target RID

* Status: Accepted
* Date: 2026-11-05
* Evidence: #52's install criterion, the consumer mutations below, and [GitHub Actions run 42092473537](https://github.com/tcortega/Whatsmeow.NET/actions/runs/42092473537) at 835ac8d
* Amends: ARCHITECTURE §11, ADR 0023 (install coverage), ADR 0056 (shared host detection and reporting)

## Context

The release readiness criterion requires fresh local-feed consumers on all eight target RIDs, with portable and RID-specific JIT output, Native AOT, and a report identifying native or emulated execution [observed: #52].
Before this change, AotSmoke ran on all eight RIDs, while JIT consumers ran on win-x64, linux-x64, linux-musl-x64 and osx-arm64 [observed: `packaging.yml` and `build/ci.sh` at 32cde42].
The existing consumer starts the sidecar, calls GetInfo, checks its version and awaits disposal [observed: `build/package-smoke/Program.cs`].

The SDK resolves Native AOT publishing to a concrete RuntimeIdentifier and errors if none is resolved [observed: SDK 10.0.403, `Microsoft.NET.RuntimeIdentifierInference.targets:106-146,266-268`].
Portable Native AOT is therefore not a separate portable output; the report names it not applicable.

## Decision

Extend the existing install jobs with linux-arm64, osx-x64 and win-arm64, on the same native runner labels already used by AotSmoke.
The package job keeps its win-x64 install.
Both Linux install jobs run Alpine consumers in the SDK image of their architecture, using the existing pinned images.
The offline source build selects that image too, since it executes the runner's mounted Go toolchain.
Keep the downloaded feed's source-archive path in POSIX form too: GNU tar interpreted the Windows ARM64 runner's raw `C:` drive prefix as a remote hostname [observed: first matrix run 42088938950, job 127468327753].
Every added native host runs the full install checks, including package metadata and source checks.
The win-x86 missing-binary check remains on win-x64 only.

Reuse the existing console consumer, temporary project, empty package cache and NuGet source mapping.
On all eight RIDs, run portable build/run, portable publish, RID-specific framework-dependent publish and RID-specific self-contained publish.
The six non-musl RIDs also retain both single-file checks.
No consumer receives an explicit sidecar path, except the existing source-archive check that intentionally runs the source-built binary.

Share host detection between JIT and AOT: use `RUNNER_OS` and `RUNNER_ARCH` when set, otherwise uname.
Do not derive the requested RID from Go's process architecture.
The JIT consumer checks its runtime RID, requires process and OS architecture to agree, and requires dynamic-code support.
It verifies the expected native asset before starting: `runtimes/<rid>/native/` for portable output (musl uses the corresponding linux asset), or the flat binary for RID-specific output.
A flat binary in portable output fails, since it could conceal a broken portable lookup.

Each install form writes its RID, JIT form, result, host and feed version to the job summary after the consumer exits.
Failures say execution is unconfirmed.
AOT keeps its existing per-RID summary, adding the feed version and explicit portable-AOT limitation.
Successful native execution requires both host selection and the consumer's checks.
PR CI stays Linux only; main, manual CI and releases run the full matrix.

## Measurements and limits

All 19 jobs passed at 835ac8d, with feed `0.0.0-alpha.0.69`: all four required JIT forms on each of eight RIDs, both additional single-file forms on each of six non-musl RIDs, and all eight Native AOT runs [observed: run 42092473537, each package/install/aot job's summary].
The eight target RIDs ran natively; both musl RIDs ran in the Alpine container on a Linux runner of the matching architecture [observed: the same summaries and runtime identity checks].
The Windows ARM64 full install also built all six source targets and passed the final GetInfo check with the normalized feed path [observed: job 127477535049].

A portable JIT consumer on macOS ARM64 reports `osx-arm64`, process `Arm64`, OS `Arm64`; its RID-specific self-contained x64 publish under Rosetta reports `osx-x64`, process `X64`, OS `Arm64`, and fails the native check [observed: local consumer runs, 2026-11-05].
Removing either the portable runtime asset or the flat RID-specific binary fails the consumer; restoring it passes [observed: local macOS ARM64 mutation runs, 2026-11-05].
Adding a flat sidecar to portable output fails the layout assertion [observed: same mutations].
The requested wrong architecture fails independently of the asset checks [observed: same mutations].
All four JIT forms pass for linux-musl-arm64 in Docker Desktop's aarch64 Linux VM, reporting runtime `linux-musl-arm64`, process `Arm64`, OS `Arm64` [observed: local Alpine consumer runs, 2026-11-05].
Selecting the x64 Alpine SDK image for that ARM64 check reports `linux-musl-x64` and fails the requested-RID assertion, even though the emulated process reports both process and OS architecture as `X64` [observed: local image-selection mutation, 2026-11-05].

These checks prove installation from the tested local feed, not publication or signing.
#52 remains open for its other release criteria and for installation from nuget.org after the owner publishes.
