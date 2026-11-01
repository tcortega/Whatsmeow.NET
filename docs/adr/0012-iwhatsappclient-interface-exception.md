# 0012. `IWhatsAppClient` as the single exception to "no interface just to mock it"

- Status: Accepted
- Date: 2026-10-31
- Evidence: none from §14; an API design decision
- Amended by: [ADR 0021](0021-pairing.md) (pairing is on the client; the feature groups are decided with Messages)

## Context

The house rule is: do not add an interface just to mock it.
Consumers of this library build bots, and they need to test their own handlers without a sidecar or a WhatsApp account.
Inside the library, tests run against a real sidecar ([ADR 0011](0011-integration-tests-by-default.md)), so internals need no test seams.

## Decision

- `IWhatsAppClient` is the one public interface that exists for substitution.
- `Whatsmeow.Testing` ships `FakeWhatsAppClient` (with `Raise(event)`) to consumers; that fake is a product, not our test seam.
- One `IWhatsAppClient` exists per session, exposing the feature groups `Pairing`, `Messages`, `Media`, `Contacts`, `Groups` and `Presence` ([ARCHITECTURE §8.2](../ARCHITECTURE.md#82-public-api-shape-indicative)).
- Inside the library the house rule holds: no `ISupervisor`, `IDispatcher`, `ITransport` or similar just so a test can swap it.

See [guidelines/csharp.md](../guidelines/csharp.md#the-one-interface-exception).

## Consequences

- Adding a member to `IWhatsAppClient` or its feature groups also changes `FakeWhatsAppClient`, and is a public API change.
- Core behavior lives on real members; C# 14 extension blocks are convenience only, never logic that must be mockable.
- [AGENTS.md](../../AGENTS.md) records this as one of the two house-rule exceptions.
