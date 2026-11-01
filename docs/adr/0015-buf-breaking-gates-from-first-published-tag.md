# 0015. `whatsmeow.v1` is frozen and `buf breaking` gates from the first published tag

- Status: Accepted
- Date: 2026-10-31
- Evidence: none from §14; recorded in issue #1 and PR #13

## Context

[ARCHITECTURE §13](../ARCHITECTURE.md#13-invariants) invariant 5 said `whatsmeow.v1` never breaks without a `protocol` increment and that `buf breaking` enforces it, without saying when enforcement starts.
Nothing is published: there is no `v*` tag [observed: `git ls-remote --tags origin`, 2026-10-31] and no `Whatsmeow.Client` on nuget.org [observed: `https://api.nuget.org/v3-flatcontainer/whatsmeow.client/index.json` returns 404, 2026-10-31].
The owner's stated rationale is that, with nothing published, no consumer can hold the contract and a break costs nothing until the first published tag ([issue #1 comment](https://github.com/tcortega/Whatsmeow.NET/issues/1#issuecomment-6551195963), 2026-10-31).
Phase 1 builds the first slices, and the contract is expected to change while they take shape [inferred].

## Decision

- From the first published `v*` tag (pre-releases included) on, `whatsmeow.v1` never breaks: a break means a new package `whatsmeow.v2` with its own ADR, every additive change increments `protocol`, and `buf breaking` gates `whatsmeow.v1` against `main`.
- Before that tag, `protocol` keeps its initial value, 1, and `buf breaking` only reports.
- Upstream whatsmeow protos stay excluded from `buf breaking` either way; their wire changes are reported and never block.
- The gate timing was chosen by the owner on 2026-10-31 ([issue #1 comment](https://github.com/tcortega/Whatsmeow.NET/issues/1#issuecomment-6551195963)), and it is reversible.
- The `whatsmeow.v2` rule for breaks after the tag was decided by the orchestrator in the PR #13 review on 2026-10-31, and it is reversible.
- Amended 2026-11-01 in the PR #15 review (orchestrator decision): any published `v<semver>` tag starts the gate, pre-releases included, because a published release candidate can already be depended on [inferred: a consumer can pin a pre-release package]. CI detects it as a tag reachable from the comparison base that matches `^v[0-9]+\.[0-9]+\.[0-9]+(-[0-9A-Za-z.-]+)?(\+[0-9A-Za-z.-]+)?$` ([guidelines/proto.md](../guidelines/proto.md)).

## Consequences

- [ARCHITECTURE](../ARCHITECTURE.md) §6, §11, §12 and §13 invariant 5, [guidelines/proto.md](../guidelines/proto.md) and [AGENTS.md](../../AGENTS.md) state the same rule.
- Breaking proto changes before the first published tag need no `protocol` increment, but the CI report still shows them.
- The first published tag is a contract commitment: from then on, `whatsmeow.v1` only grows, and each addition increments `protocol`.
- How a `whatsmeow.v2` coexists with v1 is decided in its own ADR.
- Reversing either part of this record takes a new ADR.
