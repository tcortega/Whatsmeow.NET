# 0008. Product name Whatsmeow.NET; identifiers rooted at `Whatsmeow`; sidecar binary `whatsmeow-net`

- Status: Accepted
- Date: 2026-10-31
- Evidence: none from §14; a naming decision

## Context

An earlier draft called the project Wameow.
The sidecar is not a second product, but its names land in the contract, in process listings and in generated code, so a leftover codename would leak everywhere.
Upstream is the Go library whatsmeow, and readers must be able to tell the two apart.

## Decision

- The product, repo and docs are **Whatsmeow.NET**.
- Code identifiers follow the Akka.NET and Quartz.NET pattern: the brand keeps `.NET`, and packages, namespaces and types drop it (`Whatsmeow.Client`, `WhatsmeowSidecar`, `WhatsmeowException`).
- The proto package is `whatsmeow.v1`, imported in Go as `whatsmeowv1` so it never collides with `go.mau.fi/whatsmeow`.
- The Go module is `github.com/tcortega/Whatsmeow.NET/sidecar`, with the repo's case.
- The sidecar binary is `whatsmeow-net`, not `whatsmeowd`, which reads as an upstream daemon.
- The `ErrorInfo` domain is `whatsmeow.net`, the default database `whatsmeow-net.db`, and the Windows pipe prefix `\\.\pipe\whatsmeow-net-`.
- The name Wameow is retired, including in the Go layer.
- Upstream stays lowercase `whatsmeow` in prose.

The full table is [ARCHITECTURE Naming](../ARCHITECTURE.md#naming).

## Consequences

- The default database name avoids colliding with a whatsmeow or mautrix database in the same directory.
- `go.mod` must not lowercase the module path.
- A new public identifier follows the same pattern, and the Naming table is updated with it.
