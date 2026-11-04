# 0054. User docs: guides that tag their evidence, a README whose code is the samples', and package metadata

- Status: Accepted
- Date: 2026-11-04
- Evidence: issue #51, its plan review, the check-repo tests and the mutations in its PR
- Amends: [ADR 0049](0049-the-hosted-sample-and-its-fake-mode-check.md) (a second sample, the one without DI)

## Context

Nothing told a consumer how to use the library or what it guarantees, and the packages had no description, tags, URLs or README for nuget.org (#51).
Several facts a consumer relies on are guarantees with limits, security duties, or inferences nobody measured, so the docs have to say how sure each one is.

- The README's quick start had to be code CI builds, with and without DI (#51). The hosted sample, EchoBot, is DI only (ADR 0049).
- Code pasted into Markdown is checked by nothing, so it drifts from the API it shows [inferred: the usual fate of README code].
- `tools/check-repo` already walks the repository in the `dotnet` CI job, on all three OSes, with Go tests per rule [observed: `build/ci.sh` `job_dotnet`].
- `build/ci.sh install` restores every package of the feed into a new consumer and reads their nuspecs (ADR 0023, ADR 0029) [observed].
- Without a `RepositoryUrl`, the SDK's SourceLink writes the build's git remote, `https://github.com/tcortega/Whatsmeow.NET.git` in a local pack of 8954ddd [observed: its nuspec], so a clone from elsewhere would publish another URL; the URL is set explicitly.

## Decision

- **User guides in `docs/guides/`**, one file per area, an index in its `README.md`; the root `README.md` stays short: what it is, install, the quick start, the guarantees in three lines, the packages, links.
  Each guarantee carries its evidence: plain when the code or a test enforces it, `[observed: where]` for a measurement, `[inferred]` and `[unknown]` otherwise, with the § of ARCHITECTURE it came from.
- **A second sample, `dotnet/samples/EchoConsole/`**, without DI: one session, QR pairing, `On<MessageReceived>`, and `ConnectAsync` only for a paired session, since pairing starts the events (ADR 0052).
  Its one setup method, `Echo.StartAsync(IWhatsAppClient, TextWriter, CancellationToken)`, is what its FullCycle, `Samples/EchoConsoleTests.cs`, runs on the shared `FakeSidecar`; `Program.cs` adds only the sidecar's start and the wait for Enter.
- **Code in the README is a sample file.** A fenced block under `<!-- snippet: <path> -->` must be that file, byte for byte but for line endings, and the path must be under `dotnet/samples/`, which CI builds.
  `tools/check-repo` checks it in `README.md`, `docs/guides/*.md` and `dotnet/src/*/README.md`; in `README.md` and the package READMEs every C# block needs a marker, and `README.md` must show at least one sample, so the check can prove something. A guide may also show an unchecked fragment.
  Whole files only, no regions: the README shows `EchoConsole` whole and EchoBot's `EchoBotServices.cs` and `EchoHandler.cs`.
- **Package metadata** in `dotnet/src/Directory.Build.props`: `Authors` and `Copyright` as `NOTICE` says, the project and repository URLs, the tags, and `README.md` next to each project as the package's README (the native packages keep `Whatsmeow.Sidecar.Native.md`); each csproj sets its own `Description`, which joins the check-repo csproj allowlist.
  `build/ci.sh install` checks every package's description (not the SDK's default), tags, URLs, author and README, and that the README is the repository's file.

## Consequences

- A change to a sample that the README shows fails `check-repo` until the README is updated, and the other way round.
- A guide's fragment can still drift; only the samples are compiled.
- `AudioMetadata.Mimetype`'s doc stated an unmeasured claim as fact; it now says it is not measured.
- Tests: check-repo `TestCheckDocsShowSampleFilesAsTheyAre`, `TestCheckTheReadmeShowsASample`; `Samples/EchoConsoleTests.cs`; `build/ci.sh install` `packaged_metadata`.
