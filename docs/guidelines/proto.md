# Proto

Read this when adding or changing a `.proto` under `proto/whatsmeow/v1/`.
Moved from ARCHITECTURE §6; the contract's policy (independence from whatsmeow, upstream typed fields, the service shape, the error model) stays there.

## Style

- proto3, buf STANDARD lint, Google AIP-191 style: one `XxxRequest`/`XxxResponse` per RPC, `_UNSPECIFIED` zero enum values, `Timestamp`/`Duration` well-known types.
- One file per slice (ARCHITECTURE §3).
- `proto/whatsmeow/fake/v1/fake.proto` is the fake WhatsApp's control (ADR 0019), not the contract: it is linted, in `breaking.ignore`, adds no `ErrorReason`, never ships, and its C# is generated into the integration tests only.
- Never reuse or renumber fields. Reserve the number and name of any removed field.
- Error `reason` values are stable `SCREAMING_SNAKE` codes, and the list of reasons is part of the contract: `enum ErrorReason` in `error.proto`, sent without its `ERROR_REASON_` prefix.

## Mapping in .NET

- `google.protobuf.Timestamp` maps to `DateTimeOffset` (UTC), and `google.protobuf.Duration` to `TimeSpan`, in slice mapping code (see [csharp.md](csharp.md) Time).
- Use Google.Protobuf's own `Timestamp.ToDateTimeOffset()` / `Timestamp.FromDateTimeOffset()` and `Duration.ToTimeSpan()` / `Duration.FromTimeSpan()`. No extra package. They round-trip under Native AOT with zero warnings [observed: Google.Protobuf 3.36.2, ARCHITECTURE §14 Spike results].

## Changing the contract

- Run `buf format proto -w`, `buf lint` and `buf breaking` against `main` before pushing.
- Regenerate (`buf generate`) and commit the generated Go and C#. CI fails on a diff.
- Every plugin is local and pinned in `mise.toml`: remote buf.build plugins hit its rate limit even locally [observed: `resource_exhausted: too many requests`, 2026-10-31]; never fix that with retries.
- From the first published `v<semver>` tag (pre-releases and build metadata included) on, `whatsmeow.v1` never breaks: a break means a new package `whatsmeow.v2` with its own ADR, every additive change increments `protocol`, and `buf breaking` gates `whatsmeow.v1` against `main`.
  Before that tag, `protocol` keeps its initial value, 1, and `buf breaking` only reports ([ADR 0015](../adr/0015-buf-breaking-gates-from-first-published-tag.md)).
  ARCHITECTURE §6, §11, §12 and §13 invariant 5 say the same.
  `build/ci.sh` (`buf_breaking`) implements the gate: a published tag is a `v<semver>` tag reachable from the comparison base.
  With one, a break (`buf breaking` exit 100) fails the build; without one, it is a warning; any other exit code fails either way.
  A pull request compares with its merge-base with `main`, never with a stacked base branch; a push to `main` compares with the commit `main` pointed at before; `workflow_dispatch` skips the step.
  The base must exist in the clone, and both sides must compile (`buf build`), or the step fails; a base without `buf.yaml` (before #2 merged) or without `proto/whatsmeow/v1` (before #3 merged) is skipped with a notice, because buf fails when the base has fewer modules [observed: buf 1.75.0, "input contained 2 images, whereas against contained 1 images"].
