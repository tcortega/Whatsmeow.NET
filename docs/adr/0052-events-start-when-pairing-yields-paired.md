# 0052. Events start when pairing yields Paired

- Status: Accepted
- Date: 2026-11-02
- Evidence: issue #112, its plan review, and the tests named below
- Amends: [ADR 0022](0022-outbox-event-stream-and-dispatcher.md) (when a session's events start), [ADR 0034](0034-hosting-package.md) and [ADR 0037](0037-traces-and-metrics.md) (the lines that name the first `ConnectAsync`)

## Context

A session's events started only on its first successful `ConnectAsync` in the process (ADR 0022), so a session paired with `PairWithQrCodeAsync` or `PairWithPhoneNumberAsync` ended `Connected` while its handlers got nothing until the application also connected it [observed: `PairingTests.FullCycle_PairWithQrCode_ThenAMessageArrives_TheHandlerGetsItWithNoConnect` red on main, timed out waiting for the message].
The restart side had no gap: pairing already remembers the session at its first code (ADR 0021), so a sidecar restart connects it again [observed: `PairingRestartTests`].

- `Paired` is yielded only once the session is usable and `Connected`, so a pairing that fails or is cancelled never yields it (ADR 0021).
- Events are durable outbox rows read from the consumer's cursor, so starting later loses nothing; only an event dispatched while no handler takes it is not repeated (ADR 0022).
- `EventPipeline.Start` runs once and under `ExecutionContext.SuppressFlow`, wherever it is called from [observed: `EventPipeline.cs` `Start`].

## Decision

- A session's events start on its first successful `ConnectAsync` in the process, or when its pairing yields `PairingUpdate.Paired`, just before the caller gets it, so handlers registered before pairing get every event.
- A pairing that fails, is cancelled, or is stopped before `Paired` starts no events, also one stopped after the link, whose session the login still connects: a caller that stopped pairing gets its events from `ConnectAsync`.
  Remembering for a restart is unchanged: it still happens at the first code (ADR 0021).
- `FakeWhatsAppClient` needs no change: its events flow from its construction, with or without a connect or a pairing (ADR 0036).

## Consequences

- The obvious code (register handlers, pair, wait for messages) works with no `ConnectAsync`.
- In a host, a session a service pairs in its own `StartAsync` has its events flowing before a later service can fail the start: the same window ADR 0034 leaves for a session the application connects there.
- Tests: `PairingTests.FullCycle_PairWithQrCode_ThenAMessageArrives_TheHandlerGetsItWithNoConnect` and its phone-code twin, `Pair_Cancelled_StartsNoEvents_...` and `Pair_TheCodesRunOut_StartsNoEvents_...`, and `PairingRestartTests.Pair_ThenTheSidecarRestarts_ConnectsTheSessionAgain_AndItsMessagesReachTheHandler`.
