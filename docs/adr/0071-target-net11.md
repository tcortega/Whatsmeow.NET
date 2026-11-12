# 0071. The packages target net11.0 only

- Status: Accepted, 2026-11-12, by Antonio
- Date: 2026-11-12
- Evidence: the .NET 11.0.0 release notes and the pages named below, nuget.org on 2026-11-12, and a build of this repository on SDK 11.0.100
- Amends: ARCHITECTURE §3 (`net10.0` only, C# 14), `docs/RESEARCH.md` (which deferred the choice until .NET 11 was out)

## Context

.NET 11 shipped on 2026-11-10: SDK 11.0.100, runtime 11.0.0, STS, supported until 2028-11-14 [observed: dotnet/core `release-notes/11.0`].
.NET 10 is LTS and ends support the same day [observed: `releases-index.json`].
The packages targeted `net10.0` only, and three places waited for .NET 11's Process APIs: ADR 0024 (`KillOnParentExit`, and the window between `Process.Start` and the job), and the kill-parent test's host, which makes every handle inheritable [observed: `HeldTetherHost.cs`].
No stable release exists yet, only release candidates, so package validation has no baseline a target change could break [observed: `dotnet/src/Directory.Build.props`].

## Decision

- Every project targets `net11.0`, on SDK 11.0.100, C# 15.
  One target, as before: `tools/check-repo` lets no csproj set one, and `build/ci.sh aot_setup` reads the single `TargetFramework`.
- Not `net10.0;net11.0`: each new Process API would then sit behind `#if`, with the old path kept and tested beside it.
- `Microsoft.Extensions.Logging.Abstractions`, `Microsoft.Extensions.Options` and `Microsoft.Extensions.Hosting.Abstractions` are no longer package references: on `net11.0` they are part of the framework, and a reference raises NU1510 [observed: restore on SDK 11.0.100].
- `WMN0003`'s `WarningLevel` floor is 11, the `net11.0` default [observed: `dotnet msbuild -getProperty:WarningLevel`].

## Not adopted

- runtime-async: opt-in and still a preview feature [observed: learn.microsoft.com, What's new in the .NET 11 runtime].
- C# 15 unions: the library types behind them are labelled preview [observed: learn.microsoft.com, What's new in .NET 11 libraries].
- `closed` on the public event and content hierarchies: a `switch` over one then needs no default arm, so each content type a later protocol adds would break an app's build, against the additive rule (ADR 0015).
- `dotnet test`'s new switches, NuGet's `minPublishAgeHours`, `SocketsHttpHandler.InitialHttp2MaxConcurrentStreams`, and the new JSON and Base64 APIs: nothing here needs them.

## Consequences

- An app on .NET 10 cannot install a release made after this change; the release candidates before it target `net10.0`.
- The .NET 11 runtime needs an x64 CPU with x86-64-v2, and on Windows Arm64 one with LSE [observed: What's new in the .NET 11 runtime]; the deployment guide says so.
- What the build on SDK 11.0.100 newly reported, fixed in code: CA1307 on `string.StartsWith(char)`, `EndsWith(char)` and `LastIndexOf(char)`, which gained `StringComparison` overloads; CA2016 on `TextWriter.WriteLineAsync(string)`, which gained a token overload; IDE0028 on comparer-only constructors, now `[with(StringComparer.Ordinal)]`; IDE0370 on one `!`; MA0002 on one empty `ImmutableHashSet<string>` [observed: the build].
- Checking an older release still uses that release's SDK and Alpine images (`release-pins`, ADR 0059).
- `dotnet test` on SDK 11.0.100 passes its own `--results-directory`, `dotnet/artifacts/test/<project>/release`, and the platform refuses a second one with exit code 5, so the build no longer adds one and CI keeps that directory too [observed: the run].
- The two runtime workarounds stay, re-read at dotnet/runtime `v11.0.0`: `Http2Stream.cs` differs from `v10.0.14` in one unrelated line, so `RequestBodyFence`'s race is still there, and grpc-dotnet is still v2.85.0; `NamedPipeClientStream`'s connect loop is unchanged, four lines lower [observed: a diff of both tags].
- The Process APIs are adopted in their own change (ADR 0024).
