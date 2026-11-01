# C# idioms

Read this when writing or changing C# under `dotnet/`.
The control flow and type rules are the house rules from bridge, compras and chrominal (VsaTemplate lineage).
This is a library, not a VSA API: no Immediate.Handlers, no `[Handler]`, no Immediate.Injections or Immediate.Validations.
A slice here is a WhatsApp capability with a feature group on `IWhatsAppClient` (see [ARCHITECTURE.md](../ARCHITECTURE.md) §3).
The reference slice is named in `AGENTS.md` (Gold-standard slice); copy it.

## Control flow

Do not write `else`.
This is habit, not an analyzer. Leave MA0071 off.

Use a guard and continue:

```csharp
if (session is null)
	SessionNotFoundException.Throw(sessionId);
```

Invert the condition and return early.
Use `is not` when it reads cleaner.
Use a ternary for a value.
Use a switch expression for a mapping.
Single line `if` has no braces.

Test members with a property pattern, not a pile of `is null`s.

```csharp
if (state is not { Kind: SessionStateKind.Connected })
	return;
```

Throw through helpers. Always.
Never `throw new SessionNotFoundException(...)` or any other `WhatsmeowException` subtype at a call site.
Each exception type owns a `[DoesNotReturn] static void Throw(...)` helper.
BCL guards (`ArgumentNullException.ThrowIfNull`, `ArgumentOutOfRangeException.ThrowIfNegative`, `ObjectDisposedException.ThrowIf`) are already helpers; use them.

Closed sets are enums: reason codes, session state kinds, media types, and the like.
A state that carries data (`TempBanned{until}`, `Error{reason}`) is a sealed record case, not an enum plus a side field.

## Types

Records we own never use primary constructors.
That is VSA0002: positional records fight queries and make refactors lie. `tools/check-repo` fails on `record X(`, `record struct X(` and `record class X(` outside generated code, also when the `(` is on a later line.

```csharp
public sealed record SendResult
{
	public required MessageId Id { get; init; }
	public required Jid Chat { get; init; }
	public required DateTimeOffset Timestamp { get; init; }
}
```

Classes use a primary constructor where possible (supervisor, dispatcher, test classes, exception types).
Seal types.
`var` everywhere.
Expression-bodied only while the method is one statement.
The moment there is sequencing, use a block.
Never write `global::` (generated code is exempt).
Global usings live in the csproj `ItemGroup Label="Usings"`, not in a `GlobalUsings.cs`.

## Public API modeling

Moved from ARCHITECTURE §8.3; the event and content hierarchy shape stays there.

- Immutability throughout: `required` / `init` members, `ImmutableArray<T>` / `IReadOnlyList<T>` for collections.
- Async: client operations return `Task`/`Task<T>`. Handler callbacks return `ValueTask`. Every async method takes a `CancellationToken` as its last parameter. The `Async` suffix is always used.
- Time is BCL (see Time). Public members expose `DateTimeOffset` (UTC) and `TimeSpan`, never `DateTime` and never NodaTime.
- Extension blocks: convenience only (ARCHITECTURE §8.2).

### The one interface exception

House rule: do not add an interface just to mock it.
`IWhatsAppClient` is the single exception.
`Whatsmeow.Testing` ships `FakeWhatsAppClient` to consumers so they can test their bots; that fake is a product, not our test seam.
Inside the library, the house rule holds: no `ISupervisor`, `IDispatcher`, `ITransport` just so a test can swap it.

## IDs

Primitive wrappers (`SessionId`, `MessageId`, and later ones like a client message id) are Vogen value objects.
`Jid` is hand-written: it is structured (user, server, device, PN/LID pair), not a wrapper around one primitive.

```csharp
[ValueObject<string>]
public readonly partial struct SessionId
{
	private static Validation Validate(string value) =>
		string.IsNullOrWhiteSpace(value)
			? Validation.Invalid("Session id must not be empty.")
			: Validation.Ok;
}
```

Why Vogen in a public library: `default(SessionId)` is a compile error (VOG009), and an uninitialized instance (a defaulted field) throws on `.Value` [observed: spike, Native AOT].
A hand-written `readonly record struct` cannot stop a silent default, and `MessageId` plus `SessionId` is the dedupe key (ARCHITECTURE §8.4).

Rules (measured in the 2026-10-31 spike, Vogen 8.0.7, ARCHITECTURE §14):

