# 0048. The parity report: coverage read from the sidecar's call graph, exclusions with their reasons

- Status: Accepted
- Date: 2026-11-02
- Evidence: issue #36, its plan review and the PR 107 review; `tools/parity-report` tests and their mutations; the report at whatsmeow `56e5ad0581ce`; a prototype that compared CHA and VTA call graphs over the sidecar
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
  - An RTA graph from `main` and the package inits finds the same 43 methods [observed: the PR 107 review].
  - CHA (`golang.org/x/tools/go/callgraph/cha`) gives no edges out of a method of an unexported type it does not see as a runtime type: it takes its functions from `ssautil.AllFunctions`, which visits methods of exported types only [observed: x/tools v0.52.0, `ssautil/visit.go`; a test's unexported server type had no edges].
  - The fake WhatsApp (`internal/fakewhatsapp`, behind `fakewhatsapp`) calls `ParseWebMessage` for real (fakewhatsapp/history.go:104), and tests call whatsmeow too: neither is the release build [observed: source].
  - Loading and analysing the sidecar takes about 3 seconds.

## Decision

- **`tools/parity-report`** loads the sidecar's packages as the release build links them, without build tags and without tests, as SSA.
  It walks from each method that implements an RPC of a generated `whatsmeow.v1` interface, named `whatsmeow.v1.<Service>/<Method>`, then from each `main` function, for what no RPC reaches; code `main` never reaches, such as a package nothing links, credits nothing.
  From a reached function of the sidecar module it follows each function it calls statically or takes as a value (a closure it makes, a method value it hands on, since what it hands over is called later), and each interface call to every type the sidecar declares that implements the interface, as CHA would, with no CHA: it stops at whatsmeow's code.
  A `*whatsmeow.Client` method it calls, takes as a value, or reaches through an interface the `Client` implements, is covered.
  A call through a function value is not followed: it would reach every function of that signature, crediting every closure to every RPC; the function that took the closure is credited instead.
- **Each method is covered, excluded or missing.**
  Covered names the RPCs that reach it, or, when none does, the sidecar function that calls it, such as `history.(*Service).fetch`.
  Excluded gives the reason `sidecar/parity.excluded` records, one `Name: reason` per line, reviewed like `licenses.allow`.
  Missing is neither, so a method a bump adds is missing without an edit anywhere.
  An exclusion of a method that is covered, or that whatsmeow no longer has, is listed for removal.
- **The list of methods is `tools/internal/whatsmeowapi`**, which `bump-report` uses too, so the two tools never disagree on what a `Client` method is.
- **A reason is a decision**: deprecated upstream, a §1 non-goal, out of a slice's scope by its ADR, done by whatsmeow itself or by another method the sidecar calls, a follow-up issue for a method in scope that is not built yet (#104, #105, #106), or "out of v1 scope (#36)", a scope decision made with the report where no earlier document made one.
  At the end of Phase 2, no method is missing.
- **CI shows it and never fails on it** (#36's non-goal): `build/ci.sh go` prints it to the log and the job summary, adds a warning when a method is missing, and adds a warning instead of failing when the tool cannot run.
  A malformed `parity.excluded` fails the tool's own unit tests, which gate like every tool's.
  A bump PR's CI run shows the report, so its new methods appear there as missing.

## Consequences

- A method reached only through a function stored in a struct field or a global, never taken as a value by reached code, shows as missing, the safe direction; it is then covered or excluded with its reason.
- Taking a function as a value counts as reaching it, as a closure made but never called would; over-crediting there needs code that makes a closure for nothing.
- An interface call reaches every sidecar type that implements the interface: an interface with two sidecar implementations credits an RPC with both.
  No interface the RPCs call has a second sidecar implementation in the release build, where the fake WhatsApp is not linked [inferred: from the slices' interfaces; the report would show an unexpected RPC].
- A method covered by an event path names a Go function, not an RPC: the .NET side receives its result through `EventService/StreamEvents`.
- Covered means called, not complete: a method the sidecar calls with fewer options than whatsmeow takes is still covered.
