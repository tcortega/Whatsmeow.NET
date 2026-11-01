# 0010. Vogen for primitive ID wrappers; `Jid` stays hand-written

- Status: Accepted; the `conversions:` line is superseded by [ADR 0020](0020-sessions-and-the-store.md) (`Conversions.None`)
- Date: 2026-10-31
- Evidence: [ARCHITECTURE §14](../ARCHITECTURE.md#spike-results-2026-10-31) question 5, "Vogen and options findings"

## Context

An earlier draft used hand-written `readonly record struct` IDs.
A `readonly record struct` cannot stop a silent `default`, and `MessageId` plus `SessionId` is the dedupe key of the at-least-once contract ([ADR 0003](0003-at-least-once-delivery-synchronousack-sqlite-outbox.md)).
The house rules use Vogen for primitive wrappers.

Measured with Vogen 8.0.7 [observed: §14 Spike results, "Vogen and options findings", and Question 5]:

- `default(T)` is a compile error (VOG009), and a defaulted field throws on `.Value` at runtime.
- Vogen needs a public `(string)` constructor on the `throws:` exception (VOG013).
- `Vogen.SharedTypes.dll` is a hard runtime dependency of consumers: the nupkg depends on `Vogen`, and a consumer without the dll crashes with `FileNotFoundException`.
- STJ source-gen inside the same assembly cannot see Vogen converters (it serialized `{}`); a consumer's STJ source-gen sees them through metadata, under AOT, with zero warnings.
- The only trim warnings came from `TypeDescriptor.GetConverter` on a Vogen type with `Conversions.Default`.
- Without `Omit`, Vogen emits a public `VogenTypesFactory` in the assembly root namespace [observed: generated code, per [guidelines/csharp.md](../guidelines/csharp.md#ids)].

## Decision

- `SessionId`, `MessageId` and later primitive wrappers are Vogen value objects.
- One assembly attribute in `Whatsmeow.Client` sets `throws: typeof(WhatsmeowValidationException)`, `conversions: Conversions.SystemTextJson` and `systemTextJsonConverterFactoryGeneration: Omit`.
- `WhatsmeowValidationException` has a public constructor taking one `string`.
- No `JsonSerializerContext` of ours contains a Vogen type; serialize `.Value` instead.
- Vogen is never `PrivateAssets="all"`; the `Vogen.SharedTypes` dependency is accepted.
- `Jid` is a hand-written `readonly struct Jid : ISpanParsable<Jid>`, because it is structured (user, server, device, PN/LID pair), not a wrapper around one primitive.

The rules are in [guidelines/csharp.md](../guidelines/csharp.md#ids).

## Consequences

- An uninitialized ID cannot silently become a dedupe key.
- Consumers get Vogen.SharedTypes (20 KB) as a transitive dependency.
- `ValueObjectValidationException` and `VogenTypesFactory` stay out of the public API.
- Consumer JSON works through the generated `[JsonConverter]` without the factory.
