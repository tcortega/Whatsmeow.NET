# 0011. Integration tests by default; .NET unit tests only for the dispatcher and supervisor

- Status: Accepted
- Date: 2026-10-31
- Evidence: none from §14 directly; question 7 supports the supervisor unit test

## Context

An earlier draft planned a broad .NET unit layer.
The house tests guide makes integration tests the default, on the principle that a green suite is worthless if it still passes when the code is wrong ([guidelines/tests.md](../guidelines/tests.md)).
In the prior art almost every failure happened at the seams (events never forwarded, acks before persistence, streams that completed silently), not inside whatsmeow [observed: [RESEARCH.md](../RESEARCH.md#conclusion) Conclusion].
Unit tests with a mocked transport or sidecar cannot see those seams [inferred].
One `FakeTimeProvider` drove a Polly exponential retry (2s, 4s, 8s gaps) and every "now" read in the same test [observed: §14 Spike results, Question 7], so time-driven logic can be unit tested deterministically.

## Decision

- Every slice gets one FullCycle through the public `IWhatsAppClient` against a real `whatsmeow-net` started with `--fake-whatsapp`, a test-only build tag that replaces whatsmeow with a scripted fake and exposes a control service.
- These live in `tests/Whatsmeow.IntegrationTests`, and they cover proto-to-record mapping and error mapping.
- .NET unit tests exist only for dispatcher per-chat ordering and the ack watermark, and for the supervisor restart/backoff state machine driven by `FakeTimeProvider` (`tests/Whatsmeow.Client.Tests`).
- The delivery invariants get kill-either-process tests: no event is lost, and a repeat only follows the last acked cursor and carries the same `MessageId` and `SessionId`.
- Go keeps per-slice unit tests with whatsmeow faked behind each slice's small interface, always with `-race`.
- Live WhatsApp tests are opt-in and never run in default CI.

The layers are in [ARCHITECTURE §11](../ARCHITECTURE.md#11-testing-strategy), and the rules in [guidelines/tests.md](../guidelines/tests.md).

## Consequences

- The `--fake-whatsapp` build and its control service must exist before the first slice is complete.
- One shared sidecar fixture serves the suite; tests never start a sidecar per test or mock the transport, the dispatcher or the sidecar.
- The library gets no interfaces for testability ([ADR 0012](0012-iwhatsappclient-interface-exception.md)).
- Each new test must be seen failing for the right reason, then mutated to prove it dies.
