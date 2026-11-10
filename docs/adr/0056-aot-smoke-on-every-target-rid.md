# 0056. AotSmoke with Native AOT from the local feed on every target RID, each on a native runner

- Status: Accepted
- Amended by: [0058](0058-jit-install-on-every-target-rid.md) (JIT install coverage and native execution reporting on every target RID), [0059](0059-install-the-release-from-nugetorg.md) (the aot jobs live in `install.yml`, and run against nuget.org after a publish), [0066](0066-the-core-ci-jobs-on-every-desktop-os.md) (pull requests run the core jobs on osx-arm64 and win-x64 too)
- Date: 2026-11-04
- Evidence: issue #44, its plan review, the mutations in its PR body, GitHub Actions run 41848735418, a manual run of `ci.yml` on the branch with two deliberately emulated `aot` jobs added for the measurement, and the docs cited below, read on 2026-11-04
- Amends: ARCHITECTURE §11 (the AOT row; packaging runs on main too), [ADR 0050](0050-release-workflow.md) (what `packaging.yml` runs, and when `ci.yml` calls it)

## Context

Native AOT and trimming are goals (§1), and `tests/AotSmoke` with zero warnings is the gate (§11).
It ran only in the `dotnet` job, against the projects, on the CI operating systems; the Phase 4 exit names every target RID (§15), Native AOT links with each platform's own toolchain, and ADR 0034 left the hosting path to this issue.

Measured:

