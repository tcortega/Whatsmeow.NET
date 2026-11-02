# 0046. External mode: a sidecar the client does not start

- Status: Accepted
- Date: 2026-11-02
- Evidence: issue #49 and its plan review; grpc-go v1.85.0 and .NET 10.0.14 read as cited below; the tests named below
- Amends: ARCHITECTURE §13 invariant 9 (scoped to embedded mode), §1, §7.8, §8.4, §8.5, §8.6 and §10; [ADR 0016](0016-sidecar-binary-gpl-3-corresponding-source-per-release.md) (its `[unknown]` on leaving the native assets out)

## Context

In a container deployment the sidecar may run as its own container or process, which an orchestrator starts, restarts and stops instead of the .NET application (§1: External mode, Phase 4).
Every guarantee so far assumed a tethered child:

- The client writes the per-launch token as the first stdin line and keeps stdin open as the tether; EOF stops the sidecar (`SidecarProcess.cs`, `cmd/whatsmeow-net`) [observed].
- The sidecar always tethers itself to its parent (`--parent-pid`, or its parent at start; [ADR 0024](0024-tether-backups.md)), and ignores SIGINT, because the client decides when it stops [observed: `cmd/whatsmeow-net/main.go`].
  In a container, stdin is `/dev/null` unless the container runs with `-i`, so a tethered sidecar would read no token and stop at once, and as PID 1 its parent at start is 0 [inferred].
- The client creates the private socket directory and reuses it across restarts ([ADR 0017](0017-sidecar-process-conventions.md)), and watches the process to restart it (§8.5).
- Invariant 9: the sidecar cannot outlive its parent process. Invariant 12 already said *embedded*.

Measured for the design:

- grpc-go's default keepalive policy lets a client ping at most every 5 minutes, and answers the third early ping with GOAWAY `too_many_pings`, closing the connection (grpc v1.85.0 `internal/transport/defaults.go:40`, `http2_server.go:881, 885-931`) [observed: source; `TestPingsAtTheMinimumIntervalOnAnIdleConnectionAreAnswered` red without the policy below].
  A TCP peer that vanished (a node lost, a network partition) never closes its end, so a client that does not ping keeps a dead connection until TCP gives up [inferred: TCP retransmission behaviour, not measured here].
- The sidecar already serves `grpc.health.v1`, behind the token, and its stream interceptor ends every open stream at Shutdown (`internal/server/server.go`) [observed].
- `Grpc.HealthCheck` 2.85.0 (Apache-2.0, from grpc-dotnet, the same version as `Grpc.Net.Client`) publishes under Native AOT with zero trim and AOT warnings, the whole assembly rooted in `tests/AotSmoke` [observed: osx-arm64, SDK 10.0.403, before any use of it].

## Decision

**Invariant 9 is scoped to embedded mode**: "An embedded sidecar cannot outlive its parent process."
An external sidecar's lifetime is its orchestrator's; the client never starts, stops or restarts it.

**The sidecar standalone.** `whatsmeow-net --database <file> --listen <address> --token-file <file>`:

- `--listen` is `unix:<absolute path>`, a Unix domain socket at that path, for example on a volume two containers share, or `tcp:<host>:<port>`.
  The socket's directory is the operator's, so it is left as it is; a socket a killed sidecar left at the path is removed, anything else there is refused, and the socket is made `0660`, so a client of the same user or of a shared group (Kubernetes `fsGroup`) connects [inferred: `fsGroup` gives both containers that group; not measured in a pod].
- `--token-file` holds the token, 32 bytes as hex, one line, the final line ending optional, since a secret mount may have none; the file is read once at start, and an error never carries what it holds.
  The token never comes from an argument or an environment variable (§10).
- Standalone, the sidecar never reads stdin and has no parent tether; SIGTERM, SIGINT and the Shutdown RPC stop it gracefully. `--nonce` is not needed, and the ready line is still printed, with no nonce.
- The two modes do not mix: `--listen` and `--token-file` come together, and with none of `--transport`, `--socket-dir`, `--nonce` or `--parent-pid`, or the sidecar exits with code 2.
- The server accepts keepalive pings every second at most, also with no stream open (`keepalive.EnforcementPolicy{MinTime: 1s, PermitWithoutStream: true}`), in both modes: it costs a local child nothing.

