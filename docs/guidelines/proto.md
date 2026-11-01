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
- From the first published `v*` tag (pre-releases included) on, `whatsmeow.v1` never breaks: a break means a new package `whatsmeow.v2` with its own ADR, every additive change increments `protocol`, and `buf breaking` gates `whatsmeow.v1` against `main`.
  Before that tag, `protocol` keeps its initial value, 1, and `buf breaking` only reports ([ADR 0015](../adr/0015-buf-breaking-gates-from-first-published-tag.md)).
  ARCHITECTURE §6, §11, §12 and §13 invariant 5 say the same.
  CI treats any tag reachable from the comparison base that matches `^v[0-9]+\.[0-9]+\.[0-9]+(-[0-9A-Za-z.-]+)?(\+[0-9A-Za-z.-]+)?$` as published, so a published release candidate starts the gate too.
  With one, a break (`buf breaking` exit 100) fails the build; without one, it is a CI warning; any other exit code fails either way.
  A pull request compares with its merge-base with `main`, never with a stacked base branch; a push to `main` compares with the commit `main` pointed at before.
- `buf.yaml` declares the `proto` module with its first file (#3): buf rejects a module with no `.proto` files [observed: buf 1.75.0].
