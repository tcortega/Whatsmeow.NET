# 0043. The weekly whatsmeow bump: one PR from GITHUB_TOKEN, extended and never rewritten, that lists the facts to re-check

- Status: Accepted
- Date: 2026-11-02
- Evidence: issue #48 and its plan review; `tools/bump-report` tests; `build/bump.sh --dry-run` against whatsmeow `54038813c47f`, against a local bare remote with a stand-in `gh`, and against a stand-in whatsmeow whose go line is 1.99; GitHub's docs and this repository's settings, read as cited below
- Amends: ARCHITECTURE §12 (the weekly `whatsmeow-bump` workflow)

## Context

whatsmeow has no tags, changes weekly, and a client that falls behind is refused with a 405 (RESEARCH "The October 2026 baseline").
Every whatsmeow fact the design rests on holds at the pin only (ADR 0013, ARCHITECTURE §14).
What the bump has to work with:

- Events from `GITHUB_TOKEN` start no workflow, except `workflow_dispatch`, `repository_dispatch`, and a `pull_request` opened, synchronized or reopened, whose runs wait for someone with write access to approve them [observed: docs.github.com, "When `GITHUB_TOKEN` triggers workflow runs", 2026-11-02].
- `GITHUB_TOKEN` can open a PR only with the setting "Allow GitHub Actions to create and approve pull requests", which is off here [observed: `repos/tcortega/Whatsmeow.NET/actions/permissions/workflow`, `can_approve_pull_request_reviews: false`].
- Branch protection requires the `ci` check of GitHub Actions and no review [observed: `branches/main/protection`].
- `go get` of a version whose go line is above the local Go fails with "requires go >= ..." and leaves go.mod as it was; `go mod download -json` still prints its version, sum and zip, beside that error, and no directory [observed: Go 1.27.3, a local proxy serving a module with `go 1.99`].
- buf's WIRE rules run on two copies of the protos outside the workspace with an inline v1 config; a v2 inline config fails there [observed: buf 1.75.0].
- ARCHITECTURE cites whatsmeow files as `message.go:459-462`, a bare name for a root file, a bare name for a file deeper in (`lidmap.go`), or a path; it cites mautrix, grpc-go, libsignal, zerolog and Go files the same way [observed: 63 names in §14, about 30 more lines elsewhere].
- No §14 fact can be re-checked whole by a machine. A bump re-checks some mechanically: the build, for API that went away; `sync-protos` and the regenerated code; and the tests that run whatsmeow's own code against what we expect (its builders, media crypto and unwrapping, its error texts and log lines) [observed: test names and imports in `sidecar/`]. Acks, dispatch order, retries and every "nowhere" claim live in code that only a live socket runs, and only a person can re-read them.

## Decision

- **A weekly workflow (Mondays 06:00 UTC) and a manual run call `build/bump.sh`**, which pins whatsmeow's latest `main`, writes `VERSION`, runs `go get` and `go mod tidy`, `sync-protos -write`, `buf generate --template buf.gen.whatsapp.yaml` and `check-licenses -write`, and opens the PR.
  It never builds or tests: the PR's `ci` run does, so a bump that breaks the build still opens, red, for a person to fix in it.
  A step that fails is named in the body and the rest still run; when `go get` refuses the Go floor, the require is written with `go mod edit`, so the PR pins the version and CI fails on it.
  `--dry-run` does it all in the working tree and prints the body.
- **`GITHUB_TOKEN` opens the PR, and a person approves its CI runs.** The owner turns the setting above on; nothing else is needed: no secret, no App.
  Each push of the bump waits for one "Approve workflows to run", which costs nothing next to the review the PR needs anyway.
  A GitHub App installation token would start the runs without the click, for an App and two secrets to keep.
- **One PR, on the branch `whatsmeow-bump`.** No upstream change since main's pin opens nothing.
  When the branch exists, the bump adds a commit on top and pushes it, never a force push, because a person may have pushed adapter fixes there; then it edits the open PR's title and body, or opens one.
- **The body says what a reviewer re-checks** (`tools/bump-report`):
  - whatsmeow's go line, loudly when it is above the pinned toolchain;
  - each line of ARCHITECTURE.md, all of it and not only §14, that cites a whatsmeow file that changed, as unconfirmed at the new pin;
  - the exported `Client` methods added, removed or changed between main's pin and the new one (#36 adds what covers each);
  - the WIRE diff against main's protos, non-gating, the first 200 findings;
  - the licence findings, which keep CI red until a reviewer reads each licence (ADR 0016).
- **A citation names a whatsmeow file** when it is a file at the module root, else when the path of one or more files ends with it; any other path, such as mautrix's `pkg/connector/client.go`, names none.
  A file changed when its bytes differ between the two module zips. Over-flagging a fact costs a re-read; missing one costs a wrong design, so the match leans to flagging.
  ADRs are not scanned: they record what was true when they were decided.
- **The baseline is the commit §14 records** ("**The whatsmeow pin** is ..."), not main's pin.
  A reviewer moves it in the bump PR after re-checking the flagged facts; a bump merged without that keeps its facts listed by the next one.
  The workflow never edits a fact, never merges, and never publishes.

## Consequences

- The first run waits for the owner's setting; until then it fails at `gh pr create`.
- A fact that cites no file, or only a file that did not change, is not flagged; a whatsmeow change elsewhere that breaks it is found by CI, by a test, or not at all. The PR asks the reviewer to read the commit range too.
- Line numbers in the citations drift with every bump; a re-check that confirms a fact updates its lines.
- The Go floor is reported, not handled: moving the toolchain is a person's change, in `mise.toml` and both go.mod files.
