# Proto

Read this when adding or changing a `.proto` under `proto/whatsmeow/v1/`.
Moved from ARCHITECTURE §6; the contract's policy (independence from whatsmeow, upstream typed fields, the service shape, the error model) stays there.

## Style

- proto3, buf STANDARD lint, Google AIP-191 style: one `XxxRequest`/`XxxResponse` per RPC, `_UNSPECIFIED` zero enum values, `Timestamp`/`Duration` well-known types.
- One file per slice (ARCHITECTURE §3).
- Never reuse or renumber fields. Reserve the number and name of any removed field.
- Error `reason` values are stable `SCREAMING_SNAKE` codes, and the list of reasons is part of the contract.

## Mapping in .NET

- `google.protobuf.Timestamp` maps to `DateTimeOffset` (UTC), and `google.protobuf.Duration` to `TimeSpan`, in slice mapping code (see [csharp.md](csharp.md) Time).
- Use Google.Protobuf's own `Timestamp.ToDateTimeOffset()` / `Timestamp.FromDateTimeOffset()` and `Duration.ToTimeSpan()` / `Duration.FromTimeSpan()`. No extra package. They round-trip under Native AOT with zero warnings [observed: Google.Protobuf 3.36.2, ARCHITECTURE §14 Spike results].

## Changing the contract

- Run `buf lint` and `buf breaking` against `main` before pushing.
- Regenerate (`buf generate`) and commit the generated Go and C#. CI fails on a diff.
- Any change to `whatsmeow.v1` increments `protocol` (ARCHITECTURE §12, §13 invariant 5).