- One assembly attribute in `Whatsmeow.Client`:

  ```csharp
  [assembly: VogenDefaults(
  	throws: typeof(WhatsmeowValidationException),
  	conversions: Conversions.SystemTextJson,
  	systemTextJsonConverterFactoryGeneration: SystemTextJsonConverterFactoryGeneration.Omit)]
  ```

- `throws:` keeps `ValueObjectValidationException` out of the public API. The exception needs a public constructor taking one `string`, or the build fails with VOG013 [observed]. A primary constructor `(string message)` satisfies it.
- `Conversions.SystemTextJson` only. Never `Conversions.Default`: it adds a `TypeConverter`, and a consumer reaching it through `TypeDescriptor.GetConverter` gets IL2026/IL2111/IL3050 under trimming [observed]. The library itself compiles warning-free either way.
- `Omit` the factory. Otherwise Vogen emits a public `VogenTypesFactory` in the assembly root namespace, which leaks into our public API [observed: generated code].
- Consumer JSON works without the factory: a consumer's own STJ source-gen context reads the `[JsonConverter]` from our compiled metadata, round-trips, and rejects invalid values, under Native AOT with zero warnings [observed].
- Inside our own assembly, STJ source-gen cannot see Vogen's converters, because generators do not see each other's output (it serialized `{}`) [observed; Vogen docs say the same]. No `JsonSerializerContext` of ours may contain a Vogen type. Serialize `.Value` instead. Today none needs to: the ready line carries no IDs.
- `Vogen.SharedTypes.dll` (20 KB, no further dependencies) is a hard runtime dependency: our nupkg depends on `Vogen` (`exclude="Build,Analyzers"`), and deleting the dll makes a consumer crash with `FileNotFoundException` [observed]. Never mark Vogen `PrivateAssets="all"`. The dependency is accepted.
- `Jid` is a `readonly struct` implementing `ISpanParsable<Jid>`, with no positional members. It carries both PN and LID forms where known.

## Time

This library deviates from the house NodaTime rule (ARCHITECTURE Decision log 0009).
NodaTime in the public API would force it on every consumer, and the library does no zone or calendar math, only timestamps, timeouts, backoff, `RetryAfter` and `Until` [inferred: from ARCHITECTURE §7-§8, nothing there needs a zone].
NodaTime's strengths go unused, and its costs were measured (ARCHITECTURE §14 Spike results): `Duration` options bind silently wrong, Polly schedules only through `TimeProvider`, and `FakeClock` cannot drive timers.

- A point in time is `DateTimeOffset` with a zero offset (UTC). Never `DateTime`.
- A length of time is `TimeSpan`.
- Now is `TimeProvider.GetUtcNow()`, from an injected `TimeProvider`. Never `DateTimeOffset.UtcNow` or `.Now`.
- `TimeProvider.System` is banned too. The single default site, where a caller injects no `TimeProvider`, reads it under `#pragma warning disable RS0030 // Justification: the default when none is injected`; no other code reads it.
- Elapsed time is `TimeProvider.GetTimestamp()` / `GetElapsedTime()`. Never `Stopwatch`.
- Every timer takes the same `TimeProvider`: `Task.Delay(TimeSpan, TimeProvider, CancellationToken)`, `Task.WaitAsync(TimeSpan, TimeProvider, CancellationToken)`, `new CancellationTokenSource(TimeSpan, TimeProvider)` (never `CancelAfter`), `new PeriodicTimer(TimeSpan, TimeProvider)`, `TimeProvider.CreateTimer`, Polly's `ResiliencePipelineBuilder.TimeProvider`.
- No way back to `DateTime`: `DateTimeOffset.DateTime`, `UtcDateTime`, `LocalDateTime`, the `DateTime` constructors and implicit conversion, and `Timestamp.ToDateTime` / `FromDateTime` are banned.
- No `DateOnly`, `TimeOnly` or `TimeZoneInfo`. Nothing here needs a civil date or a zone; if something ever does, that is an ADR, not a quiet `TimeZoneInfo`.
- Proto `Timestamp` / `Duration` map with Google.Protobuf's own `ToDateTimeOffset()` / `ToTimeSpan()` (see [proto.md](proto.md)).
- Tests use one `FakeTimeProvider` (`Microsoft.Extensions.TimeProvider.Testing`). It drives "now", delays and Polly together: in the spike one `FakeTimeProvider` drove a Polly exponential retry with 2s, 4s and 8s gaps under Native AOT [observed].

## Async and concurrency

Moved from ARCHITECTURE §8.10.

