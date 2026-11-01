# 0004. Reusing whatsmeow protos as typed fields; hand-written public records

- Status: Accepted
- Date: 2026-10-31
- Evidence: [ARCHITECTURE §14](../ARCHITECTURE.md#spike-results-2026-10-31) questions 1 and 5

## Context

whatsmeow commits 57 `.proto` files (56 proto2, 1 proto3), regenerated about weekly [observed: [RESEARCH.md](../RESEARCH.md#reuse-whatsmeows-57-proto-files-on-the-wire-never-in-the-public-api) "Reuse whatsmeow's 57 proto files"].
Hand-written mirrors drift: whatsmeow-node's `stanzaId`/`stanzaID` mismatch meant quoted replies never worked at runtime [observed: RESEARCH.md "Six earlier wrappers", citing the whatsmeow-node CHANGELOG].
GOWS carries JSON strings inside a proto envelope, named by Go reflect type [observed: RESEARCH.md "Six earlier wrappers"].
A typed message field and a `bytes` field holding its serialization are identical on the wire [observed: RESEARCH.md "Reuse whatsmeow's 57 proto files"].
Generated types are mutable and change with every bump, so exposing them would make each upstream change a breaking change for users (same section).

Question 1, grpc-go 1.85.0 and protobuf-go 1.36.12 at the pin [observed: §14 Spike results, Question 1]:

- whatsmeow protos have 125 proto2 `required` fields; `Message.extendedTextMessage.endCardTiles[]` reaches `VideoEndCard`, which has four.
- grpc-go's default codec refuses to marshal such a message, and refuses to unmarshal it on the server as `Internal` with no `ErrorInfo`, before any interceptor runs.
- whatsmeow's own decrypt path (`proto.Unmarshal`, `message.go:290`, `:446`) rejects the same message, so inbound messages are already complete; the risk is only .NET to sidecar.
- C# Google.Protobuf 3.36.2 accepts the partial message in `ToByteArray()` and `ParseFrom()`; `IsInitialized()` returns false.
- With `grpc.ForceServerCodecV2(lenient codec)` the partial message reaches the handler, whatsmeow's send path (`proto.Marshal`, `send.go:1106`, `:1261`) rejects it, and the slice returns `InvalidArgument`.

Question 5: the 57 generated WhatsApp types build, serialize, parse, compare and look up descriptors under Native AOT with zero IL/AOT warnings [observed: §14 Spike results, Question 5].

## Decision

Phase 0 verdicts:

- Question 1: go for typed fields, because the server-only lenient codec turns an incomplete message from .NET into `INVALID_ARGUMENT` instead of a bare `Internal`.
- Question 5: go, because the 57 generated WhatsApp types and the Vogen types with `Conversions.SystemTextJson` publish under Native AOT with zero IL/AOT warnings.

Design:

- `whatsmeow.v1` (proto3) imports whatsmeow's protos unmodified from `third_party/whatsmeow`, copied from the exact pinned module directory, and embeds them as typed fields.
- The Go server installs a lenient codec (`AllowPartial` both ways) with `grpc.ForceServerCodecV2`, never a global `encoding.RegisterCodec`.
- An incomplete message from .NET is rejected by whatsmeow's send path and mapped to `INVALID_ARGUMENT`.
- Go imports whatsmeow's generated packages and never regenerates upstream protos (invariant 11).
- The stable public C# API is hand-written `sealed record` types.
- Generated types appear only behind the `[Experimental]` `Raw` hatch (`MessageReceived.Raw`, `client.Messages.SendRawAsync`), from `Whatsmeow.Protocol.WhatsApp`, documented as unstable (invariant 6).

## Consequences

- Go assigns whatsmeow's structs to contract fields with no mapping code; .NET slices map protos to records.
- Records lag new upstream message types; `Raw` covers the gap.
- Switching a field to `bytes` later would not break the wire [inferred: from the wire equality above; not tested].
- `buf lint` and `buf breaking` exclude upstream protos; upstream wire changes are reported but never block ([ADR 0015](0015-buf-breaking-gates-from-first-published-tag.md)).
- Not covered by the AOT spike: `Grpc.Net.Client` and the generated `whatsmeow.v1` stubs, so `tests/AotSmoke` stays the gate ([ADR 0006](0006-no-runtime-reflection.md)).
