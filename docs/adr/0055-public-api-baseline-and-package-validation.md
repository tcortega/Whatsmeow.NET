# 0055. The public API baseline for 1.0, and package validation against the last stable release

- Status: Accepted
- Date: 2026-11-04
- Evidence: issue #50, its plan review, the API review in its PR, `build/release-test.sh`, `tools/check-repo` tests, and the probes and mutations in the PR body
- Amends: ARCHITECTURE §8.1 (stability), §8.3 (closed hierarchies), §12 (the release); [ADR 0023](0023-native-packages-build-pin-and-find.md) (which dependencies are exact)

## Context

PublicApiAnalyzers tracks `Whatsmeow.Client`, `Whatsmeow.Extensions.Hosting` and `Whatsmeow.Testing`, and RS0016/RS0017 are errors (`dotnet/.globalconfig`); before this record every member was in `PublicAPI.Unshipped.txt` and every `PublicAPI.Shipped.txt` was empty [observed].
At 1.0 that surface becomes a promise, so the owner reviewed it once before freezing it (the API review in #50's PR).

Measured:

- RS0017 fails a build that drops a member a `PublicAPI.*.txt` lists, and RS0016 one that adds a member neither lists; a `*REMOVED*<member>` line in `PublicAPI.Unshipped.txt` makes the analyzer accept a removed shipped member [observed: mutations in the PR body; the `*REMOVED*` convention is the analyzer's].
- `EnablePackageValidation` with `PackageValidationBaselineVersion` makes restore download the baseline package (a `PackageDownload`), and pack compare the package with it [observed: `Microsoft.NET.ApiCompat.targets:18-23` and `Microsoft.NET.ApiCompat.ValidatePackage.targets` in SDK 10.0.403].
- A `--locked-mode` restore with a baseline passes and leaves `packages.lock.json` unchanged; a baseline version nuget.org does not have fails restore with NU1102 [observed: a probe project on nuget.org packages].
- Packing `Whatsmeow.Client` at 1.0.1 against a 1.0.0 baseline packed from the same commit passes, with the Raw hatch's `Whatsmeow.Protocol.WhatsApp` types resolved; without `Jid.IsLid` it fails with `CP0002: Member 'bool Whatsmeow.Jid.IsLid.get' exists on [Baseline] ... but not on ...`; with a new member it passes [observed: `PackageValidationBaselinePath` probes].
- `Whatsmeow.Client` depended on `Whatsmeow.Protocol`, and `Whatsmeow.Protocol` on `Whatsmeow.Protocol.WhatsApp`, with a minimum version, not an exact one [observed: packed nuspecs], though the client is compiled against Protocol's generated code and internals (`InternalsVisibleTo`), and the Raw hatch's types come from `Whatsmeow.Protocol.WhatsApp`, which a consumer references to build a raw message.
  A consumer who resolves a newer one runs the client against generated code it was not built with [inferred: a missing member then fails at run time].
- A non-sealed record's copy constructor "must be public or protected" (CS8878), so code outside the library can derive from `WhatsAppEvent`, `MessageContent`, `SessionState` and the other abstract records through it, despite their `private protected` parameterless constructors [observed: probes against `Whatsmeow.Client`].
  C# `closed` "prevents derived classes from being declared outside the defining assembly", and it and unions are "active, but aren't yet committed to a release" [observed: devblogs.microsoft.com/dotnet/csharp-15-union-types].
- PublicAPI files record no `required`, type kind or base type [observed], so making a shipped property `required` is a source break that neither gate reports [inferred].
- The CI checkouts fetch every tag (`fetch-depth: 0`), and no `v*` tag exists yet [observed: the workflows, `git ls-remote --tags origin`].

## Decision

- **Before the baseline, the review's changes** (owner decisions in #50): `ConnectionStateChanged` is `SessionStateChanged`, `SessionState.TempBanned` is `SessionState.TemporarilyBanned` (the contract's `TempBanned` stays), the media metadata's `Mimetype` is `MimeType`, `WhatsmeowSessions.ListAsync` returns `ImmutableArray<SessionInfo>`, and `MarkReadAsync` takes an `IReadOnlyList<MessageId>`.
- **The baseline**: every public member of the three stable packages is in its `PublicAPI.Shipped.txt`, and each `PublicAPI.Unshipped.txt` holds only `#nullable enable`.
  A new member goes to `PublicAPI.Unshipped.txt`; before a stable tag, its lines move to `PublicAPI.Shipped.txt`.
  `build/ci.sh package` fails at a stable version whose `PublicAPI.Unshipped.txt` is not empty, so `PublicAPI.Shipped.txt` is always what the last stable release published.
- **Removing or changing a shipped member is a break**, which takes a new major version and its ADR; `tools/check-repo` rejects a `*REMOVED*` line in a stable package's API file, and that ADR lifts the rule for its release.
- **Outside the promise**: the `[Experimental("WMN1001")]` Raw hatch (`MessageReceived.Raw`, `SendRawAsync`), which changes with `Whatsmeow.Protocol.WhatsApp`, and types derived outside the library from its abstract records, whose cases may become `closed` later.
  A change to the Raw hatch after 1.0 needs an entry in the project's ApiCompat suppression file, since package validation does not exempt experimental members [inferred: no such option in the SDK's targets].
- **No generated protobuf or gRPC type outside the Raw hatch** (§13 invariant 6): `tools/check-repo` rejects a stable API line naming `Whatsmeow.Proto.`, `Whatsmeow.Protocol.`, `Google.Protobuf.` or `Grpc.` unless it carries `[WMN1001]`.
- **Package validation**: the three stable packages set `EnablePackageValidation` (`dotnet/src/Directory.Build.props`).
  `build/release.sh baseline <version>` names the release to compare with: the highest stable `v<major>.<minor>.<patch>` tag merged into HEAD, with the version's major and below it.
  There is none before 1.0, none for a major's first release or for the version of HEAD's own tag, never a pre-release (a pre-release is the place to change the API before its release), and never a tag only another branch holds.
  `build/ci.sh package` restores the baseline packages and packs with that version, so every package job after a release (the release workflow, its rehearsal, and a manual `ci.yml` run, the only runs that pack until the platform pass [observed: `ci.yml` `packaging` is `workflow_dispatch` only]) is compared with what the release published; a baseline nuget.org does not serve fails the job, saying the release is not published yet or its tag must be deleted (ADR 0050).
- **Exact pins**: `Whatsmeow.Client` depends on `Whatsmeow.Protocol`, and `Whatsmeow.Protocol` on `Whatsmeow.Protocol.WhatsApp`, at exactly its own version (`PackExactVersion`, ADR 0023), which `build/ci.sh install` checks.

## Consequences

- Each release before tagging: a commit that moves the Unshipped lines to Shipped; the package job refuses a stable tag without it.
- After 1.0, every package job compares with the published release; pull requests do not pack yet, so between releases RS0017 and `tools/check-repo` are the gate a pull request sees; while a new release waits for its approval, that comparison fails until nuget.org serves it, or until a refused release's tag is deleted.
- The renames change no contract; the sample and the tests follow them.
- Tests: `build/release-test.sh` (the baseline cases), `tools/check-repo` (`TestCheckPublicAPIKeepsShippedMembersAndProtobufTypesOut`), and the mutations in the PR body.
