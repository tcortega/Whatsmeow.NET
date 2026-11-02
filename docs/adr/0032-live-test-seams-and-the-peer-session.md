# 0032. Live test seams and the peer session

- Status: Accepted
- Date: 2026-11-02
- Evidence: issue #10, its plan and the coordinator's rulings, whatsmeow at the pin, and the tests named below
- Amends: [ADR 0021](0021-pairing.md) (the phone keeps only the live session's device), [ADR 0025](0025-messaging-and-receipts.md) (the strict id rule, in the live build only), docs/guidelines/tests.md ("fake-only seams stay on fake-mode fixtures")

## Context

Issue #10 is the Phase 1 exit: against real WhatsApp, kill either process mid-stream and lose no message, and measure what the design left [unknown].
The live tests need things that real WhatsApp alone does not give:

- Incoming messages at a chosen moment, with ids the test knows. The live session's own send to its own chat produces no event on that companion: whatsmeow encrypts a message for every device of the account but the sending one (send.go:889, 1314-1316) [observed: source at the pin].
- A message whose answer is withheld while the session stays connected, as a failed outbox write withholds it (ARCHITECTURE §7.3), to measure when WhatsApp delivers it again [unknown].
- A send with an id whatsmeow would not make, which the sidecar refuses (`^3EB0[0-9A-F]{18}$`, ADR 0025), to measure whether WhatsApp takes it [unknown].
- A .NET process that acks exactly once, at a known point, so "only messages after the last acked cursor repeat" is checked exactly.

## Decision

- **A `livetest` build tag**, only in files named `livetest_*` in `sidecar/internal/events/` and `sidecar/internal/messaging/` (`tools/check-repo` refuses it anywhere else). Each seam is a constant or a small type in a tagged file, with a no-op twin behind `!livetest`; no mutable global.
  - `messaging`: the id rule becomes `^[0-9A-Za-z]{1,64}$`.
  - `events`: the handler withholds the answer to the first delivery of each text message that starts with `wmn-withhold `, and logs it at warn with its id; every later delivery is recorded.
  - `build/ci.sh` builds, tests and lints the sidecar with the tag too, fails when the release build of `whatsmeow-net` links a `livetest_on.go` (and when the tagged build links none, so the check sees the seams), and fails when a packaged binary was built with `-tags livetest`.
  - `Whatsmeow.LiveTests` builds the sidecar with the tag; no shipped artifact does.
- **A persistent peer session**, `peer`, on a second account (the owner's second number): paired once, like `live`, on its own database (`WHATSMEOW_LIVE_PEER_DATABASE`), in a sidecar process of its own, so killing the live sidecar never touches it. It is never logged out, deleted or re-paired. It sends the bursts to the live account and receives what the live session sends, so the live tests send real traffic between two accounts and need no person once both sessions are paired. The tests that need it are `[LiveFact(NeedsPeer = true)]`.
- **The account's own JID from `SessionInfo`.** `SessionInfo.Jid` and `.Lid` report this device's JIDs (the PR before #10's); a test takes the account's chat as that JID with device 0.
- **The owner's ping, for the phone-only tests.** A test about the phone itself (its own message to "Message yourself", sent from device 0, and its id format) needs the owner to send `wmn ping` from the phone; its chat is the own JID (message.go:143-156) [observed: source], and its id is a sample of the phone's id format. Those tests are `[LiveFact(NeedsOwner = true)]`, on only with `WHATSMEOW_LIVE_OWNER` set, and their ping wait has a bound of its own on top of the test's timeout.
- **This amends the first plan**, a throwaway second companion of the same account paired by QR code each run: it needed a scan and a ping per run, and could not show receipts or traffic from another account.
- **The .NET kill** runs `SidecarHost events-live`, the fake-mode host's code on a release build, on a held clock (an internal `WhatsmeowSidecar.StartAsync` with a `TimeProvider`).
- **Shared checks.** `IntegrationTests/Events/DeliveryChecks.cs` is called by the fake-mode kill tests and the live ones alike.

## Consequences

- A release binary and the packages never contain the seams; CI proves it on every run.
- Once `live` and `peer` are paired, the live run needs no person; only the `NeedsOwner` tests do, when the owner opts in.
- The .NET kill's exact ack assumes the account's other traffic finished its handlers when the 4th message's handler acks [inferred]; a check failure there names the messages, so interleaved traffic is visible.
- Whether a recovered undecryptable message keeps its id is not measured: making one live meant manipulating Signal session state, which this issue does not do. It stays [inferred] and goes to a follow-up issue.
