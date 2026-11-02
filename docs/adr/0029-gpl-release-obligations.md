# 0029. The GPL release obligations: generated notices, the GPL files in the native packages, and a source archive that builds offline

- Status: Accepted
- Date: 2026-11-02
- Evidence: issue #47, its plan review, `build/ci.sh package` and `install` locally on osx-arm64 and in CI, and the sources named below
- Amends: [ADR 0016](0016-sidecar-binary-gpl-3-corresponding-source-per-release.md) (its Phase 4 obligations, now built), [ADR 0023](0023-native-packages-build-pin-and-find.md) (what each native package holds)

## Context

[ADR 0016](0016-sidecar-binary-gpl-3-corresponding-source-per-release.md) conveys each `whatsmeow-net` binary under GPL-3.0-only and lists what must ship from the first release: the Corresponding Source on every GitHub release, `COPYING`, `NOTICE` and `THIRD-PARTY-NOTICES.txt` in every native package, licence metadata on every package, and READMEs that say what the native packages contain.
Issue #47 builds them, before the release workflow (#46) exists.

Measured while building it:

- `go mod vendor` in a `git archive HEAD` copy of 86718fd gives a 151 MB `sidecar/vendor`; the archive with it is 32,033,316 bytes gzipped [observed: `du -sh`, `ls -l`].
  `build/ci.sh package` prints the exact size on every run.
