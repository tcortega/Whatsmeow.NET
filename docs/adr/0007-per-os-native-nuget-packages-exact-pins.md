# 0007. Packaging as per-OS native NuGet packages with exact-version pins

- Status: Accepted
- Date: 2026-10-31
- Evidence: [ARCHITECTURE §14](../ARCHITECTURE.md#spike-results-2026-10-31) question 6, and the Mark-of-the-Web result in question 2

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

Question 2, Windows [observed: §14 Spike results, Question 2]: the `protoc.exe` that `dotnet restore` pulled from nuget.org has no `Zone.Identifier` stream, and the unsigned spike `sidecar.exe` (Authenticode `NotSigned`) starts.

## Decision

Phase 0 verdict:

- Question 6: go without Developer ID signing, notarization or Authenticode, because the 21.9-23.3 MB CGO-free binaries ran on Alpine and Debian, and binaries restored from nuget.org carry no quarantine attribute or Mark-of-the-Web.

Design:

- The binaries ship in `Whatsmeow.Sidecar.Native.{win,linux,osx}` under `runtimes/{rid}/native/whatsmeow-net[.exe]`.
- `Whatsmeow.Client` depends on them with exact versions (`[x.y.z]`); no `runtime.json` pointer package ([ARCHITECTURE §8.1](../ARCHITECTURE.md#81-packages)).
- A musl RID resolves to the same binary as its glibc RID, which the Alpine run above supports.
- The supervisor locates the binary by option or `WHATSMEOW_NET_SIDECAR_PATH`, then `AppContext.BaseDirectory`, then `runtimes/<rid>/native/`, never `Assembly.Location`, and calls `File.SetUnixFileMode(+x)` before every start ([ARCHITECTURE §8.5](../ARCHITECTURE.md#85-sidecar-supervision)).
- One `vX.Y.Z` tag drives GoReleaser and MinVer, so the versions are always equal, and an embedded sidecar must match the client version (invariant 12).
- No Developer ID signing, notarization or Authenticode ([ARCHITECTURE §12](../ARCHITECTURE.md#12-release-and-upstream-tracking)).

Why no signing, with the evidence level of each step:

- A binary restored from nuget.org carries no quarantine attribute, and an ad-hoc signed one runs even though `spctl` rejects it [observed: Question 6] [inferred: Gatekeeper assesses only quarantined launches, which matches what we saw].
- Apple Silicon needs arm64 code to be signed at all [inferred: Apple platform rule, not measured here], and the Go linker ad-hoc signs `darwin/arm64` itself [observed: Question 6].
- So no Authenticode signing is needed either [inferred: SmartScreen keys on Mark-of-the-Web, which matches what we saw].
- An app that embeds us and ships as a notarized `.app` signs its embedded binaries as part of its own release [inferred: standard macOS app signing, not our concern].

## Consequences

- Each native package holds two binaries of about 22-23 MB, well under the nuget.org cap [inferred: from the Question 6 sizes; package compression not measured].
- CI packs, restores from a local feed, and runs on win-x64, linux-x64 (glibc and Alpine) and osx-arm64 ([ARCHITECTURE §11](../ARCHITECTURE.md#11-testing-strategy)).
- If a delivery path that sets quarantine or Mark-of-the-Web becomes supported, the signing decision is revisited by a new ADR.
- `NOTICE` carries the MPL-2.0 source offer for whatsmeow at the pin.
- The licences of `go.mau.fi/libsignal` and `go.mau.fi/util` at the pin are [unknown] ([RESEARCH.md](../RESEARCH.md#what-still-needs-proof-before-you-trust-it) "What still needs proof", item 1); shipping the binary in a NuGet package is distribution, so they must be resolved before the first public release.
