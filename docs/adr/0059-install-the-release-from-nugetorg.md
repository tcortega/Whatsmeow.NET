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

`build/ci.sh install nuget.org:<version>` and `aot nuget.org:<version> <rid>` run the same checks against nuget.org; a directory argument keeps the feed checks unchanged.
* The checkout must be the release tag `v<version>`, since the source checks compare the published archive with HEAD.
* `nuget.config` has nuget.org alone; restore uses an empty packages folder and, in both modes, an empty HTTP cache.
* After the restore, exactly the eight packages `build/release.sh ids` prints must be there, each at exactly the version with its own id and version in its nuspec, and no other Whatsmeow package; they are copied into the consumer's own feed directory, so every `packaged_*` check reads the bytes nuget.org served.
* The Corresponding Source comes from the release's GitHub asset, which the publish job uploads before any push (ADR 0050).
* Each native package must also hold `.signature.p7s`.
That is a presence check of the served file, not a cryptographic verification: `dotnet nuget verify` would add online chain and revocation checks on fourteen runners for little over the source mapping, which already restricts every package to nuget.org [inferred].

The install and aot jobs move from packaging.yml into `install.yml`, one matrix for both uses.
packaging.yml calls it for its artifact; release.yml's new `nuget-install` job calls it with the tag after `publish`, on a tag push only.
win-x64 joins the install matrix, so the package job no longer installs in CI; locally `build/ci.sh package` still installs and publishes AotSmoke on the host.
A version nuget.org does not serve yet fails `nuget-install`; re-running its failed jobs retries, and nothing waits or sleeps for it.
A rehearsal prints that it would run, since nothing is published.

## Measurements and limits

* The feed path is unchanged: a local `build/ci.sh package` on osx-arm64 at `0.0.0-alpha.0.76` passed every check, the six JIT forms and AotSmoke [observed: 2026-11-06].
* `install nuget.org:9.9.9-probe.1` and `aot nuget.org:9.9.9-probe.1 osx-arm64`, in a clone with that tag made locally, stop at restore with `NU1101 ... in source(s): nuget.org` [observed]: the path reaches nuget.org alone and fails closed.
An untagged version, an empty one and a tag on another commit fail the tag check [observed].
* Against a stand-in, a clone whose nuget.org-mode config maps `Whatsmeow.*` to a folder of that feed's packages, each given a `.signature.p7s`, and the rest to nuget.org, with the source asset at a `file://` URL: every check passed, on exactly the eight served packages [observed].
Without the signature file, without the source asset, with the tag on another commit, and with `release.sh ids` missing a package, it fails on the matching check [observed].
Mutants dropping the signature expectation, the copy into the consumer's feed, the source download, the tag check, the exact package set, or mapping `Whatsmeow.*` to the local feed in this mode were each caught [observed].
* Before a publish nothing can show the nuget.org path passing.
#52 closes on the first real `nuget-install` run with every row passing.
