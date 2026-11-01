# 0014. AF_UNIX as the default transport on Windows too; named pipe as the selectable fallback

- Status: Accepted
- Date: 2026-10-31
- Evidence: [ARCHITECTURE §14](../ARCHITECTURE.md#spike-results-2026-10-31) question 2, Transport spike

## Context

grpc-go #7039 reports that sustained, unpaced streaming over AF_UNIX on Windows fails with "http2: frame too large" / PROTOCOL_ERROR, and it was open when researched [observed: [RESEARCH.md](../RESEARCH.md#grpc-over-a-private-unix-socket-wins-with-a-windows-asterisk) "gRPC over a private Unix socket wins"].
Microsoft's sample uses named pipes on Windows; RESEARCH.md recommended building both transports and picking the Windows default after a soak (same section).

Question 2, GitHub Actions run 41228306743, spike code at tag `spike/windows-transport` (commit `0e5774d`) under `spikes/windows-transport/`: Go 1.27.3 grpc-go 1.85.0 server, .NET 10.0.14 Native AOT client, on Windows Server 2022 (build 20348, x64), Windows Server 2025 (build 26100, x64) and Windows 11 (build 26200, arm64) [observed: §14 Spike results, Question 2]:

- The #7039 reproducer shape (Go client and server, AF_UNIX, bidi, 500 B messages back to back, 90 s): 15.1-21.5M messages echoed in order, no transport error, on all three hosts.
- That matches a third-party report on the issue that the corruption was a Go runtime bug, present on Go 1.22.12 and gone on Go 1.26.5.
- .NET client to Go sidecar, every test passing on both transports and all three hosts, with 60 s or 30 s budgets:

| Test | AF_UNIX | Named pipe |
|---|---|---|
| server stream, 512 B, in order | 34-40M events, 563k-654k/s | 34-38M events, 564k-631k/s |
| server stream, 64 KiB messages | 1,078-1,482 MiB/s | 393-545 MiB/s |
| client stream upload, 64 KiB chunks, in order | 308-390 MiB/s | 328-469 MiB/s |
| bidi 500 B back to back + a ping every 50 ms | 3.2-4.2M echoed in order | 1.5-2.1M echoed in order |
| stdin EOF stops the sidecar | exit 0 | exit 0 |

The spike's named pipe used an owner-only DACL `D:P(A;;GA;;;OW)` on the server and `PipeOptions.CurrentUserOnly` on the client [observed: tag `spike/windows-transport`, `spikes/windows-transport/go/server/pipe_windows.go:16`, `spikes/windows-transport/dotnet/Program.cs:50`].

## Decision

Phase 0 verdict:

- Question 2: go for AF_UNIX as the Windows default, because both transports passed every test on three Windows hosts and the #7039 reproducer saw no transport error.

Design:

- The Unix domain socket is the default transport on every OS, Windows included.
- On Windows the named pipe stays selectable as a fallback: `\\.\pipe\whatsmeow-net-` plus a 128-bit random suffix, an owner-only DACL (go-winio `D:P(A;;GA;;;OW)`), and `PipeOptions.CurrentUserOnly` on the client.
- The sidecar sets an explicit owner-only DACL on the Windows socket directory at creation, because `os.Chmod(dir, 0700)` does not set an ACL there ([ARCHITECTURE §10](../ARCHITECTURE.md#10-security)).
- The per-launch token stays the real gate on both transports.

## Consequences

- In the table above, AF_UNIX was about 2-4x faster for server streams of 64 KiB messages and about 1.5-3x for bidi.
- Client-stream upload was comparable, with the named-pipe range equal or higher (328-469 vs 308-390 MiB/s) [inferred: ratios from the range ends across the three hosts, not paired per host].
- Without the explicit DACL, the socket directory's protection would come from the user's `%LOCALAPPDATA%\Temp` ACL; #4 measured it on a CI runner and recorded it in [ARCHITECTURE §10](../ARCHITECTURE.md#10-security).
- RESEARCH.md also reports third-party network stacks on managed machines breaking AF_UNIX `connect` for the JDK; whether that affects us is [unknown], and the fallback is the way out.
- Both transports stay in the code and in CI.
- The spike code stays reachable through the `spike/windows-transport` tag; the branch was deleted after the results were recorded.