- GitHub hosts a standard runner for each non-musl RID: `ubuntu-26.04` (x64), `ubuntu-26.04-arm`, `windows-2025` (x64), `windows-11-arm`, `macos-26` (arm64), and for osx-x64 `macos-26-intel` and `macos-15-intel` [observed: docs.github.com "GitHub-hosted runners" reference, and the `actions/runner-images` README, 2026-11-04].
  `macos-26-intel` is "Standard macOS runner for x64" [observed: github.blog changelog 2026-02-26, macOS 26 generally available].
  GitHub publishes no retirement date for either Intel image [observed: the `actions/runner-images` README names one for macOS 14 only]; third parties quote a GitHub notice that Intel ends with the macOS 15 image in fall 2027 [inferred: LedFx PR #1508, WordPress changeset 61241], which `macos-26-intel` coming later puts in doubt.
- No GitHub runner runs Alpine (musl) [observed: the same reference lists none].
- `windows-11-arm` has Visual Studio 2026 with `VC.Tools.ARM64` and `VC.Tools.x86.x64`, Git Bash, and SDK 10.0.403; the Ubuntu 26.04 images, x64 and arm64, have clang 20 to 22 and Docker 29 [observed: `actions/runner-images` `Windows11-VS2026-Arm64-Readme.md`, `Ubuntu2604-Readme.md`, `Ubuntu2604-Arm64-Readme.md`].
- Native AOT on Alpine needs `clang build-base zlib-dev` [observed: learn.microsoft.com "Native AOT deployment overview", Alpine prerequisites]; with those, and no `lld`, the `linux-musl-arm64` publish links and runs in `mcr.microsoft.com/dotnet/sdk:10.0.403-alpine3.24-arm64v8` [observed: Docker Desktop on macOS arm64, 2026-11-04]; so do both musl RIDs on GitHub runners [observed: run 41848735418, jobs 126647441108 and 126647441126].
  That image's digest is `sha256:bf500481…9bff`, and the `amd64` one `build/ci.sh` already pins is unchanged [observed: MCR manifest].
- Under Native AOT, `RuntimeInformation.RuntimeIdentifier` is the RID the app was published for: `osx-arm64`, `osx-x64` and `linux-musl-arm64` locally [observed: AotSmoke's first line], and each of the eight on its runner [observed: run 41848735418].
- An `osx-x64` app under Rosetta reports `ProcessArchitecture` `X64` and `OSArchitecture` `Arm64` [observed: an osx-x64 publish run on macOS 26 arm64 locally, and on `macos-26` in run 41848735418, job 126647441102].
  A `win-x64` app under Prism on `windows-11-arm` reports `X64` and `Arm64` the same way, and `win-x64` publishes there [observed: run 41848735418, job 126647441038].
  So `ProcessArchitecture` differing from `OSArchitecture` gives both emulators away.
- `jdx/mise-action` with `install_args: go` runs `mise install go` only, and on `windows-11-arm` downloads `mise-v2026.10.25-windows-arm64.zip` and installs Go 1.27.3 [observed: run 41848735418, job 126647440990].
- An AOT publish puts the packaged binary beside the app, and a `linux-musl-*` publish gets the `linux-<arch>` one through the RID graph [observed: ADR 0023, and `build/ci.sh aot`'s check for the binary beside the app on every RID of run 41848735418].

## Decision

- **One AotSmoke, two sources.** `tests/AotSmoke` takes `<version> <rid> [<whatsmeow-net>]` and exits 0 only if:
  - it runs as the RID it was published for, and `ProcessArchitecture` equals `OSArchitecture`, so a Rosetta or Prism run fails;
  - `RuntimeFeature.IsDynamicCodeSupported` is false, so a publish without Native AOT fails; `build/ci.sh aot` also fails when `AotSmoke.dll` is in the output;
  - without DI, it starts the sidecar, `GetInfo` reports the version and protocol 1, and a created session lists as `Created`;
  - with the generic host, the options bind from configuration (a relative `DatabasePath` into the content root, a `TimeSpan`, the sidecar path when given, and on Windows the `Transport` enum as `NamedPipe`, its only value besides the default, which is Windows only, so elsewhere no enum is bound), a declared session resolves as its keyed `IWhatsAppClient` and lists as `Created`, a handler is registered so `AddHandler`'s generic code is compiled, and `GetInfo` reports the version;
  - an upstream WhatsApp message round-trips.
  In the repository it references the projects and is given a sidecar built from source (`build/ci.sh dotnet`, every pull request).
  `build/ci.sh aot <feed> <rid>` copies it out of the repository with `AotSmokeFromFeed`, which swaps the project references for the feed's `Whatsmeow.Client`, `Whatsmeow.Extensions.Hosting` and `Whatsmeow.Testing`, versioned by a `Directory.Packages.props` it writes beside it, restores into an empty packages folder, publishes for the RID with `-warnaserror`, checks the packaged binary is beside the app, and runs it with no sidecar path.
  One project, so the rooted assemblies are listed once.
- **Each RID on its own native runner**, in `packaging.yml`'s `aot` job, after the package job, on the feed it built: `linux-x64` `ubuntu-26.04`, `linux-arm64` `ubuntu-26.04-arm`, `osx-x64` `macos-26-intel`, `osx-arm64` `macos-26`, `win-x64` `windows-2025`, `win-arm64` `windows-11-arm`.
- **musl in the Alpine SDK container of its architecture, on the Linux runner of that architecture**: `linux-musl-x64` on `ubuntu-26.04`, `linux-musl-arm64` on `ubuntu-26.04-arm`.
  The container shares the host's CPU and kernel, so this counts as native.
- **`build/ci.sh aot` refuses a RID whose OS or architecture is not the host's**, read from `RUNNER_OS` and `RUNNER_ARCH` when set and from `uname` otherwise: docker would emulate a musl RID where the app cannot tell, and the app's own check is the second line for the rest.
  `build/ci.sh package` runs it for the host's RID outside CI only, since CI has the `aot` jobs.
- **Where it runs.** `ci.yml` calls `packaging.yml` on every event but a pull request: on main and on demand; `release.yml` calls it on every release and rehearsal, and the publish job needs it, so a warning on any RID stops a release.
  Pull requests keep linux-x64 only, where the `dotnet` job's AotSmoke still fails on a new warning; a branch proves the matrix with a manual run of `ci.yml` on it.
- **The summary.** Each `aot` job writes one line to the job summary: the RID, zero trim and AOT warnings, and where it ran, `(native, Alpine container)` for musl.
  Every target RID has a native runner today, so none is listed as not run; the change that drops one, such as `osx-x64` once GitHub retires the Intel macOS images, replaces its entry with a summary line saying it is not run.
- **No `lld`, no musl toolchain on the host**: the container installs `clang build-base zlib-dev` at each run, from Alpine's repositories, unpinned like the NuGet restore around it.

## Consequences

- Every merge to main runs the package job, the two install jobs and eight `aot` jobs: about 25 minutes of runner time for the first three and 18 for the `aot` jobs, the longest 4 minutes 14 seconds (`osx-x64`) after the package job's 14 minutes [observed: run 41848735418]; none on a pull request.
- Adding a release target means a new `aot` matrix entry; nothing checks the matrix against `tools/check-licenses -targets` (ADR 0050 already makes a new target a reviewed change in `build/release.sh`).
- A new SDK pin updates both Alpine image digests in `build/ci.sh`.
- `macos-26-intel` is the osx-x64 runner; its retirement date is [unknown], and the weekly bump's CI run on main is where a removed label first shows.
