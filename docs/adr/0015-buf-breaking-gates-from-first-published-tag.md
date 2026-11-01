# 0015. `buf breaking` gates `whatsmeow.v1` only from the first published tag

- Status: Accepted
- Date: 2026-10-31
- Evidence: none from §14; recorded in issue #1

## Context

[ARCHITECTURE §13](../ARCHITECTURE.md#13-invariants) invariant 5 said `whatsmeow.v1` never breaks without a `protocol` increment and that `buf breaking` enforces it, without saying when enforcement starts.
No `vX.Y.Z` tag has been published [observed: `git ls-remote --tags origin`, 2026-10-31, no tags].
Nothing is published, so no consumer can hold the contract, and a break costs nothing yet [observed: owner decision, 2026-10-31, recorded in issue #1].
Phase 1 builds the first slices, and the contract is expected to change while they take shape [inferred].

## Decision

- Until the first published `vX.Y.Z` tag, `buf breaking` runs in CI against `main` and reports, but does not gate.
- `protocol` stays 1 until that tag, and no increment is required before then.
- From the first published tag on, `buf breaking` gates `whatsmeow.v1` against `main`, as [ARCHITECTURE §11](../ARCHITECTURE.md#11-testing-strategy) says, and `protocol` increments on every change to `whatsmeow.v1` (ARCHITECTURE §12), additive or breaking.
- Upstream whatsmeow protos stay excluded from `buf breaking` either way; their wire changes are reported and never block.
- The decision is the orchestrator's, and it is reversible.

## Consequences

- Invariant 5 now says when the gate starts, and §6, §11, §12, [AGENTS.md](../../AGENTS.md) and [guidelines/proto.md](../guidelines/proto.md) say the same.
- Breaking proto changes before the first published tag need no `protocol` bump, but the CI report still shows them.
- The first published tag is a contract commitment: from then on, every change needs a `protocol` increment, and the gate blocks breaks.
- Reversing this means turning the gate on earlier, by a new ADR.