**The client.** `WhatsmeowSidecarOptions.External`, an `ExternalSidecarOptions` with `Address` (the same syntax as `--listen`), `TokenPath` and `Consumer`; null is embedded mode.

- The token file is read again on every connect, so a sidecar restarted with a rotated secret is reached with it.
- A connect calls GetInfo until the sidecar answers, every second on the `TimeProvider`, within `StartupTimeout`, then fails with `SidecarUnavailableException`: an orchestrator starts the two in any order. A refused token fails at once with `SidecarUnavailableException` (`Unauthenticated`), and a protocol outside `[MinProtocol, MaxProtocol]` with `SidecarVersionMismatchException`, the sidecar left running; any version is accepted (§8.5).
- Then the client opens a `grpc.health.v1` Watch on the channel. Its end is what a process exit is in embedded mode: the supervisor connects again with the same backoff (1 s doubling to 30 s, ADR 0017), and each connect calls `Connect` again for the sessions the application had connected that came back `Disconnected`, reopens the event streams after the highest seq received, and logs `SidecarRestarted`.
  A watch is event-driven and sees a restart however fast; polling GetInfo would miss a sidecar that restarted between two polls unless the contract grew an instance id.
- External mode pings a connection silent for 15 s and drops it when the answer takes 15 s more (`SocketsHttpHandler.KeepAlivePingDelay`, `KeepAlivePingTimeout`, `HttpKeepAlivePingPolicy.Always`), which fails every call on it, the watch included; embedded mode does not ping.
- Disposing the client stops its event readers, acks what their handlers finished and closes its connection; it never calls Shutdown. The sessions stay connected in the sidecar, and a later client resumes from the cursor.
- `Consumer` names the client's event cursors, default `client`, the one name of embedded mode; several clients of one sidecar each need their own.
- `SidecarPath`, `DatabasePath`, `Transport`, `History` and `SendPacing` configure a sidecar the client starts, so they fail validation in External mode instead of being ignored; the setup step applies none of their defaults, nor `WHATSMEOW_NET_SIDECAR_PATH`. `MediaRoot` is the client's path to the root the sidecar has as `--media-root`: a media path crosses as an absolute path inside the sidecar's root (`media.proto`), so both sides mount it at the same path; without it every transfer uses chunks. A relative `TokenPath` is in the host's content root on the DI path, as `DatabasePath` is (ADR 0034).
- No named pipe in External mode: its name is random per launch.

**Transport security.** TCP carries the token in clear text, so the docs limit it to a private network, such as the one between a pod's containers; TLS and mTLS wait until someone asks. Health without a token, which a Kubernetes gRPC probe would need, is not offered: a TCP probe works.

## Consequences

- A consumer that leaves the native packages' assets out (`ExcludeAssets="all"` on each of the three) builds and runs with no sidecar binary in its output and reaches an external sidecar: `build/ci.sh install` publishes such a consumer and runs it against the feed's binary started standalone, so ADR 0016's `[unknown]` is `[observed]` there.
- The whole integration suite runs a second time with the shared fixture's sidecar standalone over TCP, as its own CI job on linux-x64 (`build/ci.sh external`); the fixture restarts it itself for the crash tests.
- A sidecar may be shut down by any holder of its token; in External mode that is every client.
- A consumer name no client uses any more keeps its sessions' events in the sidecar's database (ADR 0022's known cost).
- Tests: `cmd/whatsmeow-net` `TestStandaloneServesOverTCPWithTheTokenFromTheFile`, `TestStandaloneServesOverAUnixSocketAtThePath`, `TestTheTokenFileMustHoldOneToken`, `TestStandaloneRefusesTheEmbeddedFlags`, `TestStandaloneStopsGracefullyOnSIGTERMAndSIGINT`, `TestStandaloneOutlivesTheProcessThatStartedIt`; `internal/transport` `TestListenAddress*`, `TestTheUnixSocketIsForItsOwnerAndGroupOnly`; `internal/server` `TestPingsAtTheMinimumIntervalOnAnIdleConnectionAreAnswered`; `Sidecar/ExternalTests.cs`.
