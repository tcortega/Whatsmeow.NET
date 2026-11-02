# 0048. The parity report: coverage read from the sidecar's call graph, exclusions with their reasons

- Status: Accepted
- Date: 2026-11-02
- Evidence: issue #36 and its plan review; `tools/parity-report` tests; the report at whatsmeow `56e5ad0581ce`; a prototype that compared CHA and VTA call graphs over the sidecar
- Amends: ARCHITECTURE §4 (`tools/parity-report`, Phase 2, not `check-parity` in Phase 4), §11 and §12

## Context

Phase 2 ends when the parity report covers messaging, groups and contacts (§15), and a whatsmeow bump has to show the `Client` methods it added that nothing covers.
What there is to work with:

- whatsmeow's `Client` has 136 exported methods at the pin; the one name declared twice, `DangerousInternals`, has its second copy in `internals_generate.go`, which is `//go:build ignore` and package main, and no other root file has a build constraint [observed: whatsmeow `56e5ad0581ce`].
- `tools/bump-report` already lists them from the source, ignoring tests, other packages and the generator (ADR 0043).
- Each slice reaches whatsmeow through a small interface that `*whatsmeow.Client` satisfies, sometimes through a helper, a closure the send queue runs later (`messaging`), or a worker no RPC starts (`history`), and the event handler runs inside whatsmeow [observed: `sidecar/internal/*`].
- A hand-kept "method: RPC" list would be right only until the code moved; nothing would check it.
- A call graph of the sidecar's release build from every RPC finds the calls [observed: the prototype]:
  - Following every edge credits every closure of a signature to every caller of a function value: all eight `MessageService` RPCs reached `BuildEdit`, through the queue's shared send, with both CHA and VTA.
  - Following static and interface calls only, and walking into each closure a reached function creates, names each builder by its own RPC, and CHA and VTA give the same result: 38 methods reached from RPCs, 5 only from the event handler or the history worker.
  - Loading and analysing the sidecar takes about 3 seconds.

## Decision

- **`tools/parity-report`** loads the sidecar's packages without build tags, the release build, as SSA, and builds a CHA call graph.
  Its roots are the methods that implement an RPC of a generated `whatsmeow.v1` `*Server` interface, named by the generated `<Service>_<Method>_FullMethodName`; one promoted from the embedded `Unimplemented*Server` is not a root.
  From each, it follows static and interface calls and the closures a function creates, within the sidecar module, up to the first call of a `*whatsmeow.Client` method.
  A call through a function value is not followed: the RPC that made the closure is credited instead.
- **Each method is covered, excluded or missing.**
  Covered names the RPCs that reach it, or, when none does, the sidecar function that calls it, such as `history.(*Service).fetch`.
  Excluded gives the reason `sidecar/parity.excluded` records, one `Name: reason` per line, reviewed like `licenses.allow`.
  Missing is neither, so a method a bump adds is missing without an edit anywhere.
  An exclusion of a method that is covered, or that whatsmeow no longer has, is listed for removal.
- **The list of methods is `tools/internal/whatsmeowapi`**, which `bump-report` uses too, so the two tools never disagree on what a `Client` method is.
- **A reason is a decision**: deprecated upstream, a §1 non-goal, out of a slice's scope by its ADR, done by whatsmeow itself under the §7.2 configuration, or a follow-up issue for a method in scope that is not built yet (#104, #105, #106).
  At the end of Phase 2, no method is missing.
- **CI shows it and never fails on it** (#36's non-goal): `build/ci.sh go` prints it to the log and the job summary, adds a warning when a method is missing, and adds a warning instead of failing when the tool cannot run.
  A malformed `parity.excluded` fails the tool's own unit tests, which gate like every tool's.
  A bump PR's CI run shows the report, so its new methods appear there as missing.

## Consequences

- A method reached only through a method value or a function stored in a field the walk does not follow shows as missing, the safe direction; it is then covered by a closure or excluded with its reason.
- CHA over-approximates interface calls inside the sidecar: an interface with two sidecar implementations credits an RPC with both.
  No interface the RPCs call has a second sidecar implementation in the release build, where the fake WhatsApp is not linked [inferred: from the slices' interfaces; the report would show an unexpected RPC].
- A method covered by an event path names a Go function, not an RPC: the .NET side receives its result through `EventService/StreamEvents`.
- Covered means called, not complete: a method the sidecar calls with fewer options than whatsmeow takes is still covered.
