# Proto

Read this when adding or changing a `.proto` under `proto/whatsmeow/v1/`.
Moved from ARCHITECTURE §6; the contract's policy (independence from whatsmeow, upstream typed fields, the service shape, the error model) stays there.

## Style

- proto3, buf STANDARD lint, Google AIP-191 style: one `XxxRequest`/`XxxResponse` per RPC, `_UNSPECIFIED` zero enum values, `Timestamp`/`Duration` well-known types.
- One file per slice (ARCHITECTURE §3).
- Never reuse or renumber fields. Reserve the number and name of any removed field.
- Error `reason` values are stable `SCREAMING_SNAKE` codes, and the list of reasons is part of the contract.

## Mapping in .NET

- `google.protobuf.Timestamp` maps to NodaTime `Instant`, and `google.protobuf.Duration` to NodaTime `Duration`, in slice mapping code. Never to BCL types (see [csharp.md](csharp.md) Time).
- `NodaTime.Serialization.Protobuf` provides these conversions [observed: package 2.0.2 on nuget.org, published 2025-01; whether its API fits AOT is part of ARCHITECTURE §14 spike 5].

## Changing the contract

- Run `buf lint` and `buf breaking` against `main` before pushing.
- Regenerate (`buf generate`) and commit the generated Go and C#. CI fails on a diff.
- Any change to `whatsmeow.v1` increments `protocol` (ARCHITECTURE §12, §13 invariant 5).
