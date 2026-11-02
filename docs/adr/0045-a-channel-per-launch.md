# 0045. A channel per launch

- Status: Accepted
- Date: 2026-11-02
- Evidence: issue #85; `Sidecar/RestartTokenTests.cs`, `TestListenNeverReusesAnEarlierLaunchsEndpoint` and `TestListenLeavesWhatIsNotASocket`, with the mutations below; grpc-dotnet v2.85.0 `GrpcChannel.cs` and `GrpcProtocolHelpers.cs`; .NET 10.0.14 `NamedPipeClientStream.cs`
- Amends: ARCHITECTURE §8.5 (socket directory), §8.6, §10; ADR 0017 (the socket's name); ADR 0037 (where errors are counted)

## Context

Every sidecar restart, calls the client made reached the next sidecar while it was still starting, carried the last launch's token, and were refused `UNAUTHENTICATED`: the ack loop's retries, the event streams' reopens and the application's own calls.
On win-x64 that was about 1,300 calls in one suite run [observed: #85, PR #78 run 41447476908].
A refused call surfaced as `SidecarUnavailableException` with `ErrorCode` `Unauthenticated` instead of a plain unreachable sidecar.

The client had one channel for every launch.
Its interceptor read the current launch's token when a call started, and its `ConnectCallback` read the current endpoint when a connection opened [observed: `LocalChannel.cs` before this ADR].
Every launch listened at the same socket path (`s` in the socket directory, ADR 0017), before its ready line, and the client kept the dead launch current until the next one's ready line.
So a call in that window took the old token to the new process.
Clearing the current launch before the next one spawned was not enough: a call could read the token before the switch and open its connection after it, because one HTTP/2 pool served every launch [observed: #85, 2 of 5 local runs].
The token goes on a call and the endpoint on a connection, so one channel cannot tie the two together.

On main, `RestartTokenTests` failed in every run [observed: macOS]: 8 of 8 in its first form (8 callers looping `GetInfoAsync` from the next sidecar's `starting` line, 24 `Unauthenticated` each), and 3 of 3 in its final one (8 callers, 1 ms apart, from each of 3 crashes until the client counts the next launch, one connected session).

## Decision

- **One channel per launch.** `LocalChannel` takes its launch's endpoint and token, fixed for its lifetime; the client creates it right after the ready line.
  A call made on it carries that launch's token and can only connect to that launch's endpoint.
- **A stable invoker in front.** `LaunchInvoker` (`Core/Transport/`) starts each call on the current launch's channel. With no launch (before the first one, and after the dispose) it starts the call on a channel with no endpoint, which fails it as unreachable without connecting, as before the first launch until now (ADR 0034).
  Everything that took the one channel's invoker takes this one, so the fake WhatsApp control still follows restarts (ADR 0019).
  The error counter wraps it, so every failed call is still counted in one place (ADR 0037).
- **"Ready" is the ready line.** A launch becomes current when its ready line is read, as before, because the launch's own `GetInfo` and its reconnects go through the stable invoker.
- **Every launch listens at a socket of its own.** The sidecar names the socket `s-` plus 64 random bits, and removes the sockets earlier launches left in the directory before it listens: entries named `s-` that are sockets, nothing else. On Windows a directory entry reports an AF_UNIX socket as `fs.ModeSocket` from its reparse tag [observed: Go 1.27.3 `os/types_windows.go`; not run on Windows]. So a dead launch's channel, which opens a new connection when a call needs one, finds no socket or a refusing one, and its call fails as unreachable; it never reaches a later launch.
  The named pipe already had a random name per launch (ADR 0014).
- **A pipe connect gives up after 2 s.** A launch's pipe exists from before its ready line until it exits, so a dead launch's name never comes back, and `NamedPipeClientStream.ConnectAsync` without a timeout tries again forever while the pipe is missing [observed: .NET 10.0.14 `NamedPipeClientStream.cs:131-162`, `NamedPipeClientStream.Windows.cs:113-129`]. So a call to a dead launch over the pipe, an event stream reopening before the next ready line say, would hang until a later launch.
  The connect takes a token that the `TimeProvider` cancels after 2 s, the default of go-winio's own `DialPipe` (v0.6.4 `pipe.go:235-236`); the pipe checks it every 50 ms. A connect cut by it throws an `IOException`, so gRPC reports the call `Unavailable` like any unreachable sidecar: a `TimeoutException` there would be `Internal` (grpc-dotnet v2.85.0 `GrpcProtocolHelpers.cs:450-462`) [observed: source; not run on Windows].
  A Unix socket needs no bound: a connect to a dead launch's path is refused at once, or finds no file.
  The client already took the endpoint from the ready line, so it needs no change for this.
- **The last channel is disposed one launch late.** A launch's channel is disposed when the launch after the next one is published, or at the dispose.
  Disposing a `GrpcChannel` cancels its active calls (grpc-dotnet v2.85.0 `GrpcChannel.Dispose`), and a call made on the dead launch just before the switch was still running then: disposing at the switch ended it as `TaskCanceledException` in every run [observed: mutation 3].
  A call to a dead launch fails fast instead, over the Unix socket: no `GetInfoAsync` in `RestartTokenTests` took over 20 ms with the channel never disposed [observed: one run, macOS]. Over the pipe it fails within the 2 s bound [inferred: not run on Windows]. Both are far shorter than a launch, so by the next switch none is left [inferred].
  At most two channels are open at a time.
- **A cancellation from the dispose stays.** A call still running when the sidecar is disposed, or one a crash ends with no status, ends as `OperationCanceledException`, as a call to a dead sidecar did before this ADR [observed: `Sessions/WatchDownTests.cs`, `Telemetry/TelemetryCrashTests.cs`]. No behaviour of that kind changes.

## Consequences

- A call made before the next launch is ready fails as unreachable, `SidecarUnavailableException` with `ErrorCode` `Unknown`, and never reaches the next launch; the internal retries no longer reach it either, so the next sidecar refuses none of them.
- `RestartTokenTests` passes in every run [observed: 5 of 5, macOS]; it also asserts that calls failed during every restart, so it never passes with no call in the window. Mutations, 3 runs each:
  1. One channel again, the token read per call, with the sockets still unique: red in 2 of 3, 4 and 14 `Unauthenticated`.
  2. The fixed socket name `s`, with a channel per launch: red in 3 of 3, 23 to 32 `Unauthenticated`.
  3. The last channel disposed at the switch: red in 3 of 3, `TaskCanceledException`.
- `TestListenNeverReusesAnEarlierLaunchsEndpoint` replaces `TestListenReplacesAStaleSocketFromACrashedRun`. It fails with the fixed name (the listen finds the address in use) and without the removal (the earlier socket is still there). `TestListenLeavesWhatIsNotASocket` fails when the removal does not check for a socket.
- For the platform pass, not run in PR CI, which is linux-x64 only: the pipe transport's bound (a reopen before the next ready line fails `Unavailable` within 2 s and the session's events go on after the restart), and the earlier sockets' removal on Windows (`fs.ModeSocket` on a `ReadDir` entry).
- A hard-killed .NET process still leaves its socket directory behind (ADR 0017), now with the last launch's socket in it.
- This also removes the refused calls that tripped grpc-go's ping policy in #77.