- `go mod vendor` copies the licence files of every vendored module, libsignal's `LICENSE` included [observed: the vendor tree].
- From that copy, with `GOPROXY=off`, `GOSUMDB=off`, `GOTOOLCHAIN=local` and an empty `GOMODCACHE`, `go build` succeeds for darwin/arm64, linux/amd64 and windows/arm64; the go command uses `vendor/` without a flag [observed].
- A binary built from the archive records `mod github.com/tcortega/Whatsmeow.NET/sidecar (devel)` and no `vcs.*` build settings, since the archive has no `.git` [observed: `go version -m`], so it is not byte-identical to the packaged one; it links the same module versions (`dep` lines), which `build/ci.sh install` checks.
- `bin/go` and `pkg/tool/linux_amd64/compile` of the go.dev `go1.27.3.linux-amd64.tar.gz` (sha256 `65efbc80...9b40`, as go.dev lists it) are statically linked [observed: `file`], so a GOROOT mounted read-only into the pinned Alpine SDK image builds there [observed: Docker on osx-arm64, linux/amd64 emulated].
  That mise installs that same archive on the Linux runners is [inferred: mise's Go backend downloads the go.dev archives].
- In `docker run --network none` the container has only `lo` in `/sys/class/net`; without the flag it also has `eth0` [observed].
- The gnu.org GPL-3.0 text, `https://www.gnu.org/licenses/gpl-3.0.txt`, has sha256 `3972dc9744f6499f0f9b2dbf76696f2ae7ad8af9b23dde66d6af86c9dfb36986` [observed 2026-11-02].
  libsignal's `LICENSE` is not that text: its appendix names libsignal-protocol-go and RadicalApp LLC, and its URLs are `http` [observed: `diff`].
- None of the allowlisted licence files has a CR; two (`github.com/vektah/gqlparser/v2` `LICENSE` and `validator/imported/LICENSE`) end without a newline [observed].
- Immutable releases do not stop a release from being deleted: "If you delete the immutable release, you can delete the tag, but you cannot reuse the same tag name" [observed: GitHub docs, "Immutable releases"].
  Once a release is published as immutable its assets are "protected from modification or deletion", and GitHub recommends attaching every asset to a draft first [observed: the same page].
  A `GITHUB_TOKEN` that can create a release can also delete one, since both need `contents: write` [inferred: GitHub's permission model; not measured].

## Decision

- **Notices are generated.** `go -C tools run ./check-licenses -write` writes `sidecar/THIRD-PARTY-NOTICES.txt`: every file `sidecar/licenses.allow` lists, verbatim and in its order, each under a header naming the module, its version (`go env GOVERSION` for std), the file and the reviewed licence.
  It writes only when the allowlist matches what links, so the file never holds an unreviewed licence text.
  The default run, which `build/ci.sh go` makes, fails when the file is missing or differs; a bump that changes a licence file fails twice, first in the allowlist, then in the notices, until a reviewer edits the allowlist and regenerates.
  The file is committed, marked `-text linguist-generated`, and never edited by hand.
- **The GPL files.** `sidecar/COPYING` is the gnu.org GPL-3.0 text, kept beside the sidecar so the root stays the MPL-2.0 `LICENSE` alone.
  Each `Whatsmeow.Sidecar.Native.*` package carries `COPYING`, the root `NOTICE` and `THIRD-PARTY-NOTICES.txt` at its root, and `dotnet/src/Whatsmeow.Sidecar.Native.md` as its `README.md`; all four come from one conditional block in `dotnet/src/Directory.Build.props`.
  `NOTICE` and the README name the release by rule (the tag is `v` followed by the package version), not by a version written in at pack time.
  The README keeps ADR 0016's `[inferred]` and `[unknown]` tags.
- **Licence expressions.** `PackageLicenseExpression` is `MPL-2.0` for everything under `dotnet/src`, and `GPL-3.0-only` for the native packages.
- **The source archive.** `build/ci.sh package` writes `whatsmeow-net_<version>_source.tar.gz` into the feed beside the packages: `git archive HEAD` under the prefix `whatsmeow-net_<version>/`, plus the `go mod vendor` output of `sidecar/`, made in a temporary copy and gzipped with `-n`.
  It rides the `native-packages` artifact, so the release workflow (#46) attaches the archive the install checks built from; #46 creates the release as a draft, uploads every asset, then publishes, since an immutable release takes no new assets.
- **The proof**, in `build/ci.sh install`, on every OS that installs the feed:
  - The archive holds exactly the files of `HEAD` plus `sidecar/vendor/`, with its `modules.txt`.
  - It builds for every release target with the release flags, `GOPROXY=off`, `-mod=vendor`, an empty module cache and an empty build cache.
    On Linux CI the build runs in `docker run --network none` in the pinned Alpine SDK image, with mise's GOROOT mounted read-only; it fails when the container has any interface but `lo`, or when its Go is not the toolchain `sidecar/go.mod` names, so "no network" is [observed] there.
    On macOS and Windows the host keeps its network, and that the go command cannot download is [inferred: `GOPROXY=off` and `-mod=vendor` in the go command documentation].
  - Each binary built from it links the module versions of the packaged binary, as `go version -m` reads both.
  - The host's binary, given to the consumer through `WHATSMEOW_NET_SIDECAR_PATH`, reports the release version through `GetInfo`.
  - Each native package's README and GPL files are byte-equal to the repository's, `COPYING` has the GPL-3.0 sha256, and every package in the feed declares its licence expression.
- **Releases are never deleted.** `tools/check-repo` rejects every line of a workflow or shell script that deletes a release or a release asset: `gh release delete` and `delete-asset`, Octokit `deleteRelease`, an action named for it, and a REST `DELETE` on `/releases` through `gh api`, curl or a request route; comments too.
  GitHub cannot enforce this for us (above), so this guards our own code only.

## Owner items

For Antonio, batched with #46 and #52; nothing here assumes they are done:

- Turn on immutable releases for the repository, so a published release's assets and tag cannot change.
- Add a tag ruleset on `v*` that blocks deletion and force updates.
- Never delete a release by hand while its packages are on nuget.org.

## Open legal questions for #52

Legal review is still [unknown] (ADR 0016); none of these is answered here:

- The archive holds no Go toolchain or standard library source; whether GPLv3 §1's System Libraries and "generally available free programs" exclusions cover the statically linked standard library is [unknown].
- `go mod vendor` leaves out the vendored modules' tests and the files of packages that never link; that this is still the Corresponding Source, "the source code needed to generate, install, and ... run the object code", is [inferred: GPLv3 §1].
- A binary built from the archive is not byte-identical to the released one (above); whether that matters is [unknown].
- Whether naming the release by rule in `NOTICE` and the README is the "clear directions next to the object code" of GPLv3 §6(d) is [unknown].
- How long a GitHub release must stay up to keep the source "available for as long as needed" (GPLv3 §6(d)) is [unknown]; we never delete one.
- Whether the nuspec's repository URL and commit inform recipients of the .NET packages' Executable Form where the Source Code Form is, as MPL-2.0 §3.2(a) asks, is [unknown].
- Whether to publish README text that carries `[inferred]` and `[unknown]` tags at all is the owner's decision.

## Consequences

- A release built by #46 carries everything ADR 0016 lists from its first day, provided #46 attaches the archive from the `native-packages` artifact.
- Every package job builds the archive, about 32 MB, and every install job builds six binaries from it with cold caches; the CI cost is measured in the PR that lands this.
- A bump that adds a module or changes a licence file needs `go -C tools run ./check-licenses -write` after the allowlist edit; the notices diff shows the reviewer the new text.
- No MPL Exhibit B guard: nothing here attaches one, and ADR 0016 already forbids it (owner: never over-engineer).
- The .NET packages' READMEs, descriptions, tags and URLs are #51's; a Client README that mentions the GPL native dependencies is #51's too.
