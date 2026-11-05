# 0023. Native packages: one version, a plain Go build, exact pins, and how a consumer finds the binary

- Status: Accepted
- Amended by: [0058](0058-jit-install-on-every-target-rid.md) (JIT install coverage and native execution reporting on every target RID)
- Date: 2026-11-01
- Evidence: issue #43, its plan review, a prototype on SDK 10.0.403 and Go 1.27.3 (osx-arm64 and Docker Alpine 3.24), and `build/ci.sh package` and `install` in CI
- Amends: [ADR 0007](0007-per-os-native-nuget-packages-exact-pins.md) (the sizes, the build), [ADR 0016](0016-sidecar-binary-gpl-3-corresponding-source-per-release.md) (`.goreleaser.yaml`)
- Supersedes: the GoReleaser lines of ARCHITECTURE §5 and §12, and ADR 0016's ".goreleaser.yaml builds exactly the targets in `tools/check-licenses`"
- Amended by: [ADR 0029](0029-gpl-release-obligations.md) (each native package also holds its README, `COPYING`, `NOTICE` and `THIRD-PARTY-NOTICES.txt`)

## Context

ADR 0007 chose three per-OS native packages pinned at exactly the client's version.
Issue #43 builds them, and has to answer how the version flows, how the pin is expressed, how the binaries are built, and where a consumer's app finds the binary in each way it can be built.

Measured while building it:

- NuGet pack turns a `ProjectReference` into a dependency whose range is `VersionRange.Parse` of the `ProjectVersion` metadata on its internal `_ProjectReferencesWithVersions` item, which the `_GetProjectReferenceVersions` target fills [observed: NuGet.Client `PackTaskLogic.cs:967-969`; `NuGet.Build.Tasks.Pack.targets:320-357` in SDK 10.0.403].
  A plain version there means "at least"; `[x.y.z]` is exact [observed: prototype nuspec].
- Restore leaves out a `ProjectReference` with `ReferenceOutputAssembly="false"`, so pack drops it from the dependencies [observed: NuGet.Client `GetRestoreProjectReferencesTask.cs:66-71`; prototype assets file].
- `Update` on an item inside a target changes every item of that type, not the ones it names [observed: a scratch project on SDK 10.0.403].
- A package that holds only `runtimes/<rid>/native/` and `buildTransitive/` restores into a `net10.0` app with no compatibility error, and its `buildTransitive` targets reach the app through Client's `exclude="Build,Analyzers"` dependency [observed: the consumer's `project.assets.json`].
- NuGet creates every extracted file with mode 0766 masked by the umask, whatever the zip entry says, so the owner execute bit survives extraction [observed: NuGet.Client `NuGetExtractionFileIO.cs:19,45-52`; a zip entry `rw-r--r--` came out `rwxr--r--`].
  It is lost only by later copies, such as a Docker build context from Windows [inferred: RESEARCH "Per-OS native packages"]; the client sets it before every start anyway.
- MinVer 8.0.0 sets `Version` inside its `MinVer` target, so `dotnet msbuild -getProperty:Version` reads the SDK default 1.0.0 and `-t:MinVer -getProperty:Version` reads MinVer's version [observed]. Its `build/MinVer.targets` adds `NU5105` to `NoWarn` [observed].
- `go version -m` reads Mach-O, ELF and PE binaries from any host and prints `build -tags=fakewhatsapp` only for a tagged build [observed]. Under `-trimpath` it records no `-ldflags` (go.dev/issue/52372, `cmd/go/internal/load/pkg.go:2470-2479`), so the version is checked through `GetInfo` [observed].
- `CGO_ENABLED=0 go build -trimpath -ldflags "-s -w -X main.version=<v>"` from another path with an empty build cache gives a byte-identical binary [observed: sha256]; the `vcs.*` build settings come from the commit, and `-buildid=` is not needed.

Where a consumer's app finds the binary, with the locator ARCHITECTURE §8.5 already had [observed: prototype consumer on osx-arm64 unless named]:

| How the app is built | Where the binary lands | Result |
|---|---|---|
| Portable `dotnet build`, `dotnet run`, tests, portable `dotnet publish` | `runtimes/<rid>/native/` in the output, all six RIDs | found; the output is about 148 MB [observed: `du -sh` of the build output and of a portable publish, osx-arm64] |
| Portable on Alpine (musl) | `runtimes/linux-<arch>/native/`: the locator ignores the C library and always asks for `linux-<arch>` | found [observed: linux-musl-arm64 locally, linux-musl-x64 in GitHub Actions run 41351388993]; settles RESEARCH item 9 |
| RID-specific publish, framework-dependent or self-contained | flat, beside the app, one binary | found |
| `-r linux-musl-x64` | flat: the portable RID graph imports `linux-x64` into `linux-musl-x64` (`PortableRuntimeIdentifierGraph.json`) | found |
| Native AOT publish | flat | found, 0 warnings |
| Single-file, default | beside the app: only managed DLLs are bundled | found |
| Single-file with `IncludeNativeLibrariesForSelfExtract` | bundled and extracted under `~/.net/<app>/<hash>/` unless excluded | not found without `ExcludeFromSingleFile`, found with it |
| Single-file with `IncludeAllContentForSelfExtract` | beside the app (excluded), but `AppContext.BaseDirectory` is the extraction directory | not found |

