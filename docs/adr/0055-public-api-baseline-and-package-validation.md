# 0055. The public API baseline for 1.0, and package validation against the last stable release

- Status: Accepted, 2026-11-06, by the API review Antonio delegated to his agent (PR #118)
- Date: 2026-11-04
- Evidence: issue #50, its plan review, the API review in its PR and its delegated decision, `build/release-test.sh`, `tools/check-repo` tests, and the probes and mutations in the PR body
- Amends: ARCHITECTURE §8.1 (stability), §8.3 (closed hierarchies), §12 (the release); [ADR 0023](0023-native-packages-build-pin-and-find.md) (which dependencies are exact); [ADR 0012](0012-iwhatsappclient-interface-exception.md) and [ADR 0036](0036-testing-package-and-fake-client.md) (who implements `IWhatsAppClient`)

## Context

PublicApiAnalyzers tracks `Whatsmeow.Client`, `Whatsmeow.Extensions.Hosting` and `Whatsmeow.Testing`, and RS0016/RS0017 are errors (`dotnet/.globalconfig`); before this record every member was in `PublicAPI.Unshipped.txt` and every `PublicAPI.Shipped.txt` was empty [observed].
At 1.0 that surface becomes a promise, so it is reviewed once before it is frozen.
Antonio delegated that review, R1 through R5 of the API review in PR #118 included, to his agent on 2026-11-06 [observed: his instruction, relayed by the coordinator]; the decision is the PR's review comment.

Measured:

- RS0017 fails a build that drops a member a `PublicAPI.*.txt` lists, and RS0016 one that adds a member neither lists; a `*REMOVED*<member>` line in `PublicAPI.Unshipped.txt` makes the analyzer accept a removed shipped member [observed: mutations in the PR body; the `*REMOVED*` convention is the analyzer's].
- `EnablePackageValidation` with `PackageValidationBaselineVersion` makes restore download the baseline package (a `PackageDownload`), and pack compare the package with it [observed: `Microsoft.NET.ApiCompat.targets:18-23` and `Microsoft.NET.ApiCompat.ValidatePackage.targets` in SDK 10.0.403].
- A `--locked-mode` restore with a baseline passes and leaves `packages.lock.json` unchanged; a baseline version nuget.org does not have fails restore with NU1102 [observed: a probe project on nuget.org packages].
- Packing `Whatsmeow.Client` at 1.0.1 against a 1.0.0 baseline packed from the same commit passes, with the Raw hatch's `Whatsmeow.Protocol.WhatsApp` types resolved; without `Jid.IsLid` it fails with `CP0002: Member 'bool Whatsmeow.Jid.IsLid.get' exists on [Baseline] ... but not on ...`; with a new member it passes [observed: `PackageValidationBaselinePath` probes].
- `Whatsmeow.Client` depended on `Whatsmeow.Protocol`, and `Whatsmeow.Protocol` on `Whatsmeow.Protocol.WhatsApp`, with a minimum version, not an exact one [observed: packed nuspecs], though the client is compiled against Protocol's generated code and internals (`InternalsVisibleTo`), and the Raw hatch's types come from `Whatsmeow.Protocol.WhatsApp`, which a consumer references to build a raw message.
  A consumer who resolves a newer one runs the client against generated code it was not built with [inferred: a missing member then fails at run time].
- A non-sealed record's copy constructor "must be public or protected" (CS8878), so code outside the library can derive from `WhatsAppEvent`, `MessageContent`, `SessionState` and the other abstract records through it, despite their `private protected` parameterless constructors [observed: probes against `Whatsmeow.Client`].
  C# `closed` "prevents derived classes from being declared outside the defining assembly", and it and unions are "active, but aren't yet committed to a release" [observed: devblogs.microsoft.com/dotnet/csharp-15-union-types].
- Package validation fails a pack that adds a member to a public interface, `CP0006: Cannot add interface member 'void P.IClient.B()' ... because it does not exist on [Baseline]`, and passes one that adds a default-implemented member or a member to a sealed class; `-p:ApiCompatGenerateSuppressionFile=true` writes the CP0006 entry to the project's `CompatibilitySuppressions.xml`, and the next pack reads it and passes [observed: a probe library packed at 1.1.0 against its 1.0.0, SDK 10.0.403].
  `IWhatsAppClient` is an interface with no default members (ADR 0036), so every member a minor version adds to it would fail the pack, and nothing stopped a suppression file from accepting any other break [observed: `tools/check-repo` passed a `CompatibilitySuppressions.xml` suppressing CP0006 in `Whatsmeow.Client`].
- PublicAPI files record no `required`, type kind, base type or interface list, generic constraint or variance [observed: `PublicAPI.Shipped.txt` lists `WhatsmeowException` without `: Exception`, `IWhatsAppEventHandler<TEvent>` without `in`, and `AddHandler<TEvent, THandler>()` without its `where` clauses], so making a shipped property `required`, or changing a shipped type's base, interfaces or constraints, is a source break that neither RS0016/RS0017 nor a check of those files reports [inferred].
- RS0017 compares the code with the `PublicAPI.*.txt` files only, so a member dropped from the code and from `PublicAPI.Shipped.txt` together passes it [observed: a clone with `Jid.IsLid` dropped from `Jid.cs` and `PublicAPI.Shipped.txt` builds `Whatsmeow.Client` without RS0017, and `build/release.sh shipped-api` there, at a local `v1.0.0` tag, names the line].
- The CI checkouts that read the tags fetch every tag (`fetch-depth: 0`), and no `v*` tag exists yet [observed: the workflows, `git ls-remote --tags origin`]; in a `--depth 1` clone `git tag --merged HEAD` sees only the tags of the commits it has, so a baseline would be silently empty there [observed: the review of #50's PR; `build/release-test.sh`].
- `packaging` runs on a push to main, on `workflow_dispatch` and in the release workflow, never on a pull request [observed: `ci.yml`, `release.yml`, ADR 0056]; the rehearsal packs at `v0.0.0-rehearsal.<run>`, a tag in the runner only (ADR 0050).

## Decision

- **Before the baseline, the review's changes** (R1 through R5, accepted): `ConnectionStateChanged` is `SessionStateChanged`, `SessionState.TempBanned` is `SessionState.TemporarilyBanned` (the contract's `TempBanned` stays), the media metadata's `Mimetype` is `MimeType`, `WhatsmeowSessions.ListAsync` returns `ImmutableArray<SessionInfo>`, and `MarkReadAsync` takes an `IReadOnlyList<MessageId>`.
- **The baseline**: every public member of the three stable packages is in its `PublicAPI.Shipped.txt`, and each `PublicAPI.Unshipped.txt` holds only `#nullable enable`.
  A new member goes to `PublicAPI.Unshipped.txt`; before a stable tag, its lines move to `PublicAPI.Shipped.txt`.
  `build/ci.sh package` fails at a stable version whose `PublicAPI.Unshipped.txt` is not empty, so `PublicAPI.Shipped.txt` is always what the last stable release published.
- **From v1.0.0, removing or changing a shipped member is a break**, which takes a new major version and its ADR.
  `tools/check-repo` rejects a `*REMOVED*` line in a stable package's API file, and the contract job's `build/release.sh shipped-api` fails when a line, other than a `[WMN1001]` one, that `PublicAPI.Shipped.txt` held at the baseline tag of the next stable release is gone or changed, so dropping a member from the code and that file together fails a pull request, which does not pack.
  A new major's first pre-release tag lifts `shipped-api` for that major, which has no baseline; its ADR lifts the `*REMOVED*` rule.
  Before v1.0.0 there is no baseline, so a member is changed or removed by editing `PublicAPI.Shipped.txt` itself, as ADR 0015 lets the contract break before its first tag.
  A shipped type's base, interfaces, generic constraints and `required` members are not in the files, so reviewers check them by hand.
- **`IWhatsAppClient` grows in minor versions.**
  Only the library and `Whatsmeow.Testing` implement it; an application substitutes it with `FakeWhatsAppClient` or a mocking library, and a class of its own that implements it is outside the promise, as its remarks say.
  A minor version that adds a member to it adds that member's CP0006 suppression to `Whatsmeow.Client`'s `CompatibilitySuppressions.xml`; it gets no default implementation, so the fake still fails to build until it implements the member (ADR 0036).
  `IWhatsAppEventHandler<TEvent>` is the consumers' to implement, so it never grows.
  `tools/check-repo` rejects a suppression file anywhere but `Whatsmeow.Client` and `Whatsmeow.Testing`, and any suppression in it other than CP0006 on an `IWhatsAppClient` member or a change of the Raw hatch.
- **Outside the promise**: the `[Experimental("WMN1001")]` Raw hatch (`MessageReceived.Raw`, `SendRawAsync`), which changes with `Whatsmeow.Protocol.WhatsApp`, and types derived outside the library from its abstract records, whose cases may become `closed` later.
  A change to the Raw hatch after 1.0 needs an entry in the project's `CompatibilitySuppressions.xml`, since package validation does not exempt experimental members [inferred: no such option in the SDK's targets].
- **No generated protobuf or gRPC type outside the Raw hatch** (§13 invariant 6): `tools/check-repo` rejects a stable API line naming `Whatsmeow.Proto.`, `Whatsmeow.Protocol.`, `Google.Protobuf.` or `Grpc.` unless it carries `[WMN1001]`, and a `[WMN1001]` line naming any of them but `Whatsmeow.Proto.`, the upstream WhatsApp types.
- **Package validation**: the three stable packages set `EnablePackageValidation` (`dotnet/src/Directory.Build.props`).
  `build/release.sh baseline <version>` names the release to compare with: the highest stable `v<major>.<minor>.<patch>` tag merged into HEAD, with the version's major and below it.
  There is none before 1.0, none for a major's first release or for the version of HEAD's own tag, never a pre-release (a pre-release is the place to change the API before its release), and never a tag only another branch holds.
  It fails in a shallow clone, and when a version past its major's first release finds no release of that major, so missing tags never pass as no baseline.
  A rehearsal's `0.0.0-rehearsal.<run>` stands for the next stable release: the version of the highest pre-release tag that no stable tag has yet, else the patch after the highest stable tag; its baseline is that version's.
  `build/ci.sh package` restores the baseline packages and packs with that version, so every package job after a release (a push to main, a manual `ci.yml` run, the release workflow and its rehearsal) is compared with what the release published; a baseline nuget.org does not serve (NU1102) fails the job, saying the release is not published yet or its tag must be deleted (ADR 0050).
  A rehearsal is also held to a stable release's empty `PublicAPI.Unshipped.txt`, so a break or an unshipped member shows there before the tag.
- **Exact pins**: `Whatsmeow.Client` depends on `Whatsmeow.Protocol`, and `Whatsmeow.Protocol` on `Whatsmeow.Protocol.WhatsApp`, at exactly its own version (`PackExactVersion`, ADR 0023), which `build/ci.sh install` checks.

## Consequences

- Each release before tagging: a commit that moves the Unshipped lines to Shipped; the package job refuses a stable tag without it.
- After 1.0, every package job compares with the published release; pull requests do not pack, so between releases RS0017, `tools/check-repo` and `shipped-api` are the gate a pull request sees; while a new release waits for its approval, that comparison fails until nuget.org serves it, or until a refused release's tag is deleted.
- A rehearsal fails while any `PublicAPI.Unshipped.txt` holds a member, as the release itself would.
- The renames change no contract; the sample and the tests follow them.
- A hand-written `IWhatsAppClient` in an application breaks on a minor update; that is the price of one interface without default members, which keeps the fake complete.
- Tests: `build/release-test.sh` (the baseline and shipped-api cases), `tools/check-repo` (`TestCheckPublicAPIKeepsShippedMembersAndProtobufTypesOut`, `TestCheckCompatibilitySuppressionsOnlyLetIWhatsAppClientGrowAndRawChange`), and the mutations in the PR body.
