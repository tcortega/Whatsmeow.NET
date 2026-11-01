# Whatsmeow.NET agent notes

.NET wrapper over the Go library whatsmeow: a CGO-free Go sidecar (`whatsmeow-net`) serves the `whatsmeow.v1` gRPC contract over a private local socket, and `Whatsmeow.Client` supervises it behind an idiomatic, AOT-safe C# API.
No product code yet. Phase 0.
Read [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) before changing the contract, the delivery path, supervision, transport, or packaging.
[docs/RESEARCH.md](docs/RESEARCH.md) is the why with sources; reference only.

This is a library, not a VSA API. No Immediate.Handlers, `[Handler]`, Immediate.Injections, EF, or Refit.
House rules (bridge, compras, chrominal) win over anything older in this repo, except the exceptions recorded in the ARCHITECTURE Decision log (BCL time, `IWhatsAppClient`).
Generated code is committed: never hand-edit `sidecar/gen/` or `Whatsmeow.Protocol*`; regenerate it (Commands).
ARCHITECTURE §13 invariants are wrong to break without an ADR first.

## Tree

proto/whatsmeow/v1/    our contract, one file per slice (from #3)
third_party/whatsmeow/ upstream protos copied from the exact pinned version (`VERSION`)
sidecar/               Go module `github.com/tcortega/Whatsmeow.NET/sidecar`; `scripts/` holds the repo tools
dotnet/                `Whatsmeow.slnx` (never a .sln), src/, tests/; samples/ later (Phase 3)
docs/                  ARCHITECTURE, RESEARCH, guidelines/, adr/
global.json mise.toml  .NET SDK pin; Go, buf, golangci-lint and protoc pins
nuget.config           nuget.org only
AGENTS.md              this file (CLAUDE.md is a symlink to it; nothing may depend on it)

## Commands

Setup once: the .NET SDK from `global.json` (10.0.403), then `mise trust && mise install` for Go, buf, golangci-lint and protoc.
Run from the repo root, in a POSIX shell (Git Bash on Windows).

Pre-push: exactly what CI (`.github/workflows/ci.yml`) runs on linux-x64, osx-arm64 and win-x64, with warnings as errors.

```sh
go -C sidecar run ./scripts/check-repo                                   # suppressions, generated markers, analyzer configs
dotnet restore dotnet/Whatsmeow.slnx --locked-mode
dotnet build dotnet/Whatsmeow.slnx -c Release --no-restore -p:ContinuousIntegrationBuild=true
dotnet test --solution dotnet/Whatsmeow.slnx -c Release --no-build
dotnet publish dotnet/tests/AotSmoke/AotSmoke.csproj -c Release --no-restore -p:ContinuousIntegrationBuild=true   # zero trim/AOT warnings, host RID
(cd sidecar && go mod tidy -diff && CGO_ENABLED=0 go build ./... && go test -race -count=1 ./... && CGO_ENABLED=1 golangci-lint run ./...)
go -C sidecar run ./scripts/sync-protos                                  # third_party matches the pin; -write to update
buf lint
buf generate --template buf.gen.whatsapp.yaml && git status --porcelain   # must print nothing; never edit generated code
```

A package change regenerates `packages.lock.json` with a plain `dotnet restore dotnet/Whatsmeow.slnx`; commit it.

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
The whatsmeow event handler only writes to the outbox; it never blocks on .NET, the network, or media.
whatsmeow facts (ack gating, off-queue events, retries) are measured at the pin in ARCHITECTURE §14; re-check on every bump, never from memory.
Go idioms: [docs/guidelines/go.md](docs/guidelines/go.md).

## Contract rules

From the first published `v*` tag (pre-releases included), `whatsmeow.v1` never breaks (a break is a new `whatsmeow.v2` with its ADR), every additive change bumps `protocol`, and `buf breaking` gates; before it, `protocol` keeps its initial value, 1, and `buf breaking` only reports (ADR 0015).
Never reuse or renumber a field.
Proto style: [docs/guidelines/proto.md](docs/guidelines/proto.md).

## Gold-standard slice

None yet.
The first complete Phase 1 slice (proto + Go + .NET + FullCycle) becomes the reference; name it here.
Copy it for the next slice. Do not invent a second pattern.

## Where does X go

| Thing | Place |
| --- | --- |
| RPC / message for a slice | `proto/whatsmeow/v1/<slice>.proto` |
| Go slice (service impl, event mapping) | `sidecar/internal/<slice>/` |
| Go core (gRPC plumbing, error mapping, outbox, store, log) | `sidecar/internal/{server,transport,outbox,store,log}/` |
| Process entry (flags, tether, ready line) | `sidecar/cmd/whatsmeow-net/` |
| .NET public types + proto ↔ record mapping | `dotnet/src/Whatsmeow.Client/<Slice>/` |
| `Jid`, ids, `MediaRef`, transport, dispatcher, errors, telemetry | `dotnet/src/Whatsmeow.Client/Core/` |
| New error reason | `ErrorInfo.reason` list (ARCHITECTURE §6) + exception subtype (§8.7) |
| DI, options, hosted supervisor | `dotnet/src/Whatsmeow.Extensions.Hosting/` |
| Consumer fakes | `dotnet/src/Whatsmeow.Testing/` |
| Integration test (default) | `dotnet/tests/Whatsmeow.IntegrationTests/` |
| Dispatcher / supervisor unit test | `dotnet/tests/Whatsmeow.Client.Tests/` |
| Decision | `docs/adr/NNNN-title.md` |

## Tests

Integration tests are the default: a FullCycle through `IWhatsAppClient` against a real `whatsmeow-net --fake-whatsapp`.
New or changed tests: [docs/guidelines/tests.md](docs/guidelines/tests.md).

## Maintaining this file

Keep this file for knowledge useful to almost every future agent session in this project.
Do not repeat what the codebase already shows; point to the authoritative file or command instead.
Prefer rewriting or pruning existing entries over appending new ones.
When updating this file, preserve this bar for all agents and keep entries concise.
