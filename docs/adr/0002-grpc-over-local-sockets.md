# 0002. gRPC over local sockets as the IPC transport

- Status: Accepted
- Date: 2026-10-31
- Evidence: [ARCHITECTURE §14](../ARCHITECTURE.md#spike-results-2026-10-31) Transport spike and question 2

## Context

The sidecar ([ADR 0001](0001-go-sidecar-over-csharp-port-or-cgo.md)) needs commands with deadlines and cancellation, one ordered event stream with backpressure, chunked media, and typed errors.
Prior art uses stdio NDJSON or JSON-RPC (whatsmeow-node, WhatsBox, wuzapi), gRPC over a Unix socket (GOWS), or in-process FFI (neonize) [observed: [RESEARCH.md](../RESEARCH.md#six-earlier-wrappers-already-made-the-mistakes-worth-avoiding) "Six earlier wrappers"].
RESEARCH.md found gRPC (grpc-go server, Grpc.Net.Client client) to be the only option with maintained first-party libraries on both sides that also gives streaming, deadlines, cancellation, HTTP/2 flow control, status codes and codegen [observed: [RESEARCH.md](../RESEARCH.md#grpc-over-a-private-unix-socket-wins-with-a-windows-asterisk) "gRPC over a private Unix socket wins"].
Raw length-prefixed protobuf over stdio was the strongest lightweight alternative, but it would mean rebuilding multiplexing, cancellation and backpressure ourselves (same section).

## Decision

`whatsmeow.v1` is served over gRPC (HTTP/2 h2c) on a private local socket: a Unix domain socket in a freshly created `0700` directory, with a named pipe as the Windows fallback ([ADR 0014](0014-af-unix-default-on-windows.md)).

- Commands are unary RPCs.
- Events are one long-lived server stream with a durable `seq`, resumed with `after_seq` and acked with `AckEvents` ([ADR 0003](0003-at-least-once-delivery-synchronousack-sqlite-outbox.md)).
- Media moves by path or as 64 KiB chunk streams, never whole files in one message (invariant 8).
- A per-launch 32-byte token arrives on stdin, never argv or environment, and an interceptor checks it in constant time.
- The ready line echoes a nonce, so the client never connects to a stale sidecar.
- .NET connects through `SocketsHttpHandler.ConnectCallback`.
- Readiness comes from the ready line plus `grpc.health.v1`, never from `GrpcChannel.ConnectAsync` or `GrpcChannel.State`.
- `MaxReceiveMessageSize` and `MaxSendMessageSize` are 16 MiB on both sides.

Checked in Phase 0:

- Spawn, token on stdin, JSON ready line on stdout, and a unary call over a Unix socket through `ConnectCallback` work, with a Native AOT client and 0 IL/AOT warnings [observed: §14 Spike results, Transport spike, osx-arm64].
- A server stream of 200,000 events of 512 bytes arrived in order at about 849,000 events/s [observed: same].
- `GrpcChannel.State` and `ConnectAsync` throw `InvalidOperationException` when `ConnectCallback` is set [observed: same].
- Closing stdin stops the sidecar with exit 0 [observed: same, and Question 2 on three Windows hosts].

## Consequences

- Dependencies are `Grpc.Net.Client`, `Google.Protobuf` and `Grpc.StatusProto`; `Grpc.Core` is banned (invariant 7).
- gRPC types never reach the public API (invariant 6, [ADR 0004](0004-whatsmeow-protos-as-typed-fields-hand-written-records.md)); `RpcException` is mapped to `WhatsmeowException` subtypes.
- Errors travel as a gRPC status plus `google.rpc.ErrorInfo` in domain `whatsmeow.net`, and clients map on `reason` only ([ARCHITECTURE §6](../ARCHITECTURE.md#6-the-whatsmeowv1-contract)).
- The client owns event-stream reconnection, because server-streaming calls are not auto-retried after the first message [observed: RESEARCH.md "gRPC over a private Unix socket wins", citing MS Learn on gRPC retries].
- External mode (Phase 4) reuses the same contract over another endpoint.
