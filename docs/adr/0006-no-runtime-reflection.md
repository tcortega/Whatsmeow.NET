# 0006. No runtime reflection; source generation and explicit registration

- Status: Accepted
- Date: 2026-10-31
- Evidence: [ARCHITECTURE §14](../ARCHITECTURE.md#spike-results-2026-10-31) question 5, "Vogen and options findings"

## Context

A goal is Native AOT and trimming compatibility with zero warnings ([ARCHITECTURE §1](../ARCHITECTURE.md#1-goals-and-non-goals)).
Runtime reflection (assembly scanning, `Activator`, `MakeGenericType`, descriptor-based protobuf APIs, reflection-based binding) is what breaks trimming.

Question 5, macOS arm64, .NET SDK 10.0.400, `PublishAot=true`, `TrimmerSingleWarn=false` [observed: §14 Spike results, Question 5]:

- Zero IL/AOT warnings for the 57 generated WhatsApp types, Google.Protobuf `Timestamp`/`Duration` conversions, Vogen value objects, Polly retry, the `[OptionsValidator]` generator and the configuration-binding generator.
- Every path ran correctly in the native binary.
- The only trim warnings came from a probe calling `TypeDescriptor.GetConverter` on a Vogen type with `Conversions.Default`.
- Not covered: `Grpc.Net.Client` and the generated `whatsmeow.v1` stubs, which do not exist yet.

A library with only `IsAotCompatible=true` does not get the configuration-binding generator: `BindConfiguration` raised IL2026/IL3050 until `EnableConfigurationBindingGenerator=true` was set [observed: §14 Spike results, "Vogen and options findings"].

## Decision

- No runtime reflection in library code; everything is generated at build time or written explicitly ([ARCHITECTURE §8.9](../ARCHITECTURE.md#89-reflection-policy)).
- Protobuf uses generated code; logging uses `[LoggerMessage]`; options validation uses `[OptionsValidator]`; JSON uses a `JsonSerializerContext`; native interop uses `[LibraryImport]` and CsWin32; IDs use Vogen.
- Event dispatch is exhaustive pattern matching over sealed records; handler registration is explicit (`builder.AddHandler<THandler>()`), never assembly scanning.
- Libraries that bind configuration set `EnableConfigurationBindingGenerator=true` explicitly.
- Vogen uses `Conversions.SystemTextJson` only ([ADR 0010](0010-vogen-ids-hand-written-jid.md)).
- Enforcement: `IsAotCompatible=true` and `IsTrimmable=true` on every shipped project, trim and AOT warnings as errors, `BannedApiAnalyzers` with `BannedSymbols.txt`, and `tests/AotSmoke` publishing with zero warnings.
- The full per-need table and the banned list live in [guidelines/csharp.md](../guidelines/csharp.md#reflection).
- Go follows the same rule: no `reflect`, `unsafe` or cgo ([guidelines/go.md](../guidelines/go.md)).

## Consequences

- Invariant 7 forbids runtime reflection ([ARCHITECTURE §13](../ARCHITECTURE.md#13-invariants)).
- `tests/AotSmoke` stays the gate, because the spike did not cover `Grpc.Net.Client` or the `whatsmeow.v1` stubs.
- Handler discovery by source generator may come later; assembly scanning never.
- A dependency that needs reflection is rejected or wrapped behind generated code.
