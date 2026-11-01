# 0009. BCL time instead of the house NodaTime rule

- Status: Accepted
- Date: 2026-10-31
- Evidence: [ARCHITECTURE §14](../ARCHITECTURE.md#spike-results-2026-10-31) question 7, "Vogen and options findings"

## Context

The house rules (bridge, compras, chrominal) use NodaTime.
This library is a NuGet package, so NodaTime in its public API would be forced on every consumer.
It does no zone or calendar math, only timestamps, timeouts, backoff, `RetryAfter` and `Until` [inferred: from ARCHITECTURE §7-§8, nothing there needs a zone].

Question 7 [observed: §14 Spike results, Question 7]:

- Polly v8 has no NodaTime support; it schedules only through `System.TimeProvider` (no NodaTime or `IClock` reference in Polly.Core 8.9.0).
- NodaTime 3.3 bridges one way only, `TimeProvider.ToClock()`; `IClock` has no timer API, so no `IClock`-backed `TimeProvider` can exist, and `FakeClock` cannot drive delays.
- One `FakeTimeProvider` drove a Polly exponential retry (2s, 4s, 8s gaps) and every "now" read in the same test.
- BannedApiAnalyzers 5.6.0 flags each ambient-time and timer ID listed in [guidelines/csharp.md](../guidelines/csharp.md#bannedsymbolstxt), and none of their `TimeProvider` overloads.

`TimeSpan` binds from `00:00:30` through the binding generator under AOT; `NodaTime.Duration` does not bind from a configuration string at all, and both binders keep the old value without an error [observed: §14 Spike results, "Vogen and options findings"].

So NodaTime would have needed an interop file, two fakes, a hand-parsed options step, an extra protobuf package, and a forced dependency on every consumer (§14 Spike results, Question 7).

## Decision

Antonio chose BCL time; this library opts out of the house NodaTime rule.

- A point in time is `DateTimeOffset` with a zero offset (UTC); never `DateTime`.
- A length of time is `TimeSpan`.
- "Now", elapsed time and every timer come from one injected `TimeProvider` (default `TimeProvider.System`).
- Proto `Timestamp` and `Duration` map with Google.Protobuf's own conversions.
- No NodaTime in any shipped package; no `DateOnly`, `TimeOnly` or `TimeZoneInfo`.
- `BannedSymbols.txt` bans ambient time and the timer overloads that bypass `TimeProvider`.

The rules are in [guidelines/csharp.md](../guidelines/csharp.md#time).

## Consequences

- Invariant 7 bans `DateTime` and any clock or timer that bypasses the injected `TimeProvider` ([ARCHITECTURE §13](../ARCHITECTURE.md#13-invariants)).
- Tests use one `FakeTimeProvider` for now, delays and Polly together.
- Time options are `TimeSpan` and bind under AOT ([guidelines/options.md](../guidelines/options.md)).
- If the library ever needs a civil date or a zone, that is a new ADR, not a quiet `TimeZoneInfo`.
- [AGENTS.md](../../AGENTS.md) records this as one of the two house-rule exceptions.
