# Go idioms

Read this when writing or changing Go under `sidecar/`.
The house rules have no Go guide yet; these rules are this repo's own.
Moved from ARCHITECTURE §7.9 and §11; what the sidecar does is ARCHITECTURE §7.

## Conventions

- `context.Context` is the first parameter of every function that does I/O or may block.
- Errors are wrapped with `%w`; there are sentinel errors per slice. Errors are mapped to `ErrorInfo` in exactly one place, `internal/server`.
- No `reflect`, no `unsafe`, no cgo, no package-level mutable state except the startup-only whatsmeow globals (ARCHITECTURE §7.2). Enforced with `golangci-lint` (`forbidigo`/`depguard`).
- Each slice talks to whatsmeow through a **small internal interface** (for example `type sender interface { SendMessage(...) }`), so tests can fake whatsmeow. `*whatsmeow.Client` satisfies it.
- Import the generated contract as `whatsmeowv1`. Never name that package `whatsmeow`. See ARCHITECTURE [Naming](../ARCHITECTURE.md#naming).
- Every goroutine has an owner, a cancellation path, and is waited on during shutdown (`errgroup`).
- Never regenerate whatsmeow's protos into our own packages; import whatsmeow's (ARCHITECTURE §13, invariant 11).

## Tests

- Every slice has unit tests with whatsmeow faked behind the slice's small interface.
- Event-mapping tables cover every event type.
- Outbox replay, ack and crash semantics are tested.
- `-race` always.
- These live in `sidecar/internal/**`.

The rest of [tests.md](tests.md) applies to Go too: the prime directive, write-it-so-it-can-fail, frozen tests, the banned list, and determinism.
