# 0005. Feature-slice organization across proto, Go and .NET

- Status: Accepted
- Date: 2026-10-31
- Evidence: none from §14; a structural decision

## Context

Every WhatsApp capability (sessions, pairing, messaging, media, contacts, groups, presence, history) crosses three layers: the `whatsmeow.v1` proto, the Go sidecar, and the .NET library.
Organizing each layer by technical kind (all protos, all services, all mappers) spreads one capability over many places and invites drift between them.
Proto field-name drift between layers is one of the recurring bug classes in the prior art [observed: [RESEARCH.md](../RESEARCH.md#six-earlier-wrappers-already-made-the-mistakes-worth-avoiding) "Six earlier wrappers"].
The house rules use vertical slices, but this is a library, not a VSA API, so the house VSA tooling does not apply ([AGENTS.md](../../AGENTS.md)).

## Decision

- A slice is a WhatsApp capability, and it is cut the same way through all three layers: `proto/whatsmeow/v1/<slice>.proto`, `sidecar/internal/<slice>/`, `dotnet/src/Whatsmeow.Client/<Slice>/` ([ARCHITECTURE §3](../ARCHITECTURE.md#3-code-organization-feature-slices-end-to-end)).
- A slice owns its proto messages, Go service implementation, event mapping, .NET public types, proto-to-record mapping and tests.
- Adding a capability means adding or extending one slice in all three layers.
- Slices never call each other's internals.
- Shared needs go into a deliberately small core: Go `internal/{server,transport,outbox,store,log}`, .NET `Core/`.
- Slices may depend on core; core never depends on a slice.

## Consequences

- The public entry points mirror the slices: `client.Pairing`, `client.Messages`, `client.Media`, `client.Contacts`, `client.Groups`, `client.Presence`.
- The first complete Phase 1 slice becomes the gold standard named in [AGENTS.md](../../AGENTS.md); later slices copy it, and no second pattern is invented.
- Each slice gets one integration FullCycle ([ADR 0011](0011-integration-tests-by-default.md)).
- A capability that seems to need two slices is a sign that something belongs in core, or that the slice boundary is wrong.
