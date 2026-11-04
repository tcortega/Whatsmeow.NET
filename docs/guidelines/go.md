# Go idioms

Read this when writing or changing Go under `sidecar/`.
The house rules have no Go guide yet; these rules are this repo's own.
Moved from ARCHITECTURE §7.9 and §11; what the sidecar does is ARCHITECTURE §7.

## Conventions

- `context.Context` is the first parameter of every function that does I/O or may block.
- Errors are wrapped with `%w`. A call fails with an `ErrorInfo` reason by wrapping that reason's sentinel from `internal/server` (`server.ErrSessionNotFound`, ...; `server.WithMetadata` and `server.InvalidArgument` add metadata): reasons are shared across slices, and core never imports a slice. Errors are mapped to `ErrorInfo` in exactly one place, `internal/server`: `depguard` denies `google.golang.org/grpc/status` and `google.golang.org/genproto/googleapis/rpc/errdetails` everywhere else, except in `_test.go` files, which read a returned status to check its reason.
- No `reflect`, no `unsafe`, no cgo, no package-level mutable state except the startup-only whatsmeow and libsignal globals (ARCHITECTURE §7.2; `internal/log.SetupSignal`, which a test package that creates devices in parallel calls from `TestMain`). Enforced with `golangci-lint` (`sidecar/.golangci.yml`): `depguard` denies `reflect` and `unsafe`, `gochecknoglobals` and `gochecknoinits` reject package state, `forbidigo` rejects `encoding.RegisterCodec(V2)`.
- No cgo, checked with cgo turned on, because `CGO_ENABLED=0` excludes cgo files by build constraint and nothing would see them: `go list` with `CGO_ENABLED=1` fails on any package with `CgoFiles`, and golangci-lint runs with `CGO_ENABLED=1`, where a cgo file's `import "C"` is reported as `unsafe` [observed: golangci-lint 2.15.0]. Builds use `CGO_ENABLED=0`. `build/ci.sh go` runs all of it for every module.
- Build tags are an allowlist (`tools/check-repo`): GOOS and GOARCH values, `fakewhatsapp` only in `sidecar/internal/fakewhatsapp/` and `sidecar/cmd/whatsmeow-net/` (so a slice is the same code in the test and release builds), and `ignore` only under `tools/`. Any other tag can hide code from build, test and lint. `// +build` lines are rejected. `build/ci.sh go` runs the sidecar's build, test, lint and cgo check once more with `-tags fakewhatsapp`.
- The fake WhatsApp (ADR 0019): `fakewhatsapp.Client` copies `*whatsmeow.Client`'s method signatures, so one slice interface takes both. A slice that needs a whatsmeow method adds it to the fake; a slice that needs a scripted behaviour adds an RPC to `proto/whatsmeow/fake/v1/fake.proto` and implements it in `internal/fakewhatsapp/control.go`.
- Two modules: `sidecar/` carries only product code and dependencies; repo tools live in `tools/` with their own `go.mod`. One `/.golangci.yml` lints both.
- Every error from another package is wrapped before it is returned (`wrapcheck`, `errorlint`).
- A `//nolint` names the linter and says why (`nolintlint`). No other suppression.
- Formatting is gofumpt plus goimports with the `github.com/tcortega/Whatsmeow.NET` local prefix (`golangci-lint fmt`).
- Each slice talks to whatsmeow through a **small internal interface** (for example `type sender interface { SendMessage(...) }`), so tests can fake whatsmeow. `*whatsmeow.Client` satisfies it.
- Import the generated contract as `whatsmeowv1`. Never name that package `whatsmeow`. See ARCHITECTURE [Naming](../ARCHITECTURE.md#naming).
- Every goroutine has an owner, a cancellation path, and is waited on during shutdown (`errgroup`). The one exception is a goroutine blocked reading stdin (the tether, the token): a stdin read cannot be interrupted portably, so it is left to end with the process.
- The gRPC server's lenient proto codec is installed with `grpc.ForceServerCodecV2`. Never `encoding.RegisterCodec`/`RegisterCodecV2`: that is package-level mutable state (ARCHITECTURE §6).
- Never trust a remembered whatsmeow behavior. ARCHITECTURE §14 "Other whatsmeow claims" and question 4 record what the pinned source does; re-check them on every whatsmeow bump.
- A string taken from WhatsApp data into a proto3 field goes through `server.Text`, which makes it UTF-8: protobuf refuses to marshal one that is not, and whatsmeow copies names and texts unchecked (ARCHITECTURE §7.3).
- Every write of the sidecar goes through the store's fair write gate: `store.Writer.Tx` for a transaction, `Exec` for one statement; inside `Tx`, write through the transaction with the context it gives, since a write on the gate there fails with `store.ErrNestedWrite`. `forbidigo` rejects `Exec`, `ExecContext`, `Begin`, `BeginTx`, `Conn`, `Prepare` and `PrepareContext` on a `*sql.DB` outside `internal/store` and tests (ADR 0040). It is a backstop: reads stay on the `*sql.DB`, so a `QueryContext` or `QueryRowContext` that writes (`INSERT ... RETURNING`) gets past it; never write through one.
- Handlers return in milliseconds: whatsmeow abandons a node after about 5 minutes and moves the queue on (ARCHITECTURE §7.3).
- Retry what whatsmeow does not: uploads have no retry in whatsmeow; downloads do (ARCHITECTURE §7.6).
- Never regenerate whatsmeow's protos into our own packages; import whatsmeow's (ARCHITECTURE §13, invariant 11).

## Tests

- Every slice has unit tests with whatsmeow faked behind the slice's small interface.
- Event-mapping tables cover every event type.
- Outbox replay, ack and crash semantics are tested.
- `-race` always.
- The fake WhatsApp's own tests keep whatsmeow's device store in SQLite's memdb VFS (`openDeviceStore`), not in a file: they check the fake, `internal/store` checks the file, and on a loaded win-x64 runner a store file's creation and flushes took up to 25.6 s of a test's 30 s bound (#93, ARCHITECTURE §14). A slice's tests keep the sidecar's file store.
- These live in `sidecar/internal/**`.

The rest of [tests.md](tests.md) applies to Go too: the prime directive, write-it-so-it-can-fail, frozen tests, the banned list, and determinism.
