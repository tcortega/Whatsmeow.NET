# 0059. Install the release from nuget.org on every target RID

* Status: Accepted
* Date: 2026-11-06
* Evidence: #52's nuget.org install criterion, the local runs and mutations below
* Amends: ARCHITECTURE §11, ADR 0050 (a fifth release job), ADR 0056 and ADR 0058 (where their jobs live)

## Context

#52 closes only when the same install check passes on every target RID against nuget.org, after the owner pushes the tag and approves the publish [observed: #52].
`build/ci.sh install` and `aot` restored Whatsmeow.* from a local feed directory only, and release.yml ran them before the publish only [observed: `build/ci.sh` and `release.yml` at 6ec1520].
No Whatsmeow package is on nuget.org yet: the flat container answers 404 for all eight ids [observed: 2026-11-06].

nuget.org adds a repository signature to the packages it serves, and restore extracts it as `.signature.p7s` at the package root.
Every one of the 851 package versions restored from `https://api.nuget.org/v3/index.json` in a local packages folder has that file [observed: `.nupkg.metadata` sources in `~/.nuget/packages`, 2026-11-06].
Four packages without an author signature (Vogen 8.0.7, MinVer 8.0.0, QRCoder 1.8.1, Serilog 4.3.1) report `Signature type: Repository` [observed: `dotnet nuget verify --all`, SDK 10.0.403].
Ours carry no author signature (ADR 0007), so nuget.org signing them the same way is [inferred]; the first publish decides.
`packaged_files` demands each native package's exact extracted files, so it would fail on the served packages unless it expects that file.

## Decision

`build/ci.sh install nuget.org:<version>` and `aot nuget.org:<version> <rid>` check a published release against nuget.org; a directory argument keeps the feed checks, which now also compare the source archive's bytes.

The checker revision and the release revision are separate, so a fault in the checker is fixed in a later commit and run against the same tag:
* The version must be `<major>.<minor>.<patch>[-<pre-release>]`, and the tag `v<version>` must exist and be an ancestor of HEAD, checked before anything is read from it: the checker is the release or a later commit on its line.
* Every release input comes from the tag's tree, extracted with `git archive` into the consumer's directory: the consumer's `global.json` and `build/package-smoke/Program.cs`, AotSmoke and the `dotnet/Directory.*.props` it reads, the notice and README files the package checks compare, `build/release.sh ids`, the release targets (the release's `tools/check-licenses -targets`), and the Go toolchain `source_build` must use.
* The SDK and tools installed are the release's: `bash build/ci.sh release-pins v<version>` writes the tag's `mise.toml` and `global.json` into the checkout before they are installed, and the startup Go check reads the tag's `go.mod` pins; `GOTOOLCHAIN` stays `local`, so no toolchain is downloaded.
A checkout whose pins differ from the release's fails with that recovery named.
* `nuget-install.yml` (`workflow_dispatch`, a required tag, `contents: read`, no environment or secret) runs `install.yml` with the tag from the revision it is dispatched on; release.yml's `nuget-install` job runs it after `publish`, on a tag push only, where the checker is the release.

What the install job checks in this mode:
* `nuget.config` has nuget.org alone; restore uses an empty packages folder and, in both modes, an empty HTTP cache.
* Exactly the release's eight ids are restored, each at exactly the version with its own id and version in its nuspec, each with `.nupkg.metadata` naming `https://api.nuget.org/v3/index.json` as its source, and no other Whatsmeow package; they are copied into the consumer's own feed directory, so every `packaged_*` check reads the bytes nuget.org served.
* The Corresponding Source comes from the release's GitHub asset, which the publish job uploads before any push (ADR 0050).
Its files outside `sidecar/vendor` must equal the tag commit's own `git archive`: the same entries and kinds, the same link targets (never followed), the same executable bits outside Windows, whose checkouts do not keep them, and the same bytes.
The vendored modules stay checked by `source_build` (built from them alone) and `source_corresponds` (the packaged binary's module versions).
The GetInfo version check is stamped from the version, so it is no source identity.
* Each native package must also hold `.signature.p7s`.
That is a presence check of the served file, not a cryptographic verification: `dotnet nuget verify` would add online chain and revocation checks on fourteen runners for little over the origin check [inferred].

The aot job checks the tag and its pins, and publishes and runs AotSmoke from nuget.org alone; the package set, origin, nuspec, signature file and source checks are the install job's.

A setup that fails (a restore, an AOT publish) gets one row per RID in the step summary; for nuget.org it adds restore's `NU1101`/`NU1102` lines naming a Whatsmeow package, which say nuget.org did not serve it when restore ran, not why.
A version nuget.org does not serve yet fails `nuget-install`; re-running its failed jobs retries, and nothing waits or sleeps for it.

The install and aot jobs live in `install.yml`, one matrix for both uses: packaging.yml calls it for its artifact, release.yml and `nuget-install.yml` with a tag.
win-x64 joins the install matrix, so the package job no longer installs in CI; locally `build/ci.sh package` still installs and publishes AotSmoke on the host.
A rehearsal prints that it would run, since nothing is published.
`fetch-depth: 0` stays in tag mode: the ancestry check and the extraction need the tag and its history; a shallow checkout was not measured.

## Measurements and limits

All local, osx-arm64, nothing published; tags only in throwaway clones, the stand-in fixture and its results in #149's comments.
* Feed mode: `build/ci.sh package` at e8894ee passed every check, the source archive byte for byte the commit's [observed: `0.0.0-alpha.0.80`].
* nuget.org mode against a stand-in (a clone whose config maps `Whatsmeow.*` to a folder of a feed's packages, each given a `.signature.p7s`, the expected origin that folder, the source asset a `file://` URL): every check passed with the checker equal to the release (feed at e8894ee) and with a later checker (release cd30d55, checker the branch tip), install and AOT [observed].
* It fails on the matching check without the signature file, without the source asset, with the tag on a side commit, with other SDK and tool pins (and passes once `release-pins` restored them), with one changed byte in the asset, with the asset's `CLAUDE.md` link retargeted outside the tree at a copy of the same bytes, with a signed local source left expecting the nuget.org origin, and with the release's ids missing one package; a later checker with other `go.mod` toolchain pins passes [observed].
* Feed mode with one changed byte in the archive fails on bytes [observed].
* Mutants each caught: no signature expectation, no copy into the consumer's feed, no source download, `Whatsmeow.*` from the local feed, no ancestry check, no exact package set, no origin check, no byte comparison, links followed, no pin check, the checker's own Go pins in this mode, no version validation [observed].
* `install nuget.org:9.9.9-probe.1` and `aot nuget.org:9.9.9-probe.1 osx-arm64` stop at restore with `NU1101 ... in source(s): nuget.org`, and the step row says so [observed]: the path reaches nuget.org alone and fails closed.
`nuget.org:1.0.0^{tree}` and `release-pins` with it or an untagged version are refused [observed].
* Before a publish nothing can show the nuget.org path passing.
#52 closes on the first real `nuget-install` run with every row passing.
