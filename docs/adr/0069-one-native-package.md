# 0069. One native package holds all six binaries

- Status: Accepted
- Date: 2026-11-11
- Evidence: the `1.0.0-rc.1` packages on nuget.org (their nuspecs and sizes, read on 2026-11-11), its release run 42946072313, `build/release-test.sh` and `build/ci.sh install` (red, then green), and `build/ci.sh package` locally and in CI (PR body)
- Amends: [ADR 0007](0007-per-os-native-nuget-packages-exact-pins.md) (three per-OS packages), [ADR 0023](0023-native-packages-build-pin-and-find.md) (the packages, their shared files), [ADR 0050](0050-release-workflow.md) (the ids `push 1` pushes)

## Context

ADR 0007 split the sidecar binaries into `Whatsmeow.Sidecar.Native.Linux`, `.MacOS` and `.Windows`, two binaries each, and named nuget.org's cap of about 250 MB as a reason to split by OS (RESEARCH "Per-OS native packages").
ADR 0023 built them, and pinned `Whatsmeow.Client` to each of the three at exactly its own version.

Measured on `1.0.0-rc.1`, the first published release:

- `Whatsmeow.Client` depends on all three native packages at `[1.0.0-rc.1]` [observed: its nuspec on nuget.org], so every consumer restores all six binaries; the split saves no consumer a download.
- The six binaries are 156,789,378 bytes unpacked, 25,231,520 to 26,963,456 each [observed: `unzip -l` of the three nupkgs from nuget.org].
- The three nupkgs nuget.org serves are 18,725,414 (Linux), 19,200,969 (MacOS) and 18,945,732 (Windows) bytes, 56,872,115 together [observed: `Content-Length` from the flat container].
  So one package of all six is far under the cap, which never applied.
- Trusted Publishing pushed all eight ids of rc.1, every one new on nuget.org at the time, under the same policy [observed: run 42946072313, the publish job's "Your package was pushed" for each].
  So a new id needs no change to the policy.
- `push 1` to "nuget.org serves every package of phase 1" took about 21 minutes for those four packages [observed: same job, 02:43:47 to 03:04:57], under the 45 minute limit of `wait 1`.
- The new id `Whatsmeow.Sidecar.Native` is absent on nuget.org, and the rc.1 ids are owned by `tcortega` with no reserved prefix [observed: the flat container's 404 and search's `verified: false`, 2026-11-11].

## Decision

- **One package.** `Whatsmeow.Sidecar.Native` is the one pack-only project (`IncludeBuildOutput` and dependencies off) and packs `runtimes/<rid>/native/whatsmeow-net[.exe]` for all six release targets; pack fails when a binary is missing.
  `Whatsmeow.Client` references it with `PackExactVersion="true"`, so it depends on it at exactly its own version, as on the three before (§13 invariant 12).
- **Its files sit in its project.** Its README is `dotnet/src/Whatsmeow.Sidecar.Native/README.md`, picked up like every other package's, and its single-file targets `dotnet/src/Whatsmeow.Sidecar.Native/Whatsmeow.Sidecar.Native.targets`, packed as `buildTransitive/Whatsmeow.Sidecar.Native.targets`.
  They sat at `dotnet/src/` only because three projects shared them.
  `COPYING`, `NOTICE`, `THIRD-PARTY-NOTICES.txt` and `GPL-3.0-only` come from `dotnet/src/Directory.Build.props` for that one project name (ADR 0029).
- **The release.** `push 1` is `Whatsmeow.Sidecar.Native` and `Whatsmeow.Protocol.WhatsApp`; the release pushes six ids, and `owners` checks those six.
  `verify` takes the six assets from the one native package.
- **Nothing else changes.** The locator never names a package (ARCHITECTURE §8.5), so every row of ADR 0023's table lands where it did: portable builds under `runtimes/<rid>/native/`, a RID-specific, self-contained, Native AOT or single-file publish flat beside the app, musl through the Linux binary.
  The execute bit handling, the Corresponding Source per release (ADR 0016) and the GPL files in the package (ADR 0029) stay.
- **The proof.** `build/ci.sh install` checks one pin, the package's exact files (the six binaries, its `buildTransitive` targets, README and the GPL files), its notices and licence, and runs every consumer path ADR 0023 and 0058 list on every target host and RID; `build/release-test.sh` pins the push phases and the six ids.

## Consequences

- One nupkg of about 57 MB instead of three of about 19 MB: 56,697,085 bytes at `1.0.0-rc.1.2`, with the same six binary sizes as rc.1 [observed: `build/ci.sh package` on osx-arm64 at `fc83c63`]; `build/ci.sh package` prints it on every run.
  A consumer downloads what it downloaded before.
- The three rc.1 ids stay on nuget.org, depended on exactly by `Whatsmeow.Client` `1.0.0-rc.1`; nothing here unlists or deprecates them (ADR 0050).
  Deprecating them, with `Whatsmeow.Sidecar.Native` as the alternative, is the owner's, in nuget.org's web UI, after rc.2 is published.
- The checker on main can no longer re-check rc.1 from nuget.org (`build/ci.sh install nuget.org:1.0.0-rc.1`, `nuget-install.yml`): it expects one native package, and rc.1 has three [observed: that run, red on the pin, the files, the notices, the licences and the READMEs].
  That is accepted: rc.1 was checked green on every target host and RID when it shipped, from the feed and from nuget.org [observed: run 42946072313, every install and aot job], and nothing is frozen before a stable 1.0.0.
- An app that left the native binaries out for External mode now sets `ExcludeAssets="all"` on one package instead of three (ADR 0046).
