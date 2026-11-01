# Architecture

> **Status:** v1 design, frozen for implementation.
> Changes go through an ADR (see [Decision log](#decision-log)).
> **Name:** Whatsmeow.NET.
> Identifiers are in [Naming](#naming).
> **Background:** the full research behind every decision here is in [`RESEARCH.md`](RESEARCH.md).
> This document states *what we do*.
> The research explains *why*, with sources.
> **How to write the code:** [`guidelines/`](guidelines/README.md).
> Coding rules that used to live here (§6 proto style, §7.9, the idiom half of §8.3, §8.9 table, §8.10, test discipline) moved there; each spot keeps a pointer.
> **House rules win:** where this document and the house guidelines (shared with bridge, compras, chrominal) disagreed, the house rule was applied, except where a measured reason made this library opt out (BCL time, `IWhatsAppClient`).
> Both kinds are ADRs in the [Decision log](#decision-log).

Whatsmeow.NET gives .NET applications full access to WhatsApp's multi-device protocol by wrapping the Go library [whatsmeow](https://github.com/tulir/whatsmeow). It has two parts, released together from one repo:

1. **`whatsmeow-net`**: a thin, CGO-free Go sidecar. It hosts one or more whatsmeow clients and serves the `whatsmeow.v1` gRPC API over a private local socket.
2. **The .NET library** (`Whatsmeow.Client` and friends): spawns and supervises `whatsmeow-net`, and exposes an idiomatic, AOT-safe C# API. gRPC and protobuf never leak into the public surface, except through an explicitly unstable `Raw` escape hatch.

The bot built on top is **out of scope** for this repository.

## Naming

The product is **Whatsmeow.NET**, the same name as the repo.
Code identifiers follow the Akka.NET and Quartz.NET pattern: the brand keeps the `.NET` suffix, and packages, namespaces and types drop it.
An earlier draft called the project Wameow.
That name is retired, including in the Go layer.

The sidecar is not a second product.
Its names still land in the contract, in process listings and in generated code, so they use this identity instead of a leftover codename.
Upstream whatsmeow stays lowercase `whatsmeow` in prose, so the two stay easy to tell apart.

| Kind | Name | Notes |
|---|---|---|
| Product, repo, docs | Whatsmeow.NET | not a C# namespace |
| NuGet and C# root | `Whatsmeow` | `Whatsmeow.Client`, `WhatsmeowSidecar`, `WhatsmeowException` |
| Proto package | `whatsmeow.v1` | `proto/whatsmeow/v1/`. Our contract. Upstream packages stay `WAWebProtobufs*` |
| Generated Go package | `whatsmeowv1` | `go_package` is `github.com/tcortega/Whatsmeow.NET/sidecar/gen/whatsmeow/v1;whatsmeowv1`, so it does not collide with `go.mau.fi/whatsmeow` |
| Go module | `github.com/tcortega/Whatsmeow.NET/sidecar` | case matches the GitHub repo. Do not lowercase it in `go.mod` |
| Sidecar binary | `whatsmeow-net` | `whatsmeow-net.exe` on Windows. Not `whatsmeowd`, which reads as an upstream daemon |
| Env var | `WHATSMEOW_NET_SIDECAR_PATH` | overrides binary location |
| ErrorInfo domain | `whatsmeow.net` | reason-code namespace, not a DNS claim |
| Default DB file | `whatsmeow-net.db` | avoids colliding with a whatsmeow or mautrix database in the same directory |
| Windows pipe prefix | `\\.\pipe\whatsmeow-net-` | plus a 128-bit random suffix |
| ActivitySource, Meter | `Whatsmeow.Client` | public constants |
| Sidecar log category | `Whatsmeow.Sidecar` | stderr is forwarded here |

---

## 1. Goals and non-goals

**Goals**

- Feature parity with whatsmeow for personal and group messaging, media, contacts, groups, presence, receipts and history sync.
- **No lost messages.** Delivery is at-least-once from WhatsApp all the way into user handlers, and survives a crash of either process, for every stanza kind whose server ack whatsmeow lets a handler withhold: decrypted messages, non-retry receipts, and `w:gp2` group notifications. Undecryptable messages are acked early by design, and their content is recovered by the retry protocol: it arrives later as a normal, gated message (§7.2 `AutomaticMessageRerequestFromPhone`). The remaining kinds (retry receipts, calls, app state, history-sync receipts, most notifications) are acked by whatsmeow regardless of our handler, so for those the guarantee is best effort. This matches how whatsmeow's maintainer runs it in production (§14 question 8).
- Absorb whatsmeow's weekly churn. A whatsmeow bump changes Go adapter code only. It never changes the `whatsmeow.v1` contract or the public C# API.
- Modern and strict: `net10.0` / C# 14, Native AOT and trimming compatible, no runtime reflection, latest stable toolchains.
- Zero setup for consumers. `dotnet add package` is enough; the sidecar binary ships inside NuGet.

**Non-goals (v1)**

- Bot framework features: commands, middleware, conversation state. Those belong to the bot.
- Long-term message storage. The application owns history; the sidecar keeps only an operational cache.
- Media processing in Go. Thumbnails, transcoding and waveforms are done in .NET.
- Calls (whatsmeow only does call signaling), newsletters/channels, business features. Possible later.
- Being a network service. `whatsmeow-net` is a local child process. "External mode" (separate container) comes in Phase 4.

---

## 2. System overview

```mermaid
flowchart LR
  subgraph App[".NET process"]
    U["Application / bot"]
    subgraph Lib["Whatsmeow.Client"]
      F["Feature APIs<br/>Messaging · Media · Groups · Contacts · Presence · Pairing"]
      D["Dispatcher<br/>bounded Channel · per-chat ordering · watermark ack"]
      R["Event reader<br/>StreamEvents(after_seq)"]
      S["Sidecar supervisor<br/>locate · spawn · tether · handshake · health · restart"]
      T["Transport<br/>GrpcChannel over UDS / named pipe + token"]
    end
  end
  subgraph Go["whatsmeow-net (Go, CGO_ENABLED=0)"]
    G["gRPC server<br/>auth · logging · error mapping"]
    FS["Feature slices<br/>session · pairing · messaging · media · contacts · groups · presence · history"]
    O["Outbox<br/>durable seq · replay · ack cursors"]
    W["whatsmeow clients<br/>one per session"]
    DB[("SQLite (modernc)<br/>whatsmeow store + session + outbox")]
  end
  WA(("WhatsApp"))
  U --> F --> T
  T <-->|"gRPC / HTTP/2 h2c"| G
  T --> R --> D --> U
  S -. "spawn · stdin tether · stderr logs" .-> Go
  G --> FS --> W
  W -->|"handler: INSERT, return true"| O --> DB
  W <--> DB
  W <-->|"Noise WebSocket / HTTPS media"| WA
```

**Process model:** one `whatsmeow-net` process per host application, many sessions (WhatsApp accounts) per process, one SQLite database for all sessions.

---

## 3. Code organization: feature slices end-to-end

Vertical-slice thinking applies to this library. A **slice is a WhatsApp capability**, and it is cut the same way through all three layers:

| Slice | Proto file | Go package | .NET folder | Public entry point |
|---|---|---|---|---|
| Sidecar (process) | `sidecar.proto` | `internal/sidecar` | `Sidecar/` | `WhatsmeowSidecar` |
| Sessions | `session.proto` | `internal/session` | `Sessions/` | `WhatsmeowSidecar.Sessions` |
| Pairing | `session.proto` (`Pair`) | `internal/pairing` | `Pairing/` | `client.Pairing` |
| Events | `events.proto` | `internal/events` + `internal/outbox` | `Events/` | `client.On<T>`, handlers |
| Messaging | `message.proto` | `internal/messaging` | `Messaging/` | `client.Messages` |
| Media | `media.proto` | `internal/media` | `Media/` | `client.Media` |
| Contacts & LID | `contact.proto` | `internal/contacts` | `Contacts/` | `client.Contacts` |
| Groups | `group.proto` | `internal/groups` | `Groups/` | `client.Groups` |
| Presence & receipts | `message.proto` (presence RPCs) | `internal/presence` | `Presence/` | `client.Presence` |
| History sync | `events.proto` (history events) | `internal/history` | `History/` | events + `client.Messages.RequestHistoryAsync` |
| Chat state (Phase 3) | `chatstate.proto` | `internal/chatstate` | `ChatState/` | `client.Chats` |

**Rules**

- A slice owns its proto messages, Go service implementation, event mapping, .NET public types, proto↔record mapping and tests. Adding a capability means adding or extending **one** slice in all three layers.
- Slices do not call each other's internals. Shared needs go into the **core**, which is deliberately small:
  - Go core: `internal/server` (gRPC plumbing, interceptors, error mapping), `internal/transport`, `internal/outbox`, `internal/store`, `internal/log`.
  - .NET core: `Core/Transport`, `Core/Dispatching`, `Core/Errors`, `Core/Telemetry`, `Core/Primitives` (`Jid`, `MessageId`, `SessionId`, `MediaRef`).
- Slices may depend on core. Core never depends on a slice.

---

## 4. Repository layout

```
/AGENTS.md                         # agent entry point, progressive disclosure
/CLAUDE.md                         # symlink to AGENTS.md
/NOTICE                            # MPL-2.0 source offer for whatsmeow
/.editorconfig                     # house VsaTemplate file, tabs (see docs/guidelines/csharp.md)
/.gitattributes                    # LF everywhere, so generated and copied files are byte identical on Windows
/global.json                       # exact .NET SDK pin and the Microsoft.Testing.Platform runner; at the root so every cwd sees it
/mise.toml                         # exact pins for buf, golangci-lint and protoc
/docs/ARCHITECTURE.md              # this document
/docs/RESEARCH.md                  # research report (reference only)
/docs/guidelines/                  # how to write code: csharp, options, tests, go, proto
/docs/adr/                         # decision records, NNNN-title.md
/buf.yaml                          # buf v2 workspace; upstream protos excluded from lint/breaking
/buf.gen.yaml                      # whatsmeow.v1 to Go (sidecar/gen) and C# (Whatsmeow.Protocol); managed mode
/buf.gen.whatsapp.yaml             # upstream protos to C# (Whatsmeow.Protocol.WhatsApp); a separate template, since one plugin has one out dir
/proto/whatsmeow/v1/*.proto           # OUR contract, one file per slice
/third_party/whatsmeow/            # *.proto and LICENSE copied byte for byte from the EXACT pinned module + VERSION (a go.sum line)
/sidecar/                          # Go module github.com/tcortega/Whatsmeow.NET/sidecar
  cmd/whatsmeow-net/                     # entry point: flags, stdin token + tether, ready line, signals
  internal/<slice>/                # one package per slice (see §3)
  internal/{server,transport,outbox,store,log}/   # core
  gen/                             # buf-generated Go for whatsmeow.v1 only (never for upstream protos)
  scripts/sync-protos/             # checks (default) or rewrites (-write) third_party/whatsmeow against VERSION
  scripts/check-parity/            # whatsmeow Client method coverage report
  .golangci.yml                    # lint rules from docs/guidelines/go.md
/dotnet/
  Whatsmeow.slnx  Directory.Build.props  Directory.Build.targets  Directory.Packages.props
  BannedSymbols.txt  .globalconfig  # banned APIs; generated code exempt from the ban list
  src/Directory.Build.props        # everything under src/ ships: IsAotCompatible, IsTrimmable
  src/Whatsmeow.Protocol.WhatsApp/    # committed generated upstream C# (Whatsmeow.Proto.*) in Generated/, unstable
  src/Whatsmeow.Protocol/             # committed generated whatsmeow.v1 C# (internal where possible)
  src/Whatsmeow.Client/               # public API: Core/ + one folder per slice
  src/Whatsmeow.Extensions.Hosting/   # DI, options, keyed sessions, hosted supervisor
  src/Whatsmeow.Testing/              # FakeWhatsAppClient, in-memory transport
  src/Whatsmeow.Sidecar.Native.{win,linux,osx}/   # pack-only: runtimes/{rid}/native/whatsmeow-net[.exe]
  tests/Whatsmeow.Client.Tests/       # vital unit tests only: dispatcher, supervisor (FakeTimeProvider)
  tests/Whatsmeow.IntegrationTests/   # default: real whatsmeow-net in fake-WhatsApp mode
  tests/Whatsmeow.LiveTests/          # real WhatsApp; opt-in, never in default CI
  tests/AotSmoke/                  # PublishAot console app, zero warnings required
  samples/EchoBot/
/.github/workflows/  ci.yml  whatsmeow-bump.yml  release.yml
/.goreleaser.yaml
```

Generated code is **committed** and regenerated by CI (`buf generate` plus a diff check). Consumers never need protoc.

Scaffolded in Phase 0: everything above except `NOTICE`, `buf.gen.yaml` and `proto/` (#3), `cmd/` and `internal/` (#4 onward), `gen/` (#3), `scripts/check-parity/`, `Whatsmeow.Extensions.Hosting` and `Whatsmeow.Testing` (Phase 3), `Whatsmeow.Sidecar.Native.*` (Phase 4), `Whatsmeow.LiveTests` (#7), `samples/EchoBot/` (Phase 3), `whatsmeow-bump.yml`, `release.yml` and `.goreleaser.yaml` (Phase 4).

---

## 5. Technology baseline

Re-verify exact versions on nuget.org and proxy.golang.org when scaffolding. "Latest stable" wins over the numbers below.
Each version has one pin: the Go toolchain in `sidecar/go.mod`, the .NET SDK in `global.json`, packages in `dotnet/Directory.Packages.props`, buf, golangci-lint and protoc in `mise.toml`, CI actions by commit SHA in `.github/workflows/ci.yml`.
Last verified as latest stable on 2026-10-31 [observed: go.dev/dl, builds.dotnet.microsoft.com, nuget.org, proxy.golang.org, GitHub releases]: Go 1.27.3, .NET SDK 10.0.403 (runtime 10.0.14), buf 1.75.0, protoc 36.2, golangci-lint 2.15.0, Google.Protobuf 3.36.2, Meziantou.Analyzer 3.0.318, BannedApiAnalyzers 5.6.0, xunit.v3 4.0.2.

| Area | Choice |
|---|---|
| Go | 1.27.x toolchain (1.27.3 current, `toolchain go1.27.3`); `go 1.26.0` module floor, matching whatsmeow's `go 1.26.0` (toolchain `go1.27.2`) at the pin |
| whatsmeow | exact pseudo-version, bumped weekly by CI |
| gRPC (Go) | grpc-go latest 1.8x, protobuf-go APIv2 (`google.golang.org/protobuf`) |
| SQLite | `modernc.org/sqlite` (driver `"sqlite"`), no CGO. DSN: `file:whatsmeow-net.db?_pragma=foreign_keys(1)&_pragma=journal_mode(WAL)&_pragma=busy_timeout(5000)&_pragma=synchronous(FULL)&_txlock=immediate` (§14 question 3; whatsmeow's `Upgrade` refuses to run without foreign keys) |
| Go logging | zerolog, the same logger whatsmeow uses through `waLog.Zerolog` |
| Proto tooling | buf v2 (managed mode), protoc-gen-go, protoc-gen-go-grpc, protoc's built-in C# generator (protoc 36.2, paired with Google.Protobuf 3.36.2) + grpc C# plugin |
| .NET | `net10.0` only, C# 14 (`LangVersion latest` on the pinned SDK), `.slnx`, Central Package Management with transitive pinning, artifacts output in `dotnet/artifacts/` |
| gRPC (.NET) | `Grpc.Net.Client`, `Google.Protobuf`, `Grpc.StatusProto`. **Never** `Grpc.Core` |
| Resilience | Polly v8 (`Microsoft.Extensions.Resilience` where appropriate) |
| Time (.NET) | BCL: `DateTimeOffset` (UTC) and `TimeSpan`, from an injected `TimeProvider`; Google.Protobuf's built-in WKT conversions. No NodaTime in shipped packages (opt-out from the house rule, Decision log 0009) |
| IDs (.NET) | Vogen for primitive wrappers (`SessionId`, `MessageId`); `Jid` hand-written |
| Analyzers (.NET) | `AnalysisLevel latest-all`, `Meziantou.Analyzer`, `Microsoft.CodeAnalysis.BannedApiAnalyzers`, house `.editorconfig` with this repo's deltas ([guidelines/csharp.md](guidelines/csharp.md) Build baseline) |
| Lint (Go) | golangci-lint v2 with gofumpt and goimports ([guidelines/go.md](guidelines/go.md)) |
| Tests | xUnit v3 (4.x, Microsoft.Testing.Platform v2, selected in `global.json`), `FakeTimeProvider` (`Microsoft.Extensions.TimeProvider.Testing`); Go `testing` with `-race` |
| Versioning / release | MinVer from git tags; GoReleaser v2; NuGet Trusted Publishing (OIDC) |

---

## 6. The `whatsmeow.v1` contract

The contract is the most important artifact in the repo. Both sides are generated from it.

**Rules**

- Proto style (proto3, buf STANDARD, AIP-191, `_UNSPECIFIED` zero values, WKTs, field numbering) and the `Timestamp`/`Duration` mapping: [guidelines/proto.md](guidelines/proto.md).
- **Independent of whatsmeow's Go API.** No field mirrors a whatsmeow Go struct by accident. Mapping happens in Go slice code.
- whatsmeow's own protos (`WAWebProtobufsE2E.Message` etc.) are imported **unmodified** from `third_party/whatsmeow` and embedded as **typed fields**. On the wire, a typed field is identical to a `bytes` field holding the serialized message.
- The Go server uses a lenient codec (`AllowPartial` both ways) installed with `grpc.ForceServerCodecV2`, never a global `encoding.RegisterCodec`. With grpc-go's default codec, a message missing a proto2 `required` field fails to decode before any interceptor runs, as a bare `Internal` with no `ErrorInfo`; with the lenient codec it reaches the slice, whatsmeow's send-path `proto.Marshal` rejects it, and the slice maps that to `INVALID_ARGUMENT` (§14 question 1).
- From the first published `vX.Y.Z` tag on, `whatsmeow.v1` never breaks: a break means a new package `whatsmeow.v2` with its own ADR, every additive change increments `protocol`, and `buf breaking` gates `whatsmeow.v1` against `main`.
  Before that tag, `protocol` keeps its initial value, 1, and `buf breaking` only reports ([ADR 0015](adr/0015-buf-breaking-gates-from-first-published-tag.md)).
  `buf breaking` covers `whatsmeow.v1` only; upstream wire changes are reported but don't block.
- `GetInfo` reports `version`, `protocol` (an integer contract revision), `whatsmeow_version`, `wa_web_version`, `go_version` and `commit`.

**Service shape** (full outline in the research report):

| Service | Kind | Notes |
|---|---|---|
| `SidecarService` | unary | `GetInfo`, `Shutdown`; `grpc.health.v1` is also served |
| `SessionService` | unary + `Pair` (server stream) + `WatchSessionState` (server stream, snapshot first) | app-chosen session IDs |
| `EventService` | `StreamEvents` (server stream) + `AckEvents` (unary) | durable `seq`, resume with `after_seq`, per-consumer cursor |
| `MessageService` | unary | raw `SendMessage` plus typed helpers (text, media, reaction, edit, revoke, poll, read, presence) |
| `MediaService` | `UploadMedia` (client stream), `DownloadMedia` (server stream), `RequestMediaRetry` | path handoff or 64 KiB chunks |
| `ContactService`, `GroupService` | unary | includes LID↔PN resolution |
| `ChatStateService` | unary | Phase 3 |

**Error model:** every error is a gRPC status plus `google.rpc.ErrorInfo{ reason, domain: "whatsmeow.net", metadata }`. `reason` is a stable `SCREAMING_SNAKE` code: `NOT_LOGGED_IN`, `SESSION_NOT_FOUND`, `RECIPIENT_NOT_ON_WHATSAPP`, `MEDIA_EXPIRED`, `RATE_LIMITED`, `TEMPORARILY_BANNED`, `INVALID_ARGUMENT`, `SIDECAR_SHUTTING_DOWN`, and so on. Clients map on `reason` only, never on message text. The list of reasons is part of the contract.

---

## 7. Go sidecar (`whatsmeow-net`)

### 7.1 Responsibilities

A protocol adapter, nothing more: session lifecycle, pairing, event capture into the outbox, send queue, media transfer, history download. **No business logic and no media processing.**

### 7.2 whatsmeow configuration (mandatory)

The reference is the maintainer's own production bridge, mautrix-whatsapp `5dd621f` (`pkg/connector/client.go:81-93`), which sets the same core flags. Where we differ, the row says why.

| Setting | Value | Why |
|---|---|---|
| `SynchronousAck` | `true` | ack WhatsApp only after our handler succeeded, for the gated kinds only (§1, §14 whatsmeow source review) |
| `EnableDecryptedEventBuffer` | `true` | redelivery after a crash is decryptable and deduplicated, for DM and group Signal messages only (not bot `msmsg` or newsletter messages) |
| handler registration | `AddEventHandlerWithSuccessStatus` | returning `false` withholds the ack, for the gated kinds only |
| `ManualHistorySyncDownload` | `true` | history blobs aren't deleted from the server before we have them; we call `DownloadHistorySync` and then `DeleteMedia` ourselves (§7.7) |
| `UseRetryMessageStore` | `true` | retry receipts can be answered after a restart. mautrix defaults this to `false` ("by default, the bridge only stores messages in memory, and therefore can't accept retry receipts if the bridge is restarted"); we choose robustness |
| `AutomaticMessageRerequestFromPhone` | `true` | an undecryptable message gets a retry receipt to the sender, and after `RequestFromPhoneDelay` (5 s) a re-request to our own phone; the recovered content arrives as a gated `events.Message` (`retry.go:422-440`), with the same message ID [inferred: retry resends the original message; not traced]. mautrix sets it |
| `InitialAutoReconnect` | `true` | retry when the first connect at startup fails; mautrix's default (`initial_auto_reconnect: true`) |
| `EnableAutoReconnect` | `true`, with `AutoReconnectHook func(error) bool` capping and jittering the backoff (whatsmeow calls the hook only after a failed reconnect attempt) | with it off, a dead socket only emits `KeepAliveTimeout` and is never closed: the keepalive loop forces a reconnect only when auto reconnect is on (`keepalive.go:46`). This is the mechanism behind the "zombie socket" report; no "zombie" appears in the code |
| `store.DeviceProps`, `SetOSInfo`, `SetWAVersion` | set **once** at startup | process-global in whatsmeow; never mutated per session |

### 7.3 The event handler contract (critical)

whatsmeow runs most handlers **synchronously, in order, on one node queue per client**, but not all (§14 whatsmeow source review):

- Connection events are dispatched off the queue on their own goroutines (`LoggedOut`, `StreamReplaced`, `Disconnected`, `KeepAlive*`, `IdentityChange`, `TemporaryBan`, `Connected`, `PairSuccess`, automatic-mode `HistorySync`). They can run concurrently with message handlers.
- When the queue is full (2048 nodes), whatsmeow logs that ordering is no longer guaranteed and enqueues from a goroutine (`client.go:900`).
- A node that takes over about 5 minutes is left running in the background and the queue moves on (`client.go:954-970`).

Therefore the handler stays fast (milliseconds, never near the 5-minute cutoff), and:

1. Converts the event to a `whatsmeow.v1` event (pure mapping, no I/O except the next step).
2. Runs **one short transaction**: `INSERT INTO outbox … ON CONFLICT DO NOTHING`.
3. Signals waiting stream goroutines without blocking.
4. Returns `true` only after the commit. It returns `false` on a DB error or a panic; it uses its **own** `recover()`, because whatsmeow's `dispatchEvent` recovers a panic and returns "not failed", so the ack goes out, and the remaining handlers are skipped (`client.go:999-1013`).

The handler **never** calls gRPC, never waits on .NET, never downloads media, and never returns `false` just because no consumer is connected.

`UndecryptableMessage` is written to the outbox like any event (mautrix shows it as a placeholder), so .NET can show "waiting for this message". The recovered message arrives later, and the dedupe key (§8.4) ties the two together if it carries the same `MessageId` [inferred; verify in the Phase 1 live test].

Ephemeral high-volume events (presence, typing) bypass the outbox. They go into a bounded in-memory ring buffer with `seq = 0` and emit a `QueueOverflowEvent` when items are dropped.

### 7.4 Session state machine

Every whatsmeow signal maps to exactly one state. Each transition is written to the outbox as a `ConnectionStateEvent` and published to `WatchSessionState`.

`Created → Pairing → Connecting → Connected → CaughtUp`, with transient `Reconnecting` and the terminal or blocking states `LoggedOut`, `Replaced`, `TempBanned{until}`, `Outdated`, `Error{reason}`.
whatsmeow's `events.PermanentDisconnect` implementations at the pin are `*LoggedOut`, `*StreamReplaced`, `*ClientOutdated`, `*CATRefreshError`, `*TemporaryBan` and `*ConnectFailure` (`types/events/events.go:118-128`); each maps to one of these states (`CATRefreshError` and `ConnectFailure` to `Error{reason}`).

Actions the sidecar itself starts (`Disconnect`, `Logout`) produce no whatsmeow events (a self-initiated `Logout()` emits no `LoggedOut`), so the sidecar synthesizes those transitions. On a server-side `LoggedOut`, whatsmeow dispatches the event on a goroutine and deletes the device rows concurrently (`go cli.dispatchEvent(...)` then `cli.Store.Delete(ctx)`, `connectionevents.go:43-44`), so the handler may see the rows before or after deletion. The `LoggedOut` handler must not read or write the device rows and must not delete them itself; it only writes the outbox transition. Each SQLite file has a single owner, enforced with a lock file or lease row.

### 7.5 Sending

- Per-session FIFO queue with a token bucket and jitter. It pauses on `TempBanned`, backs off on 429, and fails all pending sends on `LoggedOut`.
- Idempotent sends: .NET supplies `client_message_id`, which is used as the WhatsApp message ID through `SendRequestExtra.ID` and persisted with the result.
- **A send whose outcome is unknown is never automatically replayed.** It surfaces as an error.
- Responses report `SendResponse.Chat`, because whatsmeow redirects a phone-number user JID to its LID, fetching it with `GetUserInfo` if needed; with no LID it returns `ErrRecipientNotOnWhatsApp` (`send.go:340-363`), which maps to `RECIPIENT_NOT_ON_WHATSAPP`.

### 7.6 Media

- Upload uses `UploadReader(ctx, io.Reader, io.ReadWriteSeeker tempFile, MediaType)` (constant memory through a temp file). whatsmeow does **not** retry uploads: `rawUpload` makes one POST to the first host (`upload.go:243-302`). The media slice owns upload retry with backoff, rewinding the temp file. Download uses `DownloadToFile`, which retries up to 5 times with an `(n+1)`-second or `Retry-After` backoff (`download-to-file.go:164`); its `File` must implement `ReaderAt`, `WriterAt`, `Truncate` and `Stat`. Never use the in-memory `Upload([]byte)` / `Download` variants for user media.
- Same host: hand files over by **path** under a configured root, validated against path traversal. Otherwise use **64 KiB chunk streams**. Never put whole files inline in a message.
- `MediaRef` (`direct_path`, `media_key`, `file_sha256`, `file_enc_sha256`, `file_length`, `mimetype`, `streaming_sidecar`) is enough to download lazily or to forward without re-uploading.
- Expired media returns `MEDIA_EXPIRED`. The caller then calls `RequestMediaRetry`, and the new path arrives as a `MediaRetryEvent`.

### 7.7 History sync

Notifications (`ProtocolMessage.HistorySyncNotification` inside `events.Message`) are persisted to a queue table. After `OfflineSyncCompleted`, a low-priority worker downloads each blob with `DownloadHistorySync(ctx, notif, synchronousStorage)`, parses messages with `ParseWebMessage`, writes **batched** `HistoryMessagesEvent` rows plus progress events, and only then calls `DeleteMedia` (in manual mode whatsmeow deletes nothing itself). whatsmeow still sends the `hist_sync` receipt unless `DisableManualHistorySyncReceipt` is set. Live traffic always has priority.

### 7.8 Process conventions

- `stdin`: first line is the 32-byte auth token. The rest is the tether: **EOF means shut down gracefully.**
- `stdout`: exactly **one** JSON line, the ready line: `{"event":"ready","version","protocol","whatsmeow","listen","nonce"}`.
- `stderr`: JSON log lines (zerolog). Every log line includes `session_id` where applicable. Phone numbers and JIDs are logged at debug level only.
- Linux: the sidecar sets `PR_SET_PDEATHSIG` on itself and re-checks its parent PID. macOS: polls its parent PID. These are backups to the stdin tether.

### 7.9 Go code conventions

Moved to [guidelines/go.md](guidelines/go.md): `context.Context` first, `%w` errors mapped to `ErrorInfo` only in `internal/server`, no `reflect`/`unsafe`/cgo/mutable globals, small per-slice whatsmeow interfaces, `whatsmeowv1` import name, owned goroutines.

---

## 8. .NET library

### 8.1 Packages

| Package | Contents | Stability |
|---|---|---|
| `Whatsmeow.Client` | public API, supervisor, transport, dispatcher, mapping | stable |
| `Whatsmeow.Protocol` | generated `whatsmeow.v1` messages and client stubs | internal use; no stability promise |
| `Whatsmeow.Protocol.WhatsApp` | generated upstream WhatsApp protos (`Whatsmeow.Proto.*`) | **unstable**: changes with every whatsmeow bump |
| `Whatsmeow.Extensions.Hosting` | `AddWhatsmeow(...)`, options validation, keyed sessions, hosted supervisor | stable |
| `Whatsmeow.Testing` | `FakeWhatsAppClient`, `Raise(event)` | stable |
| `Whatsmeow.Sidecar.Native.{win,linux,osx}` | `whatsmeow-net` binaries under `runtimes/{rid}/native/` | pinned to the exact `Whatsmeow.Client` version |

`Whatsmeow.Client` depends on the native packages with exact versions (`[x.y.z]`). Do not use a `runtime.json` pointer package.

### 8.2 Public API shape (indicative)

```csharp
await using var sidecar = await WhatsmeowSidecar.StartAsync(options, cancellationToken);
IWhatsAppClient client = await sidecar.Sessions.GetOrCreateAsync(SessionId.From("main"), cancellationToken);

await foreach (PairingUpdate update in client.Pairing.PairWithQrCodeAsync(cancellationToken)) { … }

using IDisposable subscription = client.On<MessageReceived>(static async (e, ct) => { … });

SendResult result = await client.Messages.SendTextAsync(chat, "hello", cancellationToken: cancellationToken);
```

- **One `IWhatsAppClient` per session.** It exposes feature groups that mirror the slices: `Pairing`, `Messages`, `Media`, `Contacts`, `Groups`, `Presence` (and later `Chats`).
- `WhatsmeowSidecar` owns the process and the single shared `GrpcChannel`. Clients are lightweight views over it.
- `IWhatsAppClient` is the one deliberate exception to the house rule "no interface just to mock it": `Whatsmeow.Testing` ships `FakeWhatsAppClient` to consumers. Library internals get no interfaces for testability.
- Core behavior lives on real members. C# 14 extension blocks are used only for convenience (`jid.IsGroup`), never for logic that must be mockable.
- Works without DI (`WhatsmeowSidecar.StartAsync`) and with DI (`services.AddWhatsmeow(...)` in namespace `Whatsmeow.Extensions.Hosting`, **not** `Microsoft.Extensions.DependencyInjection`).

### 8.3 Modeling rules

- Events: `public abstract record WhatsAppEvent` with `sealed record` cases (`MessageReceived`, `ReceiptReceived`, `ConnectionStateChanged`, …). Message content is a separate `abstract record MessageContent` hierarchy with sealed cases (`TextContent`, `ImageContent`, …, `UnknownContent`). These are shaped so they can become C# `closed`/`union` types later without breaking users' `switch` statements.
- Identifiers: `MessageId` and `SessionId` are Vogen value objects; `Jid` is a hand-written `readonly struct Jid : ISpanParsable<Jid>`. `Jid` carries both PN and LID forms where known. Events always include `Sender`/`SenderAlt` and `Chat`/`RecipientAlt`. Rules and rationale: [guidelines/csharp.md](guidelines/csharp.md) IDs.
- **Generated protobuf types never appear in the stable public API.** The only exception is the opt-in `Raw` escape hatch: `MessageReceived.Raw` (an `[Experimental]`-attributed property) and `client.Messages.SendRawAsync(WAWebProtobufsE2E.Message …)`. These live in `Whatsmeow.Protocol.WhatsApp` types and are documented as unstable.
- Time is BCL: `DateTimeOffset` (UTC) and `TimeSpan` in public members, "now" and every timer from one injected `TimeProvider`, never `DateTime`.
- Idioms (immutability with `required`/`init` and `ImmutableArray<T>`/`IReadOnlyList<T>`, `Task` vs `ValueTask`, `CancellationToken` last, `Async` suffix, time): [guidelines/csharp.md](guidelines/csharp.md) Public API modeling and Time.

### 8.4 Event pipeline and delivery guarantee

```
gRPC StreamEvents ─► reader loop ─► bounded Channel<Envelope> ─► dispatcher ─► handlers
                       (decode only,                (per-chat lanes,          (isolated
                        never awaits user code)      concurrent across chats)  try/catch)
                                                         │
                                       ack watermark ◄───┘  AckEvents(up_to_seq)
```

- The **reader** only decodes and writes to the channel. Backpressure flows into gRPC flow control and the sidecar's durable outbox; nothing is dropped.
- The **dispatcher** orders events per chat and runs different chats concurrently, with a configurable degree of parallelism.
- **Ack by watermark:** the cursor sent with `AckEvents` is the highest `seq` such that every event at or below it has finished all its handlers. Events are acked in batches on a timer and at shutdown.
- **Handler failures do not block the stream.** An exception is caught, logged, counted in metrics, and raised on `OnHandlerError`. The event still counts as handled; a failing event that blocked the stream forever would be worse. Users who need retries implement them in the handler.
- **The consumer contract is at-least-once:** handlers must be idempotent, and `MessageId` plus `SessionId` is the dedupe key. This is documented on every handler interface.
- Three consumption styles, all fed by the same dispatcher:
  1. DI handlers: `IWhatsAppEventHandler<TEvent>.HandleAsync(TEvent, CancellationToken)`.
  2. Delegates: `client.On<TEvent>(handler)`, which returns `IDisposable`.
  3. Opt-in `IAsyncEnumerable` reader (single consumer, mainly for scripts and tests).
- `WaitForAsync<TEvent>(predicate, timeout, ct)` supports request/response-style flows.

### 8.5 Sidecar supervision

- **Locate the binary** in this order: `options.SidecarPath` or `WHATSMEOW_NET_SIDECAR_PATH`; then `AppContext.BaseDirectory`; then `AppContext.BaseDirectory/runtimes/<rid>/native/`. The RID is derived from `RuntimeInformation` plus musl detection. The CGO-free Linux binary runs unchanged on Alpine (musl) and Debian (glibc) (§14 question 6), so a musl RID resolves to the same binary as its glibc RID. Never use `Assembly.Location`.
- **Make it executable:** call `File.SetUnixFileMode(+x)` before every start. If that fails, copy the binary to a writable cache directory (never `/tmp` mounted `noexec`).
- **Start:** redirect stdin/stdout/stderr, write the token as the first stdin line, wait for the ready line within `StartupTimeout`, verify the nonce, then call `grpc.health.v1`. If startup fails, the exception includes the tail of stderr.
- **Tether:** keep stdin open for the sidecar's whole lifetime. On Windows, also assign the process to a Job Object with `KILL_ON_JOB_CLOSE` (P/Invoke generated by CsWin32).
- **Stop:** `Shutdown` RPC, then close stdin, wait for the grace period, send SIGTERM (`[LibraryImport("libc")] kill`), and finally `Kill(entireProcessTree: true)`.
- **Restart:** a supervisor loop with Polly exponential backoff and jitter, reset after a sustained healthy period. The loop never lets an exception escape a `BackgroundService`.
- **Version check:**
  - Embedded mode requires `sidecar.version == client.version`.
  - Every mode requires `sidecar.protocol` to be within `[MinProtocol, MaxProtocol]`.
  - A mismatch is a hard `SidecarVersionMismatchException`.
- After a restart, the event reader resumes from the server-side cursor. Users see a `SidecarRestarted` diagnostic event, not a gap.

### 8.6 Transport

- `GrpcChannel.ForAddress("http://localhost", new GrpcChannelOptions { HttpHandler = new SocketsHttpHandler { ConnectCallback = … } })`.
- Unix domain socket by default on every OS, Windows included (§14 question 2: stable on both transports).
  AF_UNIX was about 2-4x faster for server streams of 64 KiB messages and about 1.5-3x for bidi.
  Client-stream upload was comparable, with the named-pipe range equal or higher (328-469 vs 308-390 MiB/s) [inferred: ratios from the range ends across the three hosts, not paired per host].
  On Windows the named-pipe transport stays selectable as a fallback; it passed the same soak.
- An auth token is added to every call by an interceptor, together with `ThrowOperationCanceledOnCancellation = true`.
- `MaxReceiveMessageSize` and `MaxSendMessageSize` are set explicitly to 16 MiB on both sides.
- Never call `GrpcChannel.ConnectAsync` or read `GrpcChannel.State`; both throw `InvalidOperationException` ("doesn't support client-side load balancing or connectivity state tracking") when `ConnectCallback` is set (§14 transport spike). Readiness comes from the handshake.

### 8.7 Errors

- `WhatsmeowException` (base) carries `ErrorCode` (from `ErrorInfo.reason`) and the `RpcException` as `InnerException`.
- Subtypes:
  - `SidecarUnavailableException`, `SidecarVersionMismatchException`
  - `NotLoggedInException`, `SessionLoggedOutException`, `SessionNotFoundException`
  - `RateLimitedException` (`RetryAfter`, a `TimeSpan`), `TemporarilyBannedException` (`Until`, a `DateTimeOffset`)
  - `RecipientNotFoundException`, `MediaExpiredException`, `WhatsmeowValidationException`
- Cancellation surfaces as `OperationCanceledException`, and deadlines as `TimeoutException`.
- `RpcException` never escapes the public API. No Result types for I/O failures.
- Exceptions are raised through per-type static throw helpers, never `throw new` at a call site ([guidelines/csharp.md](guidelines/csharp.md) Control flow). `WhatsmeowValidationException` is also the Vogen `throws:` type.

### 8.8 Observability

- Logging: `ILogger` with `[LoggerMessage]` source-generated methods only. Sidecar stderr is forwarded into category `Whatsmeow.Sidecar` with level mapping.
- Tracing: one static `ActivitySource("Whatsmeow.Client")`, with spans for sends, media transfers and event processing.
- Metrics: one `Meter("Whatsmeow.Client")` via `IMeterFactory`, covering messages sent and received, handler duration, dispatcher queue depth, errors, and restarts. Source and meter names are public constants.
- **PII:** JIDs and phone numbers appear only at `Debug` level or below, or redacted.

### 8.9 Reflection policy

**No runtime reflection in library code.** Everything that would normally use reflection is either generated at build time or written explicitly, and the shipped projects are AOT- and trim-clean with zero warnings (`tests/AotSmoke`).
The per-need table (protobuf, logging, options validation, JSON, native interop, event dispatch, handler registration, IDs) and the enforcement (`IsAotCompatible`, `IsTrimmable`, BannedApiAnalyzers with `BannedSymbols.txt`, AotSmoke) are in [guidelines/csharp.md](guidelines/csharp.md) Reflection.

### 8.10 Concurrency rules

Moved to [guidelines/csharp.md](guidelines/csharp.md) Async and concurrency: `Channels` and `System.Threading.Lock` (never across `await`), no sync-over-async or `async void`, `ConfigureAwait(false)` everywhere (CA2007 as error), every background loop owned by a `CancellationTokenSource` awaited in `DisposeAsync`.

---

## 9. Key flows

**Inbound message:** WhatsApp → whatsmeow decrypts (decrypted-event buffer) → sidecar handler maps and inserts into the outbox, then returns `true` → whatsmeow acks WhatsApp → the `StreamEvents` goroutine sends `seq=N` → .NET reader → dispatcher (chat lane) → handlers → watermark reaches N → `AckEvents(N)`. A crash at any step causes a replay from the last durable point, never a loss.

**Send:** `SendTextAsync` → `MessageService.SendText` with `client_message_id` → per-session queue (pacing) → `whatsmeow.SendMessage` → the response carries the server timestamp and the actual `chat` JID (which may be a LID) → `SendResult`.

**Media send:** .NET prepares metadata (thumbnail, dimensions, waveform) → `UploadMedia` (path or chunks) → `UploadReader` → `MediaRef` → `SendMedia(MediaRef, metadata)`. Re-sending an existing `MediaRef` skips the upload.

**Pairing:** `Pair` stream opens → sidecar calls `GetQRChannel` and then `Connect` (`GetQRChannel` after connect returns `ErrQRAlreadyConnected`) → QR codes are streamed (or a phone pairing code via `PairPhone` after the first QR event; whatsmeow documents this order but does not enforce it) → `PairSuccess` → the sidecar waits on `Client.PreKeysUploaded` (an `*exsync.Event` field, not an event type; it fires on the first successful pre-key upload, on the connect after pairing) → `success` → session state becomes `Connected`.

**Startup:** locate the binary → chmod → spawn → write the token → ready line → nonce check → health check → `GetInfo` version check → `WatchSessionState` and `StreamEvents(after_seq = cursor)` for each session.

---

## 10. Security

- The socket lives in a freshly created `0700` directory with a short random name; a stale socket file is removed before `Listen`. Linux abstract sockets are never used.
- Windows named pipes use a random name, a current-user-only DACL (go-winio `D:P(A;;GA;;;OW)`), and `PipeOptions.CurrentUserOnly` on the client; that combination connected in the soak [observed: tag `spike/windows-transport`, `spikes/windows-transport/go/server/pipe_windows.go:16`, `spikes/windows-transport/dotnet/Program.cs:50`; §14 question 2].
- On Windows, `os.Chmod(dir, 0700)` does not set an ACL. The socket directory's protection comes from the user's `%LOCALAPPDATA%\Temp` ACL [inferred: default per-user ACL, not measured], so the sidecar sets an explicit owner-only DACL on that directory at creation, and the token stays the real gate.
- A per-launch 32-byte token is delivered on stdin (never argv or environment variables). It is checked in constant time by a gRPC interceptor. A nonce echo guards against connecting to a stale sidecar.
- The SQLite database holds long-term private keys. It is created `0600`, and the docs recommend disk encryption. Backups use `VACUUM INTO` and must be encrypted by the user.
- Media paths are confined to a configured root, and path traversal is rejected.

---

## 11. Testing strategy

Integration tests are the default (house rule).
Every slice gets a FullCycle through the public `IWhatsAppClient` against a real sidecar in fake-WhatsApp mode.
.NET unit tests exist only for the short vital list below; proto-to-record mapping and error mapping are covered through integration.
How to write each test, what is banned, and the can-it-fail check: [guidelines/tests.md](guidelines/tests.md).

| Layer | What | Where |
|---|---|---|
| Integration (default) | real `whatsmeow-net` started with `--fake-whatsapp`, a test-only build tag that replaces whatsmeow with a scripted fake and exposes a control service to inject events and failures; one FullCycle per slice through the public API, covering proto ↔ record mapping and error mapping; kill-either-process tests for delivery guarantees | `tests/Whatsmeow.IntegrationTests` |
| Go unit | every slice, with whatsmeow faked behind the slice's small interface; event-mapping tables covering every event type; outbox replay/ack/crash semantics; `-race` always | `sidecar/internal/**` |
| Contract | `buf lint`; `buf breaking`. From the first published `vX.Y.Z` tag on, `whatsmeow.v1` never breaks: a break means a new package `whatsmeow.v2` with its own ADR, every additive change increments `protocol`, and `buf breaking` gates `whatsmeow.v1` against `main`. Before that tag, `protocol` keeps its initial value, 1, and `buf breaking` only reports ([ADR 0015](adr/0015-buf-breaking-gates-from-first-published-tag.md)); generated-code diff check | CI |
| .NET unit (vital only) | dispatcher per-chat ordering and watermark acking; supervisor restart/backoff state machine with `FakeTimeProvider` | `tests/Whatsmeow.Client.Tests` |
| Live | real WhatsApp with a dedicated test number; manual pairing; opt-in through environment variables; never in default CI | `tests/Whatsmeow.LiveTests` |
| Packaging | `dotnet pack` → restore from a local feed → run on win-x64, linux-x64 (glibc and Alpine), osx-arm64 | CI matrix |
| AOT | `tests/AotSmoke`, zero warnings | CI |

---

## 12. Release and upstream tracking

- One git tag (`vX.Y.Z`) drives both GoReleaser (binaries) and `dotnet pack` (MinVer). The versions are always equal.
- No Developer ID signing, notarization, or Authenticode (§14 questions 2 and 6).
  A binary restored by `dotnet restore` from nuget.org carries no `com.apple.quarantine` attribute, and an ad-hoc signed one runs even though `spctl` rejects it (§14 question 6) [inferred: Gatekeeper assesses only quarantined launches, which matches what we saw].
  Apple Silicon needs arm64 code to be signed at all [inferred: Apple platform rule, not measured here], and the Go linker ad-hoc signs `darwin/arm64` itself.
  An app that embeds us and ships as a notarized `.app` signs its embedded binaries as part of its own release [inferred: standard macOS app signing, not our concern].
- **Weekly `whatsmeow-bump` workflow:**
  1. Bump the whatsmeow pin.
  2. Copy the protos from the **exact** module directory (`go mod download -json`).
  3. Regenerate the code.
  4. Run all tests.
  5. Post a non-gating upstream wire-diff comment and a parity report.
  6. Open a PR.
- The whatsmeow pin is an implementation detail; a bump that passes CI is a patch release.
- From the first published `vX.Y.Z` tag on, `whatsmeow.v1` never breaks: a break means a new package `whatsmeow.v2` with its own ADR, every additive change increments `protocol`, and `buf breaking` gates `whatsmeow.v1` against `main`.
  Before that tag, `protocol` keeps its initial value, 1, and `buf breaking` only reports ([ADR 0015](adr/0015-buf-breaking-gates-from-first-published-tag.md)).
- `NOTICE` carries the MPL-2.0 source offer for whatsmeow at the pinned commit.

---

## 13. Invariants

Any change that violates one of these is wrong unless an ADR changes the invariant first.

1. The whatsmeow event handler never blocks on .NET, the network, or media. It only writes to the outbox.
2. A WhatsApp message of a gated kind (§1) is acked to the server only after it is durably in the outbox. Ungated kinds are acked by whatsmeow regardless; we never claim otherwise.
3. An event is acked by .NET only after all of its handlers have completed. Acks follow the watermark.
4. Every whatsmeow `PermanentDisconnect` (six types at the pin, §7.4) maps to an explicit session state and is delivered to .NET. "Silent" sessions are a bug.
5. From the first published `vX.Y.Z` tag on, `whatsmeow.v1` never breaks: a break means a new package `whatsmeow.v2` with its own ADR, every additive change increments `protocol`, and `buf breaking` gates `whatsmeow.v1` against `main`.
   Before that tag, `protocol` keeps its initial value, 1, and `buf breaking` only reports ([ADR 0015](adr/0015-buf-breaking-gates-from-first-published-tag.md)).
6. Generated protobuf types never appear in the stable public C# API.
7. No runtime reflection, no `async void`, no sync-over-async, no `Grpc.Core`, no `Newtonsoft.Json`, no `DateTime`, no clock or timer that bypasses the injected `TimeProvider`.
8. No whole-file media in a single gRPC message; use paths or chunks.
9. The sidecar cannot outlive its parent process.
10. A send with an unknown outcome is never automatically retried.
11. Go code never regenerates whatsmeow's protos into its own packages; it imports whatsmeow's.
12. Embedded sidecar version equals client version.

---

## 14. Open questions (Phase 0 spikes)

These could change the architecture.
All eight are answered, and each outcome is recorded in the ADR named in its row.

| # | Question | Decides |
|---|---|---|
| 1 | Does grpc-go's codec fail on whatsmeow messages with missing proto2 `required` fields? | **Answered: yes.** Typed fields stay, with a server-only lenient codec (§6). [ADR 0004](adr/0004-whatsmeow-protos-as-typed-fields-hand-written-records.md) |
| 2 | Windows: AF_UNIX streaming stability (grpc-go #7039) vs named pipes | **Answered.** Both stable on Go 1.27.3; AF_UNIX is the default, named pipe the fallback (§8.6). [ADR 0014](adr/0014-af-unix-default-on-windows.md) |
| 3 | modernc + sqlstore under contention, and whether `_txlock=immediate` is supported | **Answered.** Supported; final DSN in §5. [ADR 0003](adr/0003-at-least-once-delivery-synchronousack-sqlite-outbox.md) |
| 4 | Are any event types still acked before dispatch with `SynchronousAck`? | **Answered: yes, many.** The guarantee is scoped to gated kinds (§1, §13 invariant 2). [ADR 0003](adr/0003-at-least-once-delivery-synchronousack-sqlite-outbox.md) |
| 5 | AOT/trim warnings from the large generated WhatsApp types, and from Vogen-generated code with the chosen `conversions:` | **Answered (go), see Spike results.** The AOT claim holds; Vogen uses `Conversions.SystemTextJson`. [ADR 0006](adr/0006-no-runtime-reflection.md), [ADR 0010](adr/0010-vogen-ids-hand-written-jid.md) |
| 6 | Sidecar binary size; Alpine run; macOS signing | **Answered.** No Developer ID signing or notarization needed (§12). [ADR 0007](adr/0007-per-os-native-nuget-packages-exact-pins.md) |
| 7 | Can house-rule NodaTime coexist with BCL timers (Polly v8, `Task.Delay`, `PeriodicTimer`, CTS timeouts) and with testable fake time? | **Answered, see Spike results.** It can only with an interop adapter and two fakes; Antonio chose BCL time instead. [ADR 0009](adr/0009-bcl-time-over-nodatime.md) |
| 8 | Should the delivery guarantee extend to the ungated kinds, via an upstream whatsmeow PR or a fork? | **Answered: no fork.** Follow the maintainer's configuration and fix real gaps upstream. See Spike results. [ADR 0013](adr/0013-whatsmeow-config-follows-mautrix-no-fork.md) |


### Spike results (2026-10-31)

Run on macOS arm64, .NET SDK 10.0.400, `dotnet publish -r osx-arm64` with `PublishAot=true`, `TrimmerSingleWarn=false`.
Packages: Google.Protobuf 3.36.2, Grpc.Tools 2.85.0, NodaTime 3.3.7 and NodaTime.Serialization.Protobuf 2.0.2 (measured, then dropped), Vogen 8.0.7, Polly.Core 8.9.0, Microsoft.Extensions.* 10.0.14, Microsoft.Extensions.TimeProvider.Testing 10.11.0, BannedApiAnalyzers 5.6.0.
Protos: all 57 `.proto` files from whatsmeow `56e5ad0581cee858000f856b1a98dbd03cc5b14f`, compiled unmodified with `GrpcServices="None"`.
**The whatsmeow pin** is `56e5ad0581cee858000f856b1a98dbd03cc5b14f`, the merge commit of tulir/whatsmeow#1312 [observed: GitHub API, `repos/tulir/whatsmeow/pulls/1312` `merge_commit_sha`, 2026-10-31].
"At the pin" in this document means that commit.
The spikes were throwaway code outside the repo; each result below was observed in their output unless tagged.

**Question 5: answered, go.**

- Zero IL/AOT warnings for the full set: the 57 generated WhatsApp types (build, serialize, parse, equality, descriptor lookup), Google.Protobuf `Timestamp`↔`DateTimeOffset` and `Duration`↔`TimeSpan`, NodaTime with a Tzdb lookup and `NodaTime.Serialization.Protobuf`, Vogen value objects, Polly retry, the `[OptionsValidator]` generator and the configuration-binding generator.
- Every path ran correctly in the native binary (about 10 MB for the whole spike).
- The only trim warnings came from a probe that called `TypeDescriptor.GetConverter` on a Vogen type with `Conversions.Default`. That is why Vogen uses `Conversions.SystemTextJson` only ([guidelines/csharp.md](guidelines/csharp.md) IDs).
- Not covered: `Grpc.Net.Client` and the generated `whatsmeow.v1` stubs (they do not exist yet). `tests/AotSmoke` stays the gate.

**Vogen and options findings** (they shape rules in the guidelines):

- Vogen needs a public `(string)` constructor on the `throws:` exception (VOG013). `default(T)` is a compile error (VOG009). A defaulted field throws on `.Value` at runtime.
- `Vogen.SharedTypes.dll` is a hard runtime dependency of consumers: the nupkg depends on `Vogen`, and a consumer without the dll crashes with `FileNotFoundException`.
- STJ source-gen inside the same assembly cannot see Vogen converters (it serialized `{}`). A consumer's STJ source-gen sees them through metadata, under AOT, with zero warnings. Omit the public `VogenTypesFactory`.
- `TimeSpan` binds from `00:00:30` through the binding generator under AOT. `NodaTime.Duration` does not bind from a configuration string at all: the generator treats it as an object with properties, the reflection binder ignores it too, and both keep the old value without an error.
- A library with only `IsAotCompatible=true` does not get the configuration-binding generator: `BindConfiguration` raised IL2026/IL3050 until `EnableConfigurationBindingGenerator=true` was set. Only `PublishAot=true` turns it on implicitly.

**Question 1: answered, typed fields stay.** grpc-go 1.85.0, protobuf-go 1.36.12, whatsmeow at the pin.

- whatsmeow protos have 125 proto2 `required` fields. `Message.extendedTextMessage.endCardTiles[]` reaches `VideoEndCard`, which has four.
- grpc-go's default codec refuses to marshal such a message (`code = Internal`, "error while marshaling"), and refuses to unmarshal it on the server ("failed to unmarshal the received message", `Internal`, no `ErrorInfo`) before any interceptor runs.
- whatsmeow's own decrypt path (`proto.Unmarshal`, `message.go:290`, `:446`) rejects the same message, so anything whatsmeow hands us inbound is already complete. The risk is only .NET → sidecar.
- C# Google.Protobuf 3.36.2 is lenient: `ToByteArray()` and `ParseFrom()` accept the partial message; `IsInitialized()` returns false.
- With `grpc.ForceServerCodecV2(lenient codec)` the partial message reaches the handler. whatsmeow's send path marshals with plain `proto.Marshal` (`send.go:1106`, `:1261`), so the slice gets a "required field not set" error and returns `InvalidArgument`. Complete messages are unaffected.

**Question 2: answered.** GitHub Actions run 41228306743, spike code at tag `spike/windows-transport` (commit `0e5774d`) under `spikes/windows-transport/`; the branch was deleted after recording these results, and the tag keeps the commit reachable.
Setup: Go 1.27.3 grpc-go 1.85.0 server, .NET 10.0.14 Native AOT client, on Windows Server 2022 (build 20348, x64), Windows Server 2025 (build 26100, x64) and Windows 11 (build 26200, arm64).

- The grpc-go #7039 reproducer shape (Go client and server, AF_UNIX, bidi, 500 B messages back to back, 90 s): 15.1-21.5M messages echoed in order, no transport error, on all three. That matches a third-party report on the issue that the corruption was a Go runtime bug, present on Go 1.22.12 and gone on Go 1.26.5.
- .NET client ↔ Go sidecar, every test passing on both transports and all three hosts, with 60 s or 30 s budgets:

| Test | AF_UNIX | Named pipe |
|---|---|---|
| server stream, 512 B, in order | 34-40M events, 563k-654k/s | 34-38M events, 564k-631k/s |
| server stream, 64 KiB messages | 1,078-1,482 MiB/s | 393-545 MiB/s |
| client stream upload, 64 KiB chunks, in order | 308-390 MiB/s | 328-469 MiB/s |
| bidi 500 B back to back + a ping every 50 ms | 3.2-4.2M echoed in order | 1.5-2.1M echoed in order |
| stdin EOF stops the sidecar | exit 0 | exit 0 |

- Mark-of-the-Web: the `protoc.exe` that `dotnet restore` pulled from nuget.org has no `Zone.Identifier` stream, and our unsigned `sidecar.exe` (Authenticode `NotSigned`) starts. So no Authenticode signing is needed either [inferred: SmartScreen keys on Mark-of-the-Web, which matches what we saw].

**Question 3: answered.** modernc v1.61.1 parses `_txlock=deferred|immediate|exclusive` (`sqlite.go:426`); `dbutil.ParseDialect` accepts any `sqlite*` name. Workload: 8 sessions, 10,400 ops mixing whatsmeow's own store writes (sessions, identities, pre-keys, app-state versions and MACs), our outbox insert transactions, and a read-then-write cursor transaction, 3 runs each.

| DSN | SQLITE_BUSY |
|---|---|
| WAL, deferred, no `busy_timeout` | 7,886-9,568 (about 90%) |
| WAL, deferred, `busy_timeout(5000)` | 574-678, all in the read-then-write transaction |
| WAL, `_txlock=immediate`, `busy_timeout(5000)` | 0 |

`synchronous(FULL)` costs about 2-5x over `NORMAL` (0.7-1.9 s vs 2.0-3.2 s for the 10,400 ops), still with 0 BUSY. FULL is chosen because invariant 2 needs the outbox row to survive power loss, not only a process crash [inferred: from SQLite's documented WAL durability, not measured here].

**Question 4: answered by source review** of whatsmeow at the pin. A review agent read the source; the starred items were re-checked by hand.

- Acks a handler can withhold: decrypted `*events.Message` and `*events.FBMessage`, newsletter plaintext messages, non-retry receipts*, `w:gp2` group notifications*.
- Acked regardless of the handler: undecryptable messages* (retry receipt and ack go out before `UndecryptableMessage` is dispatched, `message.go:424-437`), unavailable messages, missing-secret `msmsg` (nacked), already-processed messages, retry receipts* (acked inside the retry handler, `receipt.go:34-38`), calls, app state, `hist_sync` receipts, and every notification type except `w:gp2` and `server_sync`*; `encrypt` and `account_sync` notifications run on goroutines*.
- A handler panic counts as success*. A placeholder-resend failure is turned into success (`message.go:1174`).
- Grouped receipts with several participants: only the last participant's handler result decides the ack*, because the loop overwrites `cancelled` (`receipt.go:29-31`).
- How soon the server redelivers an unacked message is [unknown].

**Other whatsmeow claims this document used to state, checked at the pin:**

- Confirmed: all six `Client` fields in §7.2*; `store.DeviceProps`, `store.SetOSInfo`, `store.SetWAVersion` are package-level; `SendRequestExtra.ID` and `SendResponse.Chat`; `SendMediaRetryReceipt` with the result as `*events.MediaRetry`; `ParseWebMessage`; `events.OfflineSyncCompleted`; `waLog.Zerolog`; `events.PermanentDisconnect`*.
- Refuted, now corrected above: "one queue per client" (§7.3)*; "whatsmeow has already deleted the device rows" on `LoggedOut` (§7.4)*; "`UploadReader` retries up to 3 times" (§7.6)*; "a `PreKeysUploaded` event" (§9; it is a `Client` field)*; "the zombie-socket bug" (§7.2, not in code; the mechanism is the keepalive loop)*.
- `RESEARCH.md` still contains the refuted claims as originally researched. This section wins over it.

**Question 6: answered.** Go 1.27.3, `CGO_ENABLED=0 -trimpath -ldflags "-s -w"`, a sidecar-shaped binary (whatsmeow client, sqlstore with the §5 DSN, gRPC health server on a Unix socket, zerolog).

- Sizes: linux-amd64 22.9 MB, linux-arm64 21.9 MB, darwin-arm64 22.4 MB, darwin-amd64 23.3 MB, windows-amd64 23.2 MB, windows-arm64 21.9 MB.
- The Linux binaries ran (store upgrade, client, socket, ready line) on `alpine:3` and `debian:stable-slim`, arm64 natively and amd64 under emulation.
- macOS: the Go linker ad-hoc signs `darwin/arm64`; `darwin/amd64` is unsigned.
  `spctl` rejects both, but that only applies to quarantined launches [inferred: Gatekeeper assesses only quarantined launches].
  The macOS `protoc` that `dotnet restore` pulled from nuget.org (Grpc.Tools 2.85.0) has no `com.apple.quarantine`, is only ad-hoc signed, and runs.
  Developer ID signing and notarization are therefore dropped (§12).

**Transport spike: answered.** Go 1.27.3 grpc-go 1.85.0 server, .NET Grpc.Net.Client 2.85.0 client published with Native AOT (0 IL/AOT warnings), osx-arm64.

- Spawn, token on stdin, JSON ready line on stdout, unary call over a Unix socket through `SocketsHttpHandler.ConnectCallback`: works.
- Server stream of 200,000 events with 512-byte payloads: in order, about 849,000 events/s.
- `GrpcChannel.State` and `ConnectAsync` throw `InvalidOperationException` with `ConnectCallback` set, confirming §8.6.
- Closing stdin stops the sidecar (exit 0).

**Question 8: answered, follow the maintainer.**

- whatsmeow's maintainer runs whatsmeow in production in mautrix-whatsapp.
  At `5dd621f` (2026-10-30; pinned to whatsmeow `20261026125351-1fbae988179a`, which is mautrix's own pin, not ours) it sets `AddEventHandlerWithSuccessStatus`, `SynchronousAck = true`, `EnableDecryptedEventBuffer` (on when `PortalEventBuffer == 0`, which we take to be the default [inferred]), `ManualHistorySyncDownload = true`, `AutomaticMessageRerequestFromPhone = true`, and `InitialAutoReconnect` (default `true`) (`pkg/connector/client.go:81-93`, `example-config.yaml:66`).
- Its handler returns real success for messages, receipts, undecryptable messages, calls, media retries and group changes, and lets whatsmeow decide which acks that gates (`pkg/connector/handlewhatsapp.go:78-132`). `UndecryptableMessage` becomes a placeholder event (`handlewhatsapp.go:435-458`).
- The maintainer recommends `SynchronousAck` plus `EnableDecryptedEventBuffer` against losing a decrypted-but-undispatched message (tulir/whatsmeow#1093). His note says hitting that is "very rare unless the program is running on a mobile phone".
- Gaps get fixed upstream: tulir/whatsmeow#1312 made `server_sync` fetch app state before acking under `SynchronousAck`.
  It was merged 2026-10-30 19:55:14 UTC as `56e5ad0`, which is our pin.
- So a fork would buy coverage the maintainer considers unnecessary, at the cost of the weekly-bump goal. We take his configuration, and report a gap upstream when a test proves one.

**Question 7: answered; BCL time chosen.**

- Polly v8 has no NodaTime support; it schedules only through `System.TimeProvider` (no NodaTime or `IClock` reference in Polly.Core 8.9.0).
- NodaTime 3.3 bridges one way only: `TimeProvider.ToClock()`. `IClock` has no timer API, so no `IClock`-backed `TimeProvider` can exist, and `FakeClock` cannot drive delays.
- One `FakeTimeProvider` drove a Polly exponential retry (2s, 4s, 8s gaps) and every "now" read in the same test.
- So NodaTime would have needed an interop file, two fakes, a hand-parsed options step, an extra protobuf package, and a forced dependency on every consumer, for a library with no zone or calendar math. Antonio chose BCL time (Decision log 0009).
- BannedApiAnalyzers 5.6.0 flags each ambient-time and timer ID listed in [guidelines/csharp.md](guidelines/csharp.md) BannedSymbols.txt, and none of their `TimeProvider` overloads.

---

## 15. Roadmap

| Phase | Scope | Exit criteria |
|---|---|---|
| 0 | Spikes from §14; repository scaffolding and CI | every spike has a written go/no-go ADR |
| 1 | Sessions, state machine, pairing, outbox and event stream, raw and text send, receipts; .NET supervisor, transport, dispatcher, errors, `Jid`, core events | pair a real phone; kill either process mid-stream; no message is lost, and a repeat only follows the last acked cursor and carries the same `MessageId` and `SessionId` (at-least-once, §8.4) |
| 2 | Media, reactions, edits, revokes, polls, presence, contacts and LID, groups, send pacing | parity report covers messaging, groups and contacts |
| 3 | History sync, chat state, hosting package, telemetry, `Whatsmeow.Testing` | hosted sample with two sessions; history doesn't delay live traffic |
| 4 | Native packages, Trusted Publishing, weekly bump, AOT smoke, PublicAPI baseline, External mode, docs | `dotnet add package` works on all target RIDs from nuget.org |

---

## Decision log

Decisions are recorded in `docs/adr/NNNN-title.md` (status, context, decision, consequences).
Phase 0 decisions:

- [0001](adr/0001-go-sidecar-over-csharp-port-or-cgo.md): Go sidecar over a native C# port or cgo FFI
- [0002](adr/0002-grpc-over-local-sockets.md): gRPC over local sockets as the IPC transport
- [0003](adr/0003-at-least-once-delivery-synchronousack-sqlite-outbox.md): At-least-once delivery through `SynchronousAck` and a SQLite outbox
- [0004](adr/0004-whatsmeow-protos-as-typed-fields-hand-written-records.md): Reusing whatsmeow protos as typed fields; hand-written public records
- [0005](adr/0005-feature-slices-across-proto-go-dotnet.md): Feature-slice organization across proto, Go and .NET
- [0006](adr/0006-no-runtime-reflection.md): No runtime reflection; source generation and explicit registration
- [0007](adr/0007-per-os-native-nuget-packages-exact-pins.md): Packaging as per-OS native NuGet packages with exact-version pins
- [0008](adr/0008-product-name-whatsmeow-net.md): Product name Whatsmeow.NET; identifiers rooted at `Whatsmeow`; sidecar binary `whatsmeow-net`

House-rule alignment (applied, or opted out of with a measured reason):

- [0009](adr/0009-bcl-time-over-nodatime.md): BCL time (`DateTimeOffset`/`TimeSpan`, injected `TimeProvider`) instead of the house NodaTime rule, because NodaTime would be forced on consumers and its costs were measured (§14 question 7)
- [0010](adr/0010-vogen-ids-hand-written-jid.md): Vogen for primitive ID wrappers; `Jid` stays hand-written; replaces hand-written `readonly record struct` IDs
- [0011](adr/0011-integration-tests-by-default.md): Integration tests by default, with .NET unit tests only for the dispatcher and supervisor; replaces a broad .NET unit layer
- [0012](adr/0012-iwhatsappclient-interface-exception.md): `IWhatsAppClient` kept as the single exception to "no interface just to mock it"
- [0013](adr/0013-whatsmeow-config-follows-mautrix-no-fork.md): whatsmeow configuration follows the maintainer's production bridge (mautrix-whatsapp); gaps are fixed upstream, never in a fork (§14 question 8)
- [0014](adr/0014-af-unix-default-on-windows.md): AF_UNIX as the default transport on Windows too; named pipe as the selectable fallback (§14 question 2)

Contract gating:

- [0015](adr/0015-buf-breaking-gates-from-first-published-tag.md): from the first published `vX.Y.Z` tag on, `whatsmeow.v1` never breaks (a break is a new `whatsmeow.v2`), every additive change increments `protocol`, and `buf breaking` gates; before it, `protocol` keeps its initial value, 1, and `buf breaking` only reports
