# 0049. The hosted sample: public API only, one setup method its fake-mode FullCycle runs, its own dependencies

- Status: Accepted
- Date: 2026-11-02
- Evidence: issue #42 and its plan review; `Samples/EchoBotTests.cs` and the mutations in its PR
- Amends: nothing; it adds the samples to the tree (ARCHITECTURE §4)

## Context

Phase 3 ends with a hosted sample that runs two sessions (ARCHITECTURE §15), and the README quick start (#51) copies it, so CI has to prove it works.
What the sample can reach decided the shape:

- The fake WhatsApp mode is `WhatsmeowBuilder.UseFakeWhatsApp()`, internal, seen only by `Whatsmeow.IntegrationTests` and `Whatsmeow.Testing` [observed: `Whatsmeow.Extensions.Hosting.csproj`], and a release sidecar refuses `--fake-whatsapp` (ADR 0019).
  A sample on the public API alone cannot start a fake-mode sidecar, and run as a process of its own, nothing could reach the fake's control on its private socket and token.
- `AddWhatsmeow()` called again adds nothing and returns a builder, and the fake mode is read when the sidecar is created, so it can be added after the sample's own setup [observed: `WhatsmeowServiceCollectionExtensions.cs`].
- A session's events reach its handlers from its first `ConnectAsync` in the process (ARCHITECTURE §8.4); pairing alone leaves the session connected with no events flowing [observed: the FullCycle, red until the sample connected after `Paired`].
- `WhatsmeowSessions.Client` is internal; a sample reaches its sessions as keyed clients [observed: `Sessions/WhatsmeowSessions.cs`].
- Two accounts that each run an echo session would echo each other's echoes forever [inferred: an echo arrives at the other account as a text from someone else].
- A message WhatsApp delivers again is recorded once: the outbox keeps one row per natural key (`ON CONFLICT (session_id, dedupe_key) DO NOTHING`, `outbox.go:179`), so only a .NET process that ends before its ack sees an event again [observed: a redelivery in the FullCycle reached no handler, so an echo id derived from the message's showed nothing a random one did not].

## Decision

- **Where.** `dotnet/samples/EchoBot/`, a plain executable in `Whatsmeow.slnx`, so CI builds it; `IsPackable` false, since `dotnet pack` packs the solution.
  It references `Whatsmeow.Extensions.Hosting` as a project, and no project gives it internals, so the compiler holds it to the public API.
- **One setup method.** `EchoBotServices.AddEchoBot(IServiceCollection)` declares the sessions `alice` and `bob`, the handlers and the console pairing, and returns the `WhatsmeowBuilder`.
  `Program.cs` calls it and adds only the OpenTelemetry exporter; the FullCycle calls it and adds `UseFakeWhatsApp()`, so what CI runs is the sample's own setup.
- **The check.** `Whatsmeow.IntegrationTests/Samples/EchoBotTests.cs`, one FullCycle, with a fake-mode sidecar of its own on a database of its own, as the hosting tests have: the host starting the sidecar is part of the sample.
  The fake is both phones: it links each session once the sample logged that its QR code is shown, then delivers texts; what was not echoed shows by order.
- **The sample's behaviour.**
  - It pairs, one at a time so the codes never mix, each session that is `Created` or `LoggedOut` when the host starts, with the QR code drawn on the console and never written as text to a log (it carries the device's secret), then connects it, so its events flow.
  - The echo skips the account's own texts and any text that starts with `echo: `, which breaks the loop between two sessions.
  - It sends with a new id: a repeat of an event, after the .NET process ended before its ack, is echoed again. An id derived from the message's would stop that (a send of an id already sent is never sent again, ADR 0025), but only a test that kills the .NET side could show it, too much for a sample's check; an application that must not repeat itself dedupes on the session and message id, as `IWhatsAppEventHandler` says.
  - It logs each echo with the message's WhatsApp timestamp, each state change, and each history batch, which is what #42's live run measures with.
- **Dependencies of the sample only.** QRCoder (MIT), which the live tests already use, and `OpenTelemetry.Extensions.Hosting` with `OpenTelemetry.Exporter.OpenTelemetryProtocol` (Apache-2.0, their whole graph), pinned centrally.
  `build/ci.sh` keeps QRCoder out of every shipped project, and no library package references OpenTelemetry.
  `Whatsmeow.IntegrationTests` gets OpenTelemetry in its lock file through the sample's reference, a test project, not a package.

## Consequences

- A sample setup change that breaks pairing or the echo fails CI; `Program.cs` itself, the exporter's three lines, is only built.
- The sample's spans and metrics are seen only in a run against real WhatsApp, #42's live run; the library's own are covered by the Telemetry tests (ADR 0037).
- The receipt-to-handler time the live run records is the handler's time minus the message's WhatsApp timestamp, to the second: no public member says when the sidecar received a message [observed: `MessageReceived`, `events.proto`]. A finer measure needs a contract field, an issue of its own.
- A later sample copies this shape: its directory under `dotnet/samples/`, one setup method, its FullCycle under `Samples/`.
