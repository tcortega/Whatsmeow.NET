# Whatsmeow.NET agent notes

.NET wrapper over the Go library whatsmeow: a CGO-free Go sidecar (`whatsmeow-net`) serves the `whatsmeow.v1` gRPC contract over a private local socket, and `Whatsmeow.Client` supervises it behind an idiomatic, AOT-safe C# API.
Phase 1: the walking skeleton (#4) starts, checks, restarts and stops the sidecar; Sessions (#6) adds the store, sessions and their state machine.
Read [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) before changing the contract, the delivery path, supervision, transport, or packaging.
[docs/RESEARCH.md](docs/RESEARCH.md) is the why with sources; reference only.

This is a library, not a VSA API. No Immediate.Handlers, `[Handler]`, Immediate.Injections, EF, or Refit.
Never over-engineer: build the simplest thing that meets the issue. No speculative options, layers, abstractions or "for later" code; add them when a real second use exists.
House rules (bridge, compras, chrominal) win over anything older in this repo, except the exceptions recorded in the ARCHITECTURE Decision log (BCL time, `IWhatsAppClient`).
Generated code is committed: never hand-edit `sidecar/gen/`, `Whatsmeow.Protocol*` or `sidecar/THIRD-PARTY-NOTICES.txt`; regenerate it (Commands).
ARCHITECTURE §13 invariants are wrong to break without an ADR first.

## Tree

proto/whatsmeow/v1/    our contract, one file per slice, plus error.proto (the ErrorInfo reason list)
proto/whatsmeow/fake/  the fake WhatsApp's test-only control, not the contract (ADR 0019)
third_party/whatsmeow/ upstream protos copied from the exact pinned version (`VERSION`)
sidecar/               Go module `github.com/tcortega/Whatsmeow.NET/sidecar`: product code and dependencies only
tools/                 Go module for repo tools (`check-repo`, `sync-protos`, `check-licenses`, the tests' `stub-sidecar`), kept out of the product module
build/ci.sh            every CI check; the workflow only calls it
dotnet/                `Whatsmeow.slnx` (never a .sln), src/, tests/; samples/ later (Phase 3)
docs/                  ARCHITECTURE, RESEARCH, guidelines/, adr/
global.json mise.toml  .NET SDK pin; Go, buf, golangci-lint, protoc, protoc plugins and shellcheck pins
nuget.config           nuget.org only
AGENTS.md              this file (CLAUDE.md is a symlink to it; nothing may depend on it)

## Commands

Setup once: the .NET SDK from `global.json` (10.0.403), then `mise trust && mise install` for Go, buf, golangci-lint, protoc, the protoc plugins and shellcheck.

Everything CI runs is `build/ci.sh`, one subcommand per CI job, on linux-x64, osx-arm64 and win-x64 (Git Bash on Windows); `package` runs on win-x64 only, and `install` installs its feed on linux-x64 and osx-arm64.
Run it from anywhere before pushing; it runs every check, reports each failure, and fails at the end:

```sh
bash build/ci.sh all        # or: setup, contract, go, dotnet, package; install <feed> installs a feed built elsewhere
```

A package change regenerates `packages.lock.json` with a plain `dotnet restore dotnet/Whatsmeow.slnx`; commit it.
Update third_party after a pin change with `go -C tools run ./sync-protos -write`, and generated code with `buf generate` (whatsmeow.v1) and `buf generate --template buf.gen.whatsapp.yaml` (upstream).
After a reviewed `sidecar/licenses.allow` edit, regenerate the native packages' notices with `go -C tools run ./check-licenses -write` (ADR 0029).

## .NET rules

Tabs, file-scoped namespaces, house `.editorconfig` at the root.
No record primary constructors. Seal types. `var` everywhere.
No `else`. Guards, early return, switch expressions.
Throw helpers on every `WhatsmeowException` subtype; never `throw new` at the call site.
Time is BCL (`DateTimeOffset` UTC, `TimeSpan`) from an injected `TimeProvider`; this library opts out of the house NodaTime rule. No `DateTime`, no ambient now, no timer without the `TimeProvider`.
`SessionId` / `MessageId` are Vogen; `Jid` is hand-written.
No runtime reflection. Source generators, `[LoggerMessage]`, `[LibraryImport]`, `JsonSerializerContext`.
`ConfigureAwait(false)` everywhere. `CancellationToken` last, `Async` suffix. No sync-over-async.
No interface just to mock it. `IWhatsAppClient` is the only exception.
Generated protobuf types never in the stable public API, except the `[Experimental]` `Raw` hatch.
Global usings live in the csproj `ItemGroup Label="Usings"`.
C# idioms: [docs/guidelines/csharp.md](docs/guidelines/csharp.md).
Options and configuration: [docs/guidelines/options.md](docs/guidelines/options.md).

## Go rules

`context.Context` first. `%w` errors, mapped to `ErrorInfo` only in `internal/server`.
No `reflect`, `unsafe`, cgo, or mutable globals. Import the contract as `whatsmeowv1`.
Every logger derives from `newLogger`, whose writer redacts phone numbers and JID user parts at info and above; `server.toStatus` redacts status messages (ADR 0039). Never write stderr another way.
The whatsmeow event handler only writes to the outbox (or what invariant 1 lists); it never blocks on .NET, the network, or media.
whatsmeow facts (ack gating, off-queue events, retries) are measured at the pin in ARCHITECTURE §14; re-check on every bump, never from memory.
Go idioms: [docs/guidelines/go.md](docs/guidelines/go.md).

## Contract rules

From the first published `v<semver>` tag (pre-releases and build metadata included), `whatsmeow.v1` never breaks (a break is a new `whatsmeow.v2` with its ADR), every additive change bumps `protocol`, and `buf breaking` gates; before it, `protocol` keeps its initial value, 1, and `buf breaking` only reports (ADR 0015).
Never reuse or renumber a field.
Proto style: [docs/guidelines/proto.md](docs/guidelines/proto.md).

## Gold-standard slice

Sidecar (#4). Copy it for the next slice; do not invent a second pattern.

* Proto: `proto/whatsmeow/v1/sidecar.proto`.
* Go slice: `sidecar/internal/sidecar/` behind `sidecar/internal/server/` (lenient codec, token check, shutdown gate, the one ErrorInfo mapping); its tests run whatsmeow-free over `bufconn`. A slice that fails with a reason wraps `server.Err<Reason>` (`sidecar/internal/session/` is the first); one that talks to whatsmeow declares a small `Client` interface and tests it with a stub on a real store.
* .NET: the entry point and shared public types at the project root in namespace `Whatsmeow` (`WhatsmeowSidecar.cs`, `WhatsmeowException.cs`, ...); internals in `Sidecar/` (process, supervisor, proto to record mapping) and `Core/<Area>/` (`Core/Errors/RpcErrors.cs` maps every `RpcException`). Namespaces: ADR 0018.
* Tests: FullCycle `dotnet/tests/Whatsmeow.IntegrationTests/Sidecar/GetInfoTests.cs` on the shared `FakeSidecar` fixture (one fake-mode sidecar for the run, ADR 0019); every later slice's FullCycle uses it. `FakeTimeProvider` unit tests in `dotnet/tests/Whatsmeow.Client.Tests/Sidecar/`.
* Only start and stop tests (`Sidecar/SidecarTests.cs`, on the `SidecarBinaries` fixture) start a sidecar per test, because that is the behaviour under test; so do the events tests that stop or kill the .NET side (`Events/EventProcessTests.cs`, with the fixture's fake build), the hosting tests, whose host starts and stops the sidecar (`Hosting/HostingTests.cs`), and the send pacing test, whose options apply at start (`Messaging/SendPacingTests.cs`).
* Events: a slice that records whatsmeow events in the outbox maps them with its own `events.Mapper` (`contacts.Event`), passed to `events.New` in `cmd/whatsmeow-net` (ADR 0028).
* Fake controls: an RPC in `proto/whatsmeow/fake/v1/fake.proto`, implemented in `sidecar/internal/fakewhatsapp/` and tested there over `bufconn` through the real `server.Server`.

## Where does X go

| Thing | Place |
| --- | --- |
| RPC / message for a slice | `proto/whatsmeow/v1/<slice>.proto` |
| Go slice (service impl, event mapping) | `sidecar/internal/<slice>/` |
| Go core (gRPC plumbing, error mapping, outbox, store, log) | `sidecar/internal/{server,transport,outbox,store,log}/` |
| Process entry (flags, tether, ready line) | `sidecar/cmd/whatsmeow-net/` |
| Entry point and public types shared across slices (`WhatsmeowSidecar`, options, `SidecarInfo`, `WhatsmeowException` and subtypes, later `Jid`, ids, `IWhatsAppClient`), namespace `Whatsmeow` | `dotnet/src/Whatsmeow.Client/` (project root) |
| Slice-only public records, namespace `Whatsmeow.<Slice>`; slice internals and proto ↔ record mapping | `dotnet/src/Whatsmeow.Client/<Slice>/` |
| Transport, dispatcher, error mapping, telemetry (internal) | `dotnet/src/Whatsmeow.Client/Core/<Area>/` |
| A schema change to the sidecar's tables | a new numbered step in `sidecar/internal/store/store.go` (`steps()`), never an edit to a merged one (ADR 0022) |
| New error reason | `ErrorReason` in `proto/whatsmeow/v1/error.proto` + exception subtype (§8.7) |
| DI, options, hosted supervisor | `dotnet/src/Whatsmeow.Extensions.Hosting/` |
| Consumer fakes | `dotnet/src/Whatsmeow.Testing/`; their tests, public API only, in `dotnet/tests/Whatsmeow.Testing.Tests/` (ADR 0036) |
| Integration test (default) | `dotnet/tests/Whatsmeow.IntegrationTests/<Slice>/`, on the `FakeSidecar` fixture |
| Live test (real WhatsApp, opt-in) | `dotnet/tests/Whatsmeow.LiveTests/`, `[LiveFact]` in the `Live` collection, with the fake-mode checks (ADR 0021, 0032) |
| A live-only sidecar seam | a `livetest_*.go` pair in the slice, behind `-tags livetest`, never in a release binary; a slice other than events and messaging needs its directory added to `tools/check-repo` first (ADR 0032) |
| Fake WhatsApp control for a slice | `proto/whatsmeow/fake/v1/fake.proto` + `sidecar/internal/fakewhatsapp/` (ADR 0019) |
| Dispatcher / supervisor / request body fence unit test | `dotnet/tests/Whatsmeow.Client.Tests/` |
| A new module linked into the sidecar | read its licence, then its lines in `sidecar/licenses.allow` (ADR 0016) |
| Decision | `docs/adr/NNNN-title.md` |

## Tests

Integration tests are the default: a FullCycle through `IWhatsAppClient` against a real `whatsmeow-net --fake-whatsapp`.
New or changed tests: [docs/guidelines/tests.md](docs/guidelines/tests.md).

## Maintaining this file

Keep this file for knowledge useful to almost every future agent session in this project.
Do not repeat what the codebase already shows; point to the authoritative file or command instead.
Prefer rewriting or pruning existing entries over appending new ones.
When updating this file, preserve this bar for all agents and keep entries concise.
