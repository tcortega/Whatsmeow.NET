# 0001. Go sidecar over a native C# port or cgo FFI

- Status: Accepted
- Date: 2026-10-31
- Evidence: [ARCHITECTURE §14](../ARCHITECTURE.md#spike-results-2026-10-31) questions 3 and 6

## Context

whatsmeow is a Go library.
It has no tags, about 15-35 commits a month, weekly proto regenerations and a breaking signature change every month or two [observed: [RESEARCH.md](../RESEARCH.md#ship-whatsmeow-to-net-over-grpc) intro, citing the whatsmeow repo, 2026-10-29].
Three ways to reach it from .NET were on the table: port it to C#, load it in-process as a cgo c-shared library through P/Invoke, or run it as a child process.
The in-process FFI route is what neonize does, and its history shows FFI leaks, `c_char_p` truncating media and manual `FreeBytesStruct` [observed: [RESEARCH.md](../RESEARCH.md#six-earlier-wrappers-already-made-the-mistakes-worth-avoiding) "Six earlier wrappers", citing the neonize CHANGELOG].
A native C# port was not analysed in RESEARCH.md or in §14.
Its cost would be re-implementing the churn above in C# every week [inferred: from the churn figures; no port was attempted or estimated].

## Decision

Wrap whatsmeow in a separate Go child process, `whatsmeow-net`, built with `CGO_ENABLED=0` and supervised by the .NET library.
No cgo c-shared library, no P/Invoke into Go, no C# reimplementation of the protocol.

The route was checked in Phase 0:

- A CGO-free, sidecar-shaped binary (whatsmeow client, sqlstore, gRPC health server, zerolog) is 21.9-23.3 MB across linux, darwin and windows on amd64 and arm64 [observed: §14 Spike results, Question 6].
- The Linux binaries ran on `alpine:3` and `debian:stable-slim` [observed: §14 Spike results, Question 6].
- SQLite without CGO (`modernc.org/sqlite`) carries whatsmeow's store and our outbox under contention with 0 `SQLITE_BUSY` on the final DSN [observed: §14 Spike results, Question 3].

## Consequences

- A fault in Go ends the child process, not the host application, and there are no FFI memory contracts [inferred: process isolation; no crash spike was run].
- The two processes need an IPC contract ([ADR 0002](0002-grpc-over-local-sockets.md)), supervision and tethering ([ARCHITECTURE §8.5](../ARCHITECTURE.md#85-sidecar-supervision)), and per-OS native packages ([ADR 0007](0007-per-os-native-nuget-packages-exact-pins.md)).
- A whatsmeow bump changes Go adapter code only, never the `whatsmeow.v1` contract or the public C# API ([ARCHITECTURE §1](../ARCHITECTURE.md#1-goals-and-non-goals)).
- Media processing stays in .NET, so the sidecar stays CGO-free ([ARCHITECTURE §1](../ARCHITECTURE.md#1-goals-and-non-goals) non-goals).
