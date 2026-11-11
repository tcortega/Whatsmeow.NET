# 0007. Packaging as per-OS native NuGet packages with exact-version pins

- Status: Accepted; amended by [0016](0016-sidecar-binary-gpl-3-corresponding-source-per-release.md), which settles the licences in the last two Consequences, and by [0023](0023-native-packages-build-pin-and-find.md), which builds the packages and measures them; its macOS and Windows evidence amended in place by [evidence 0045](../evidence/0045-macos-gatekeeper-2026-11-06.md) and [its Windows record](../evidence/0045-windows-smartscreen-2026-11-10.md) (#45), with the decision unchanged; and by [0069](0069-one-native-package.md), which puts all six binaries in one package, `Whatsmeow.Sidecar.Native`, since `Whatsmeow.Client` depends on all three and the cap never applied
- Date: 2026-10-31
- Evidence: [ARCHITECTURE §14](../ARCHITECTURE.md#spike-results-2026-10-31) question 6, the Mark-of-the-Web result in question 2, and, for macOS, [evidence 0045](../evidence/0045-macos-gatekeeper-2026-11-06.md) (one daily-use Mac, 2026-11-06), and for Windows [its Windows record](../evidence/0045-windows-smartscreen-2026-11-10.md) (one laptop, session 0, 2026-11-10)

## Context

Zero setup is a goal: `dotnet add package` must be enough, with the sidecar binary inside NuGet ([ARCHITECTURE §1](../ARCHITECTURE.md#1-goals-and-non-goals)).
WhatsBox's `runtime.json` pointer pattern only resolves when a RID is set at restore, so a portable `dotnet build`, `run` or test gets no binary [observed: [RESEARCH.md](../RESEARCH.md#per-os-native-packages-and-a-stdin-tether-keep-the-sidecar-honest) "Per-OS native packages", citing MS Learn].
nuget.org caps packages at about 250 MB (same section).
The execute bit is lost when a package is built on Windows and deployed to Linux, or copied through a Docker context (same section).

Question 6, Go 1.27.3, `CGO_ENABLED=0 -trimpath -ldflags "-s -w"` [observed: §14 Spike results, Question 6]:

- Sizes: linux-amd64 22.9 MB, linux-arm64 21.9 MB, darwin-arm64 22.4 MB, darwin-amd64 23.3 MB, windows-amd64 23.2 MB, windows-arm64 21.9 MB.
- The Linux binaries ran on `alpine:3` and `debian:stable-slim`, arm64 natively and amd64 under emulation.
- The Go linker ad-hoc signs `darwin/arm64`; `darwin/amd64` is unsigned; `spctl` rejects both.
- The macOS `protoc` that `dotnet restore` pulled from nuget.org has no `com.apple.quarantine`, is only ad-hoc signed, and runs.

Evidence 0045, macOS 26.5.2 on one daily-use Apple Silicon Mac, not a clean one, with the CI-built packages of `e5cf3b1` restored from a loopback HTTP feed, not nuget.org [observed: [evidence 0045](../evidence/0045-macos-gatekeeper-2026-11-06.md)]:

- Restore, portable build and `dotnet run`, portable and framework-dependent publishes (arm64), and self-contained and single-file publishes and their `cp -R` and `ditto` copies (arm64, and x64 under Rosetta [inferred]) set no `com.apple.quarantine`, and each started the sidecar (M, D1 to D9).
- A Gatekeeper scan with the sidecar's signing identifier appears in every ad-hoc arm64 launch, quarantined or not (attribution [inferred]).
  Of the ad-hoc sidecars, only one given a synthetic quarantine attribute raised a prompt; it was refused after the prompt was answered by an [unknown] actor and button, and it ran after an assessment of the same code (Q1a).
- This arm64 sidecar with its ad-hoc signature removed was refused at exec, AMFI logging "Attempt to execute completely unsigned code", with no prompt and no quarantine (S1).
- A quarantined, unsigned x64 sidecar (x86_64 on Apple silicon, Rosetta [inferred]), spawned by an unquarantined app after launches and an assessment of the same code earlier that day, raised a prompt, its process never printed its ready line, and the client gave up at its startup timeout before the prompt was answered (Q1b).
- S1, Q1a and Q1b are synthetic controls, not delivery paths.
  A quarantined app directory, a restore from nuget.org, a clean Mac, a native Intel desktop, and osx-x64 framework-dependent on that Mac were not measured; CI's native osx-x64 install and AOT runs passed ([ARCHITECTURE §11](../ARCHITECTURE.md#11-testing-strategy)), and their quarantine metadata and Gatekeeper state are [unknown].

Question 2, Windows [observed: §14 Spike results, Question 2]: the `protoc.exe` that `dotnet restore` pulled from nuget.org has no `Zone.Identifier` stream, and the unsigned spike `sidecar.exe` (Authenticode `NotSigned`) starts.

Evidence 0045's Windows record, Windows 11 Home 25H2 (build 26200) on one x64 laptop in daily use, driven over SSH with nobody at it, every case as a standard account in a session 0 batch logon, with the CI-built packages of `210097c` restored from a loopback HTTP feed, not nuget.org [observed: [the Windows record](../evidence/0045-windows-smartscreen-2026-11-10.md)]:

- The feed's nupkgs carried a browser-style Mark-of-the-Web, applied by the harness; the nupkg NuGet installed, the binaries it extracted, the portable build, the portable, framework-dependent, self-contained and single-file publishes, and `Copy-Item` and `robocopy` copies carried none, and each started the unsigned sidecar (M1 to M10, D1 to D8).
- The sidecar with a synthetic `Zone.Identifier` of zone 3, alone (Z1) or with every file of the app marked (Z2), started through the consumer's `CreateProcess` too, with no SmartScreen process, no CodeIntegrity, Defender or AppLocker event, and no measurable delay.
- Z1 and Z2 are synthetic controls, not delivery paths.
  An interactive desktop session (so the Explorer double-click, Z3), a restore from nuget.org, Smart App Control in enforcement (off on that laptop) and win-arm64 on a desktop were not measured.

## Decision

Phase 0 verdict:

- Question 6: go without Developer ID signing, notarization or Authenticode, because the 21.9-23.3 MB CGO-free binaries ran on Alpine and Debian, and binaries restored from nuget.org carry no quarantine attribute or Mark-of-the-Web.

Design:

- The binaries ship in `Whatsmeow.Sidecar.Native.{Linux,MacOS,Windows}` (named in [ADR 0023](0023-native-packages-build-pin-and-find.md)) under `runtimes/{rid}/native/whatsmeow-net[.exe]`.
- `Whatsmeow.Client` depends on them with exact versions (`[x.y.z]`); no `runtime.json` pointer package ([ARCHITECTURE §8.1](../ARCHITECTURE.md#81-packages)).
- A musl RID resolves to the same binary as its glibc RID, which the Alpine run above supports.
- The supervisor locates the binary by option or `WHATSMEOW_NET_SIDECAR_PATH`, then `AppContext.BaseDirectory`, then `runtimes/<rid>/native/`, never `Assembly.Location`, and calls `File.SetUnixFileMode(+x)` before every start ([ARCHITECTURE §8.5](../ARCHITECTURE.md#85-sidecar-supervision)).
- One `vX.Y.Z` tag drives MinVer, and `build/ci.sh` builds the binaries at MinVer's version, so the versions are always equal, and an embedded sidecar must match the client version (invariant 12; GoReleaser superseded by [ADR 0023](0023-native-packages-build-pin-and-find.md)).
- No Developer ID signing, notarization or Authenticode ([ARCHITECTURE §12](../ARCHITECTURE.md#12-release-and-upstream-tracking)).

Why no signing, with the evidence level of each step:

- The `protoc` that `dotnet restore` pulled from nuget.org carries no quarantine attribute, and runs although it is only ad-hoc signed and `spctl` rejects it [observed: Question 6].
  This was first explained as Gatekeeper assessing only quarantined launches [inferred]; on one daily-use Mac a scan with the sidecar's signing identifier appeared in every ad-hoc arm64 launch, quarantined or not, and of those sidecars only the quarantined one raised a prompt, while the measured restore, build, publish and copy paths set no quarantine and started it [observed: evidence 0045; attribution inferred].
  `whatsmeow-net` restored from nuget.org, a clean Mac and a native Intel desktop are [unknown].
- This arm64 sidecar must be signed at all to run on the one Apple Silicon Mac measured [observed: evidence 0045 S1; first inferred as an Apple platform rule, and beyond that Mac still inferred], and the Go linker ad-hoc signs `darwin/arm64` itself [observed: Question 6], which was enough there without quarantine [observed: evidence 0045 D].
- So no Authenticode signing is needed either: on one laptop the measured restore, build, publish and copy paths put no Mark-of-the-Web on the sidecar and started it, and a marked sidecar started by the app through `CreateProcess` in a session 0 batch logon ran with no SmartScreen check visible [observed: evidence 0045's Windows record, M, D, Z1, Z2].
  This was first explained as SmartScreen keying on Mark-of-the-Web [inferred]; that its app reputation check runs on shell launches, not on `CreateProcess`, is [inferred], and an interactive desktop session, a restore from nuget.org and Smart App Control in enforcement are [unknown].
- An app that embeds us and ships as a notarized `.app` signs its embedded binaries as part of its own release [inferred: standard macOS app signing, not our concern].

## Consequences

- Each native package holds two binaries of about 23.9-25.6 MB, and packs to about 17.9 MB (Linux), 18.3 MB (MacOS) and 18.1 MB (Windows), well under the nuget.org cap [observed: `build/ci.sh package`, which prints both, on windows-2025 in GitHub Actions run 41357262808 at commit 3369aad: binaries 23,920,800 to 25,563,136 bytes, packages 17,874,021, 18,316,525 and 18,067,996 bytes; ADR 0023].
  A binary is reproducible from its commit, but each commit embeds its own revision and version, so these numbers move a little from build to build; whether pack itself is byte-reproducible is [unknown] [observed: run 41351388993 at 5538341 packed 17,829,768, 18,269,500 and 18,021,617 bytes].
- CI packs, restores from a local feed, and runs on win-x64, linux-x64 (glibc and Alpine) and osx-arm64 ([ARCHITECTURE §11](../ARCHITECTURE.md#11-testing-strategy)).
- If a delivery path that sets quarantine or Mark-of-the-Web becomes supported, the signing decision is revisited by a new ADR.
- `NOTICE` carries the MPL-2.0 source offer for whatsmeow at the pin.
- The licences of `go.mau.fi/libsignal` and `go.mau.fi/util` at the pin are [unknown] ([RESEARCH.md](../RESEARCH.md#what-still-needs-proof-before-you-trust-it) "What still needs proof", item 1); shipping the binary in a NuGet package is distribution, so they must be resolved before the first public release.
