# 0042. Send pacing: options, jitter, a fake clock, and no retry delay to report

- Status: Accepted
- Date: 2026-11-02
- Evidence: issue #35, its plan review, whatsmeow at the pin, and the tests named below
- Amends: [ADR 0025](0025-messaging-and-receipts.md) (the per-session queue's pacing)

## Context

Issue #35 makes the per-session send queue of #9 tunable, with conservative defaults and jitter, and pins how it behaves around rate limits and temporary bans (ARCHITECTURE §7.5).
What the design had to follow:

- The queue paced with a GCRA token bucket, a burst of 5, then one a second, as constants, on the real clock (`time.Now`, `time.NewTimer`), with no jitter (ADR 0025) [observed: send.go, messaging.go at `ba8ba54`].
- Every send kind (text, raw, media, reaction, edit, revoke) already goes through the one `send`, so they share the session's queue; queues are a map by session [observed: send.go].
- A queued send already fails at once when its session stops being online, with the reason `Online` gives: `TEMPORARILY_BANNED` carries `expireTime` [observed: session/online.go:47-54].
- whatsmeow's `parseIQError` reads only the error child's `code` and `text`, and keeps the nodes in `ErrorNode` and `RawNode` (errors.go:179-214); nothing at the pin reads a retry delay [observed: a search of the pin for retry-after, backoff and 429]. Whether WhatsApp puts one in the node is [unknown].
- A message node WhatsApp answers with `error="429"` is `ErrServerReturnedError` (send.go:468-470): the node was written, so it is `WHATSAPP_ERROR`, recorded and never resent (ADR 0025).
- The .NET client sets no gRPC deadline on any call [observed: dotnet/src], so a send ends only by its `CancellationToken`, as `OperationCanceledException`.
- Safe send rates are not published (RESEARCH item 18) [unknown].

## Decision

- **Options.** `WhatsmeowSidecarOptions.SendPacing`, a `Whatsmeow.Messaging.SendPacingOptions { Burst = 5, Interval = 1 s }`, deep-copied with the options. Every session uses the one pacing, each with a budget of its own; no per-session override and no adaptive pacing (the issue's non-goals).
  The rules join `WhatsmeowSidecarOptionsSetup.Failures`, so both entry points fail at start: not null, `Burst` 1 to 1000, `Interval` more than zero and at most one hour, which keeps `(Burst - 1) × Interval` far inside a Go `time.Duration`.
  The client always passes them as `--send-burst` and `--send-interval` (a Go duration, in nanoseconds); the sidecar checks the same bounds (`messaging.Pacing.Check`) and exits with code 2 on a value outside them, since a zero interval would panic the jitter.
- **Defaults stay a burst of 5, then one a second**, ADR 0025's. Nothing measured supports another number; the jitter only slows the sends. Real values are recorded only when observed (#36), never provoked.
- **Jitter only delays a send that has to wait.** A send the bucket lets go leaves at once; one that has to wait for its turn waits a random duration from zero up to half an interval more (`math/rand/v2`; not an option). The bucket is never exceeded and the order stays FIFO. With a burst above one the jitter never moves the next send's turn, since less than half an interval late is still before the bucket's own turn, so the average rate stays one per interval; with a burst of one each wait adds its jitter, about 1.25 intervals on average.
- **A fake clock.** The slice's `now`, `after` and `jitter` are fields with the real ones as defaults, as the history (`after`) and chat state (`now`) slices do; the pacing and backoff tests replace them, so no test waits a real duration. A wait of zero asks the clock for none.
- **No `RetryAfter`.** `RateLimitedException` stays without one: there is no source. A 429 logs, at warn, the names of the error node's attributes, sorted, never their values, so a live 429 records whether WhatsApp sends a delay.
- **A message node answered 429 stays `WHATSAPP_ERROR`**, never retried: resending a written node breaks ADR 0025's rule.
- **No deadline API.** A send is ended by its token, as `OperationCanceledException`; the sidecar's handling of a gRPC deadline is tested in Go. So the issue's "a send whose deadline passes fails with `TimeoutException`" has no case: the client sets no deadline on any call, and §8.7 no longer promises one.

## Consequences

- A busy bot can tune the pacing; one sidecar paces all its sessions alike.
- The pacing options test starts a fake-mode sidecar of its own, because the options apply at start; tests.md lists it with the others that do.
- The rate-limit and ban behaviour did not change: the tests pin it.
- Polls and poll votes (#31) send through the same `send`, so they share the queue and the budget; whichever of #31 and #35 lands second adds them to the shared-budget test.
- Tests: `internal/messaging/pacing_test.go` (the defaults with jitter, jitter never exceeding the bucket, the jitter's range, one budget for every kind, sessions independent, a rate-limited send holding the sends behind it, a waiting send's deadline and cancel, a ban's end on queued and new sends, the 429's attribute names), the existing pacing and backoff tests moved onto the fake clock, `cmd/whatsmeow-net/pacing_test.go` (the flags and their bounds), and `Whatsmeow.IntegrationTests/Messaging/SendPacingTests.cs` (the options on a sidecar of their own, a budget per session, a cancelled waiting send throws `OperationCanceledException`, which the client raises itself, so the sidecar's side of it is the Go test's, a ban on queued and new sends and the application connecting again, the defaults, invalid options before a spawn, the copy across a restart) and `Hosting/HostingTests.cs` (binding and validation from configuration).