- Use `System.Threading.Channels` for producer/consumer handoff and `System.Threading.Lock` for short critical sections. Never hold a lock across `await`.
- Never block on async code (`.Result`, `.Wait()`, `GetAwaiter().GetResult()`). Never write `async void`.
- `ConfigureAwait(false)` throughout library code. CA2007 is an error here, unlike the house `.editorconfig` (an ASP.NET app has no sync context; a library does not know its caller's).
  MA0004 is an error too: CA2007 does not flag `await using (existing)`, MA0004 does [observed: scaffold, #2]. Both fire on a plain `await`.
- Test methods never call `ConfigureAwait` (xUnit1030). CA2007 is off for `dotnet/tests`: it still flags some awaits inside test methods, `await using` among them [observed: scaffold, #2], and xUnit1030 forbids the fix. MA0004 skips test methods and still flags fixtures and helpers, so those keep `ConfigureAwait(false)`; for them MA0004 is the only enforcement, not CA2007 [observed].
- No `async void`: MA0155 is an error.
- Never resume on the captured context: `ConfigureAwait(true)`, `ConfigureAwait(continueOnCapturedContext: true)` and `ConfigureAwaitOptions.ContinueOnCapturedContext` fail `tools/check-repo`.
- The `Async` suffix is exactly on awaitable-returning methods: MA0137 and MA0138 are errors. MA0137 skips test methods by default [observed: Meziantou docs, `MA0137.exclude_test_methods = true`].
- Every background loop has an owning `CancellationTokenSource`. It is awaited in `DisposeAsync`, and its exceptions are observed and logged.

## Reflection

Moved from ARCHITECTURE §8.9; the policy itself stays there.

**No runtime reflection in library code.** Everything that would normally use reflection is either generated at build time or written explicitly:

| Need | Approach |
|---|---|
| Protobuf serialization | generated code (`IBufferMessage`). Never `JsonFormatter`, `JsonParser` or descriptor-based reflection APIs |
| Logging | `[LoggerMessage]` source generator |
| Options validation | `[OptionsValidator]` source generator plus `ValidateOnStart` (see [options.md](options.md)) |
| JSON (ready line, sidecar logs) | `System.Text.Json` with a `JsonSerializerContext` |
| Native interop | `[LibraryImport]` and CsWin32. Never `[DllImport]` |
| Event dispatch | exhaustive pattern matching over sealed records, with generic handler lookup keyed by the compile-time `TEvent` type. Never `Type.GetType`, `MakeGenericType` or `Activator` |
| Handler registration | explicit generic `builder.AddHandler<THandler>()`. A source generator for discovery may come later; assembly scanning never |
| Value-object IDs | Vogen source generator |

Enforcement:
- `IsAotCompatible=true` and `IsTrimmable=true` on every shipped project, with all trim and AOT warnings as errors: locally through the `Trimming`, `AOT` and `SingleFile` diagnostic categories set to error for `dotnet/src` in `/.editorconfig` [observed: IL2057 and IL3050 became errors], and at publish through AotSmoke.
- `Microsoft.CodeAnalysis.BannedApiAnalyzers` with `BannedSymbols.txt` (see below).
- `tests/AotSmoke` publishes with `PublishAot=true` and must produce zero warnings.

## Suppressions

A suppression is allowed only with a written justification, and the reviewer judges the justification (definition of done).
This mirrors `nolintlint` in [go.md](go.md).

- `#pragma warning disable` names the rule IDs and carries `// Justification: <why>` on the same line; so does `#nullable disable`.
- `[SuppressMessage]` and `[UnconditionalSuppressMessage]` carry a non-empty `Justification = "<why>"`, however they are qualified.
- No alias of `SuppressMessageAttribute`, `UnconditionalSuppressMessageAttribute` or `GeneratedCodeAttribute`.

Everything else is an allowlist; the guards stop a rule from being turned off by accident or for convenience, and reviewers judge intent.
A real need is met by extending the allowlist in a reviewed change, never by a local workaround.

- `WMN0003` (`dotnet/Directory.Build.targets`) fails a project whose `NoWarn` holds anything beyond the literal baseline (the SDK's 1701, 1702, 2008, 8002, IL2121 with PublishAot, and 1591 outside `dotnet/src`), whose analyzer configs are anything but `/.editorconfig`, `dotnet/.globalconfig`, its generated MSBuild editorconfig and the SDK's own, or that sets `WarningsNotAsErrors` or `CodeAnalysisRuleSet`, lowers `WarningLevel` below 10, turns off `EnableNETAnalyzers`, `GenerateDocumentationFile`, `RunAnalyzers`, `RunAnalyzersDuringBuild`, `EnforceCodeStyleInBuild`, `Nullable` or editorconfig discovery, changes `AnalysisLevel`, drops `Nullable` from `WarningsAsErrors` or `TreatWarningsAsErrors` in a CI build, or loses `BannedSymbols.txt` or an analyzer.
- `tools/check-repo` allows only the blessed `/.editorconfig`, `dotnet/.globalconfig`, `/nuget.config`, `/global.json` and the four MSBuild infrastructure files (`dotnet/Directory.Build.props`, `dotnet/Directory.Build.targets`, `dotnet/src/Directory.Build.props`, `dotnet/Directory.Packages.props`); no other `Directory.*`, `*.rsp` or `*.ruleset`.
- A csproj may use only the allowlisted elements and properties in `tools/check-repo` (`csprojRules`): no `<Target>`, `<Import>`, `NoWarn` or any other rule switch.
- Generated code exists only in folders that a buf template with `clean: true` writes and CI regenerates (`dotnet/src/Whatsmeow.Protocol.WhatsApp/Generated/`, `dotnet/src/Whatsmeow.Protocol/Generated/`, and `sidecar/gen/whatsmeow/v1/` for Go). Outside them, no generated-code marker: `<auto-generated>` in the leading comments, `[GeneratedCode]` with any qualification, `*.g.cs`, `*.g.i.cs`, `*.generated.cs`, `*.designer.cs` or `TemporaryGeneratedFile_*`. Analyzers skip generated code, so a marker is a suppression in disguise.

`tools/check-repo` also rejects positional records and captured contexts (see Types, Async and concurrency). It reads C# with comments and string contents blanked, so documentation that mentions a pattern is not a finding.
These are text patterns; an in-repo Roslyn analyzer is to replace them (follow-up issue).
CI runs it in the dotnet job (`build/ci.sh dotnet`).

## Build baseline

Copy from tcortega/bridge when scaffolding `dotnet/`, then apply the deltas listed here.

- `.editorconfig` at the repo root: bridge's VsaTemplate file verbatim (tabs, file-scoped namespaces as error, Style analyzers at warning, `s_` / `_` field prefixes, and its EF Migrations section, kept for parity). Deltas, each an error:
  - `CA2007` and `MA0004` (ConfigureAwait, see Async and concurrency).
  - `MA0155` (no `async void` methods).
  - `RS0030` (banned API), so a banned call fails a local build too, not only CI.
  - `IDE0007` (`var` everywhere), `MA0137` and `MA0138` (the `Async` suffix), `CA1848` (`[LoggerMessage]` only). For IDE0007 the three `csharp_style_var_*` options also say `:error`: with house `:suggestion` there, `dotnet_diagnostic.IDE0007.severity = error` alone reported nothing in a build [observed: scaffold, #2].
  - `CA1707` and `CA2007` are off for `dotnet/tests` only: test names are `Behavior_Condition_Result` ([tests.md](tests.md)), which CA1707 rejects, and test methods never call `ConfigureAwait` (see Async and concurrency) [observed].
  - Library deltas, for `dotnet/src` only: the `Trimming`, `AOT` and `SingleFile` categories and `CS1591` (XML docs on public members). `dotnet/src/Directory.Build.props` removes 1591 from `NoWarn` for the same projects, because `NoWarn` wins over `.editorconfig` [observed: scaffold, #2]. Tests and generated code keep CS1591 off.
  - `.slnx` is in the XML section (tabs, size 2); `dotnet sln` keeps the tabs [observed].
  - The `[*.{yml,yaml}]` section says `indent_style = space`; bridge's `spaces` is not a valid value, so YAML fell back to tabs.
  - `end_of_line = lf` under `[*]`, matching `.gitattributes`. Without it the formatter assumes CRLF on Windows, and a line comment at the top of an LF file failed the Windows build with IDE0055 only [observed: CI run 41259268294, gone in 41259542114].
- `Directory.Build.props`: `LangVersion latest`, `Nullable enable` with `WarningsAsErrors Nullable`, `ImplicitUsings`, `AnalysisLevel latest-all`, `EnforceCodeStyleInBuild`, `GenerateDocumentationFile` (the unused-usings analyzer needs it), `TreatWarningsAsErrors` on CI. Deltas: `net10.0`; `UseArtifactsOutput` with `ArtifactsPath` pinned to `dotnet/artifacts`; `MSBuildTreatWarningsAsErrors` on CI; bridge's coverlet properties dropped (inert without coverlet.msbuild, and coverage is not a gate here).
- `Directory.Build.props` also turns on `RestorePackagesWithLockFile`: `packages.lock.json` is committed and CI restores with `--locked-mode`. Its CI group applies to a local build with `-p:ContinuousIntegrationBuild=true`, the pre-push build in AGENTS.md.
- `/nuget.config`: `<clear />` and nuget.org only, so no machine or user feed changes a restore.
- `src/Directory.Build.props`: `IsAotCompatible` and `IsTrimmable` for every shipped project, and CS1591 back on (above).
- Stable shipped projects (`Whatsmeow.Client` now; `Whatsmeow.Extensions.Hosting` and `Whatsmeow.Testing` when they exist) reference `Microsoft.CodeAnalysis.PublicApiAnalyzers` and track their API in `PublicAPI.Shipped.txt` / `PublicAPI.Unshipped.txt`; RS0016 and RS0017 are errors in `dotnet/.globalconfig`. `Whatsmeow.Protocol*` are unstable and untracked. This is a library delta.
- `Directory.Build.targets`: bridge's source-control `AssemblyMetadata` target without `SourceBranch` (house delta: the same commit built from two branches gives the same assembly), plus:
  - `WMN0001`: Grpc.Core or Newtonsoft.Json anywhere in the resolved package graph fails the build.
  - `WMN0002`: a project named `*Tests` that is not a Microsoft.Testing.Platform app fails the build, because `dotnet test` would skip it.
  - `WMN0003`: a project that switches a rule off fails the build (see Suppressions). It runs after `CoreCompile`, when the SDK and packages have added their analyzer configs.
  - Test results go to `dotnet/artifacts/TestResults/<project>/`, wherever `dotnet test` runs from.
  - A test project with no `*.cs` file may report zero tests; once it has one, zero tests fails the run ([tests.md](tests.md)).
  - A project with no source file of its own still gets the root `.editorconfig` and `dotnet/.globalconfig`. The SDK finds them from the project's Compile items, and otherwise an empty test project's generated xunit entry point was analyzed without them [observed: scaffold, #2].
- `Directory.Packages.props`: Central Package Management with `CentralPackageTransitivePinningEnabled`; `Meziantou.Analyzer` and `Microsoft.CodeAnalysis.BannedApiAnalyzers` as `GlobalPackageReference`.
- `dotnet/.globalconfig`: `dotnet_banned_api_analyzer.exclude_generated_code = true`. Generated code cannot be fixed at the call site; protobuf's generated `ToString()` calls the banned `JsonFormatter` [observed: 1942 RS0030 without it]. Trim and AOT analysis still cover generated code through `tests/AotSmoke`.
- `/global.json` at the repo root: the exact SDK (`rollForward: disable`) and the Microsoft.Testing.Platform runner. The SDK looks for it from the working directory, not the project, so under `dotnet/` it was ignored by commands run from the root [observed: scaffold, #2].
- `tests/AotSmoke` roots every shipped assembly with `TrimmerRootAssembly`, so all of each one is analyzed, and sets `TreatWarningsAsErrors` itself, so a trim or AOT warning fails the publish locally as well. It takes its RID from the SDK host and lists all three CI RIDs, so its lock file is the same on every OS; `dotnet publish -r` would flow the RID into the libraries and break their locked restore with NU1004 [observed].
- `.slnx`, never `.sln`.

```xml
<ItemGroup>
	<GlobalPackageReference Include="Microsoft.CodeAnalysis.BannedApiAnalyzers" Version="5.6.0" PrivateAssets="all" />
	<AdditionalFiles Include="$(MSBuildThisFileDirectory)BannedSymbols.txt" />
</ItemGroup>
```

### BannedSymbols.txt

Lives at `dotnet/BannedSymbols.txt`.
It must cover:

- Ambient time and timers that bypass `TimeProvider`. These exact IDs were checked against BannedApiAnalyzers 5.6.0: each flags the bad call and none flags the `TimeProvider` overload [observed: spike]:

  ```text
  T:System.DateTime; Use DateTimeOffset, from TimeProvider
  T:System.DateOnly; No civil dates in this library
  T:System.TimeOnly; No civil times in this library
  T:System.TimeZoneInfo; No zones in this library
  P:System.DateTimeOffset.Now; Use the injected TimeProvider.GetUtcNow()
  P:System.DateTimeOffset.UtcNow; Use the injected TimeProvider.GetUtcNow()
  M:System.Threading.Tasks.Task.Delay(System.TimeSpan); Use Task.Delay(TimeSpan, TimeProvider, CancellationToken)
  M:System.Threading.Tasks.Task.Delay(System.TimeSpan,System.Threading.CancellationToken); Use Task.Delay(TimeSpan, TimeProvider, CancellationToken)
  M:System.Threading.Tasks.Task.Delay(System.Int32); Use Task.Delay(TimeSpan, TimeProvider, CancellationToken)
  M:System.Threading.Tasks.Task.Delay(System.Int32,System.Threading.CancellationToken); Use Task.Delay(TimeSpan, TimeProvider, CancellationToken)
  M:System.Threading.CancellationTokenSource.#ctor(System.TimeSpan); Use new CancellationTokenSource(TimeSpan, TimeProvider)
  M:System.Threading.CancellationTokenSource.#ctor(System.Int32); Use new CancellationTokenSource(TimeSpan, TimeProvider)
  M:System.Threading.PeriodicTimer.#ctor(System.TimeSpan); Use new PeriodicTimer(TimeSpan, TimeProvider)
  T:System.Diagnostics.Stopwatch; Use TimeProvider.GetTimestamp / GetElapsedTime
  ```

- `System.Reflection.*` activation and invocation APIs, `Activator`, `Type.GetType`, `MakeGenericType`, assembly loading and scanning, `System.Reflection.Emit`.
- `Newtonsoft.Json`, `Grpc.Core`. `N:Grpc.Core` would also ban Grpc.Core.Api (`RpcException`, `Status`, `Metadata`), which `Grpc.Net.Client` needs, so only the types in the Grpc.Core package are listed and the package itself is banned by `WMN0001` [observed: scaffold, #2].
- `DllImportAttribute`.
- Timers that bypass `TimeProvider` beyond the list above (`System.Threading.Timer`, `System.Timers.Timer`), `Thread.Sleep`, sync-over-async (`Wait`, `Result`, `GetAwaiter().GetResult()`; a compiler-generated `await` is not flagged [observed]), `Environment.Exit` and `FailFast`.
- `Google.Protobuf.JsonFormatter` and `JsonParser`.
- The ways back to `DateTime` (`DateTimeOffset.DateTime`, `UtcDateTime`, `LocalDateTime`, the `DateTime` constructors and `op_Implicit`, `Timestamp.ToDateTime` / `FromDateTime`), `Task.WaitAsync` without a `TimeProvider`, `CancellationTokenSource.CancelAfter`, `Environment.TickCount` / `TickCount64`, `TimeProvider.System` (see Time), and `Expression.Compile`.
- Local time (`TimeProvider.GetLocalNow`, `DateTimeOffset.ToLocalTime`), and every `TimeSpan` or millisecond timeout overload in System.Threading: `SemaphoreSlim.Wait` / `WaitAsync`, `ManualResetEventSlim.Wait`, `CountdownEvent.Wait`, `Barrier.SignalAndWait`, `Monitor.Wait` / `TryEnter`, `Lock.TryEnter`, `SpinLock.TryEnter`, `SpinWait.SpinUntil`, `ReaderWriterLockSlim.TryEnter*Lock`, `Thread.Join`, `WaitHandle.WaitOne` / `WaitAll` / `WaitAny` / `SignalAndWait`, `ThreadPool.RegisterWaitForSingleObject`, `Task.Wait*`, `Timer.Change`. The list was generated from the reference assemblies [observed: Roslyn symbol scan, SDK 10.0.403]; `ITimer.Change` and `PeriodicTimer.Period` stay allowed, because those timers come from the `TimeProvider`.

An ID without a parameter list matches only the parameterless overload, so every overload is listed [observed: scaffold, #2].

## Do not

* Add an interface just to mock it. `IWhatsAppClient` is the only exception.
* Put a generated protobuf type in the stable public API. Only the `[Experimental]` `Raw` escape hatch may.
* Let `RpcException` escape the public API, or return Result types for I/O failures.
* Bind options by hand. See [options.md](options.md).
* Read the clock or start a timer without the injected `TimeProvider`. See Time.
* Add NodaTime to a shipped package. See Time.
* Log with `ILogger.LogXxx` extension calls. `[LoggerMessage]` only.
* Copy VsaTemplate handlers, Immediate.*, EF, Auth0, Hangfire, email, Linq2DB, or TUnit.
