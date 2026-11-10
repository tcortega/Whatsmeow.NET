# 0064. A rehearsal at a candidate version

* Status: Accepted
* Date: 2026-11-10
* Evidence: #52's local feed criterion, the plan for its exact-candidate matrix, `release.yml` and `packaging.yml` at `c280bb1`, `build/release-test.sh` (red, then green) and its mutations (PR body), GitHub's contexts and expressions reference, read on 2026-11-10
* Amends: [ADR 0050](0050-release-workflow.md) (the rehearsal's tag), ARCHITECTURE §12; notes [ADR 0055](0055-public-api-baseline-and-package-validation.md)

## Context

#52 asks for the local feed matrix, every target RID, JIT and AOT, at the release candidate itself.
The owner selected `1.0.0-rc.1` [observed: #52, 2026-11-08].
Every run so far built another version: the rehearsals `0.0.0-rehearsal.<run>` and main's untagged `0.0.0-alpha.0.<height>` [observed: #52's evidence table; runs 42497149254, 42508894464].
Only a pushed `v1.0.0-rc.1` would build that version on the package host, and pushing it starts the release run, freezes `whatsmeow.v1` and starts `buf breaking` gating (ADR 0015).

Measured at `c280bb1`:

* `release.yml`'s `workflow_dispatch` takes no input; the rehearsal's tag `v0.0.0-rehearsal.<run>` is written twice, in the workflow's `TAG` and in the `packaging` call [observed: `release.yml:34`, `:65`].
* The guard validates whatever tag it gets: strict SemVer without build metadata, at HEAD, HEAD on main, and not elsewhere on origin [observed: `build/release.sh` `guard`, `build/release-test.sh`].
* Publishing refuses any run that is not a push of that exact tag, at any version, a `workflow_dispatch` on a tag ref included [observed: `build/release.sh` `require_tag_push`, `build/release-test.sh`].
* `verify` fails unless the feed's `version` file is the tag's version; the install and aot jobs read the version from the feed, not from a tag [observed: `build/release.sh` `verify`, `build/ci.sh` `package_origin`, `aot_setup`].
* Workflow-level `env` may read `inputs`, and a reusable workflow call's `with` may read `needs`; expressions support `( )` grouping [observed: docs.github.com, Actions contexts "Context availability" and expressions "Operators", 2026-11-10].

## Decision

* `release.yml`'s `workflow_dispatch` takes an optional `version`, `<major>.<minor>.<patch>[-<pre-release>]` without `v`.
  Empty rehearses at `v0.0.0-rehearsal.<run>`, as before; given, the runner-local tag is `v<version>`.
  The workflow's `TAG` is the one place the tag is chosen: the pushed tag, else the candidate's, else the rehearsal's.
* The guard job exports the tag it guarded (`outputs.tag`, set by a step after `build/release.sh guard`), and `packaging` gets its `rehearsal-tag` from that output, so only a guarded tag is built.
* Nothing else changes: the same guard, packaging without caches, install, aot, verify, dry-run publishing and refusal checks; `publish` and `nuget-install` still run only on a push, and the rehearsal has no environment and no `id-token`.
  The input reaches a shell only as the `TAG` environment value, never in `run:` text.
* `build/release-test.sh` pins this wiring line by line: the input, the `TAG` expression, the guard's output after the guard step, packaging's `rehearsal-tag`, one rehearsal tag literal, and publishing only on a push.

## Consequences

* A run with `-f version=1.0.0-rc.1` on main builds the eight packages, the binaries and the source archive at `1.0.0-rc.1` through MinVer's tag path on the package host, installs and AOT-publishes them on every target RID, and verifies the exact asset names and checksums, with no tag on origin and nothing published.
* A candidate rehearsal is held to what its tag will be (ADR 0055): at a pre-release such as `1.0.0-rc.1`, `release_api_shipped` is skipped, as it will be in that tag's run, and the baseline is the one its tag computes (none, for `1.0.0-rc.1`).
  The default rehearsal is still held to the next stable release.
  So a candidate run does not prove a stable PublicAPI baseline; that is checked separately (`PublicAPI.Unshipped.txt` files, or a default rehearsal at the same commit).
* A typed `v` (`vv1...`), build metadata, or a tag origin has elsewhere fails in the guard job, before anything builds [observed: `build/release-test.sh`]; so does a version whose tag the clone already has, since `git tag` refuses an existing name [observed: local git, "fatal: tag ... already exists", exit 128].
* The test proves the text GitHub reads, not GitHub's evaluation of it; the first candidate dispatch on main is that proof [unknown until then].
  A wrong push-path value fails closed: the tag run's guard refuses a tag that is not SemVer, at HEAD, on main and on origin.
* The artifact of a candidate rehearsal is that run's only; a release downloads only its own run's artifact (`download-artifact` without `run-id`), so the candidate's evidence is linked to a later release by the binaries' checksums, which are reproducible from the commit (ADR 0023) [inferred].