The .NET docs call `IncludeAllContentForSelfExtract` "a .NET Core 3.1 compatibility mode" that is not recommended [observed: dotnet/docs single-file overview].

## Decision

- **One version.** MinVer (`MinVerTagPrefix` `v`) versions every package from the `v<semver>` tags; untagged, it is a pre-release from the height above the last tag, never a release.
  `build/ci.sh` reads it once with `-t:MinVer -getProperty:Version` and builds the sidecar with `-X main.version` at it, so the client, the native packages and every binary carry the same version (§13 invariant 12).
  `NU5105` joins the central `NoWarn` baseline in `dotnet/Directory.Build.targets`, with its reason next to it: MinVer's versions are SemVer 2.0.0 by design.
- **A plain Go build, not GoReleaser.** `build/ci.sh package` cross-builds `whatsmeow-net` for exactly the targets `tools/check-licenses -targets` prints, with the flags above, into `dotnet/artifacts/sidecar/<rid>/`.
  One list drives both the build and the licence audit, the layout is the packages' own, and the version has one source; GoReleaser would compute its own snapshot version and its own output layout.
  So the release workflow (#46) has one build of the binaries to upload, not a second one.
- **The packages.** `Whatsmeow.Sidecar.Native.Linux`, `.MacOS` and `.Windows` are pack-only projects (`IncludeBuildOutput` and dependencies off) that pack `runtimes/<rid>/native/whatsmeow-net[.exe]` for their two RIDs; pack fails when a binary is missing.
  Their empty assemblies are never packed.
- **Exact pins.** `Whatsmeow.Client` references the three projects with `PackExactVersion="true"`, a plain reference (with `ReferenceOutputAssembly="false"` pack would drop it).
  `_WhatsmeowExactProjectVersions` in `dotnet/Directory.Build.targets` runs after `_GetProjectReferenceVersions` and swaps those items of `_ProjectReferencesWithVersions` for copies whose `ProjectVersion` is `[version]`.
  It depends on a NuGet internal item, so `build/ci.sh install` checks the pin in the packed nuspec and fails naming that item and target.
- **Beside the app in a single file.** Each native package ships `dotnet/src/Whatsmeow.Sidecar.Native.targets` as `buildTransitive/<id>.targets`, which sets `ExcludeFromSingleFile` on `whatsmeow-net[.exe]`.
  `IncludeAllContentForSelfExtract` is not supported: such an app sets `SidecarPath` or `WHATSMEOW_NET_SIDECAR_PATH` to the binary beside the app, in the directory of `Environment.ProcessPath`.
- **The proof.** `build/ci.sh package` builds, packs into `dotnet/artifacts/package/`, and runs `install` on that feed.
  `install <feed>` makes a new console app outside the repository, restores `Whatsmeow.*` from the feed only into an empty packages folder, and checks the pins, one binary per release target with none built with `-tags fakewhatsapp`, that the binary refuses `--fake-whatsapp`, and then every row above that a CI runner can run, each a check of its own that names its consumer path.
  CI packs on Windows and installs that feed on Linux (glibc, Alpine in Docker, a noexec mount) and macOS; Windows also proves that win-x86, which ships no binary, fails naming both bundled paths.

## Consequences

- A portable build, `dotnet run`, test run or portable publish copies all six binaries into its output, about 148 MB [observed: `du -sh`, osx-arm64]; a RID-specific publish ships the one binary its RID needs. Consumers who care set a `RuntimeIdentifier`.
- NuGet extracts the binary as 0766 masked by the umask, 0744 with umask 022 [observed: above], so only its owner can make it executable. When one user restores or publishes it and another runs it (a Docker image built as root that runs as `USER app`), the client cannot chmod it and starts a copy in that user's local application data [observed: Alpine 3.24 container, built as root, run as `app`: the copy was `/home/app/.local/share/whatsmeow-net/<v>/whatsmeow-net`, 0700], made again on every start [inferred: `CopyToCache` copies before each start]. Such an image sets the mode at build time, or runs as the owner.
- The packed native packages are about 17.9 MB (Linux), 18.3 MB (MacOS) and 18.1 MB (Windows), packed on Windows [observed: GitHub Actions run 41357262808 at commit 3369aad]; [ADR 0007](0007-per-os-native-nuget-packages-exact-pins.md) has the bytes, and `build/ci.sh package` prints them on every run.
- In that run the Windows package job took 8m43s: 267 s for the six cold-cache Go builds, 84 s for pack, 43 s for every install check; each install job took about 1-1.5 min [observed].
- An SDK bump that changes NuGet's pack internals fails `install`'s pin check, which names `_ProjectReferencesWithVersions`; the fix is in `_WhatsmeowExactProjectVersions`.
- MinVer counts height only over the history a clone has; the release workflow (#46) checks out with full history, or a tag below HEAD is not seen [observed: MinVer README, shallow clones].
- The in-repo build references three empty assemblies; none of them reaches a package or a consumer.
- Not covered here: AOT smoke per RID from the feed (#44), signing (#45), publishing (#46), the GPL files and package metadata (#47), and runs on arm64 and osx-x64 runners (#44, #52).
