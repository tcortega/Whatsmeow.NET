# C# idioms

Read this when writing or changing C# under `dotnet/`.
The control flow and type rules are the house rules from bridge, compras and chrominal (VsaTemplate lineage).
This is a library, not a VSA API: no Immediate.Handlers, no `[Handler]`, no Immediate.Injections or Immediate.Validations.
A slice here is a WhatsApp capability with a feature group on `IWhatsAppClient` (see [ARCHITECTURE.md](../ARCHITECTURE.md) §3).
No reference slice exists yet; the first complete Phase 1 slice becomes it and is named in `AGENTS.md`.

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
That is VSA0002: positional records fight queries and make refactors lie.

```csharp
public sealed record SendResult
{
	public required MessageId Id { get; init; }
	public required Jid Chat { get; init; }
	public required Instant Timestamp { get; init; }
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
- Time is NodaTime (see Time). Public members expose `Instant` and `Duration`, never BCL date/time types.
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

Why Vogen in a public library: its analyzers reject `default(SessionId)` and `new SessionId()`, and its generated guard throws on an uninitialized instance.
A hand-written `readonly record struct` cannot stop a silent default, and `MessageId` plus `SessionId` is the dedupe key (ARCHITECTURE §8.4).

Rules:

- One `[assembly: VogenDefaults(throws: typeof(WhatsmeowValidationException))]` in `Whatsmeow.Client`, so no `ValueObjectValidationException` leaks into the public API.
  Vogen requires that exception to have a specific public constructor [unknown: the docs wording is ambiguous; confirm at scaffold time].
- Vogen ships a runtime assembly, `Vogen.SharedTypes.dll` (20 KB, no further dependencies) [observed: Vogen 8.0.7 nupkg], so consumers get it transitively. That is accepted.
- `conversions:` is decided by ARCHITECTURE §14 spike 5 (zero AOT/trim warnings). Start with `Conversions.SystemTextJson` only. `TypeConverter` goes through `System.ComponentModel` [inferred: may warn under trimming].
- `Jid` is a `readonly struct` implementing `ISpanParsable<Jid>`, with no positional members. It carries both PN and LID forms where known.

## Time

Time is NodaTime.
Never `DateTime`, `DateTimeOffset`, `DateOnly`, `TimeOnly`, `TimeZoneInfo`, or `TimeProvider`.
`Instant` is a point in time.
`LocalDate` / `LocalTime` / `LocalDateTime` are civil values with no zone.
`Duration` is a fixed length.
`Period` is a calendrical length.
`DateTimeZone` is an IANA zone.
Now is `IClock`: default to `SystemClock.Instance`, inject it, tests use `FakeClock` (`NodaTime.Testing`).
Do not call `SystemClock.Instance` at a call site.
`TimeSpan` only at BCL interop (timeouts, delays, Polly, gRPC deadlines).
Do not store it, and do not expose it publicly.
Proto `Timestamp` / `Duration` map to `Instant` / `Duration` in slice mapping code (see [proto.md](proto.md)).

Open: Polly v8, `Task.Delay`, `PeriodicTimer` and CTS timeouts are driven by `TimeProvider`/`TimeSpan` [observed: Polly `ResiliencePipelineBuilder.TimeProvider`].
With `TimeProvider` banned, `FakeClock` cannot advance them.
How we bridge that is ARCHITECTURE §14 question 7. Until it is decided, do not scatter `#pragma` suppressions of RS0030.

## Async and concurrency

Moved from ARCHITECTURE §8.10.

- Use `System.Threading.Channels` for producer/consumer handoff and `System.Threading.Lock` for short critical sections. Never hold a lock across `await`.
- Never block on async code (`.Result`, `.Wait()`, `GetAwaiter().GetResult()`). Never write `async void`.
- `ConfigureAwait(false)` throughout library code. CA2007 is an error here, unlike the house `.editorconfig` (an ASP.NET app has no sync context; a library does not know its caller's).
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
- `IsAotCompatible=true` and `IsTrimmable=true` on every shipped project, with all trim and AOT warnings as errors.
- `Microsoft.CodeAnalysis.BannedApiAnalyzers` with `BannedSymbols.txt` (see below).
- `tests/AotSmoke` publishes with `PublishAot=true` and must produce zero warnings.

## Build baseline

Copy from tcortega/bridge when scaffolding `dotnet/`, then apply the deltas listed here.

- `.editorconfig` at the repo root: bridge's VsaTemplate file verbatim (tabs, file-scoped namespaces as error, Style analyzers at warning, `s_` / `_` field prefixes). Delta: `dotnet_diagnostic.CA2007.severity = error`.
- `Directory.Build.props`: `LangVersion latest`, `Nullable enable` with `WarningsAsErrors Nullable`, `ImplicitUsings`, `AnalysisLevel latest-all`, `EnforceCodeStyleInBuild`, `GenerateDocumentationFile` (the unused-usings analyzer needs it), `TreatWarningsAsErrors` on CI. Delta: `net10.0`, AOT/trim properties on shipped projects.
- `Directory.Build.targets`: bridge's source-control `AssemblyMetadata` target.
- `Directory.Packages.props`: Central Package Management with `CentralPackageTransitivePinningEnabled`; `Meziantou.Analyzer` and `Microsoft.CodeAnalysis.BannedApiAnalyzers` as `GlobalPackageReference`.
- `.slnx`, never `.sln`.

```xml
<ItemGroup>
	<GlobalPackageReference Include="Microsoft.CodeAnalysis.BannedApiAnalyzers" PrivateAssets="all" />
	<AdditionalFiles Include="$(MSBuildThisFileDirectory)BannedSymbols.txt" />
</ItemGroup>
```

### BannedSymbols.txt

Lives at `dotnet/BannedSymbols.txt`.
It must cover:

- BCL date/time: the house NodaTime set (`T:System.DateTime`, `T:System.DateTimeOffset`, `T:System.DateOnly`, `T:System.TimeOnly`, `T:System.TimeZoneInfo`, `T:System.TimeProvider`), each with its NodaTime replacement in the message.
- `System.Reflection.*` activation and invocation APIs, `Activator`, `Type.GetType`, `MakeGenericType`.
- `Newtonsoft.Json`, `Grpc.Core`.
- `DllImportAttribute`.

## Do not

* Add an interface just to mock it. `IWhatsAppClient` is the only exception.
* Put a generated protobuf type in the stable public API. Only the `[Experimental]` `Raw` escape hatch may.
* Let `RpcException` escape the public API, or return Result types for I/O failures.
* Bind options by hand. See [options.md](options.md).
* Use BCL date/time types. See Time.
* Log with `ILogger.LogXxx` extension calls. `[LoggerMessage]` only.
* Copy VsaTemplate handlers, Immediate.*, EF, Auth0, Hangfire, email, Linq2DB, or TUnit.
