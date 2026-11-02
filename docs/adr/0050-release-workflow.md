# 0050. The release workflow: a guarded tag on main, a protected environment, the GitHub release before the packages, then nuget.org in two phases through Trusted Publishing

- Status: Accepted
- Date: 2026-11-02
- Evidence: issue #46, its plan review and two PR reviews, `build/release-test.sh` and its mutations (PR body), `build/release.sh verify` on a feed `dotnet pack` wrote at a local tag, and the docs, sources and settings cited below, read on 2026-11-02
- Amends: ARCHITECTURE §11 (where the packaging jobs live) and §12 (the release)

## Context

One `v<semver>` tag has to build the six binaries and the eight packages at its version, run the packaging checks (#43), and publish, with no long-lived NuGet API key (§12).
Nothing a release does can be taken back: packages stay on nuget.org, and the first published tag freezes `whatsmeow.v1` (ADR 0015).

Measured:

- nuget.org Trusted Publishing trades a GitHub OIDC token for an API key that is valid for 1 hour; one token buys one key.
  A policy names the repository owner, the repository, the workflow file name and, optionally, a GitHub environment, case-insensitive; its scopes allow publishing new packages and/or new versions, for a package glob, and it belongs to a user or an organization.
  A policy can start "temporarily active" for 7 days, "usually" for a private repository, and becomes permanent after a publish [observed: learn.microsoft.com/nuget/nuget-org/trusted-publishing, updated 2026-09-01].
  Whether a policy can also restrict the ref or tag is [unknown]: the docs list no such field for GitHub.
- `NuGet/login` v1.2.0 is commit `8d196754b4036150537f80ac539e15c2f1028841`, which `v1` also points to; its one required input, `user`, goes to the token exchange as `username`, and a refused exchange says "Make sure you are using the username of the policy creator, not the policy owner"; it masks the key and outputs it as `NUGET_API_KEY` [observed: `NuGet/login` `action.yml` and `src/index.ts:6,61,77,101-102` at that commit].
  The job needs `id-token: write`, which a job can set for itself [observed: docs.github.com OIDC reference].
- `dotnet nuget push --skip-duplicate` "treats any 409 Conflict response as a warning so that other pushes can continue" [observed: learn.microsoft.com dotnet-nuget-push].
- `gh release view <tag>` finds a draft as well as a published release, and says `release not found` when there is neither [observed: cli/cli `pkg/cmd/release/shared/fetch.go`, `FetchRelease` and `ErrReleaseNotFound`].
  `--json assets` gives each asset's `state` (`uploaded` once its upload finished) and `digest` (`sha256:<hex>`) [observed: `gh release view -R cli/cli --json assets`, gh 2.97.0].
- An immutable release locks its assets and tag once published, and GitHub recommends a draft, then the assets, then publishing; it applies to future releases only [observed: docs.github.com "Immutable releases" and "Preventing changes to your releases"].
  It is off here [observed: `repos/tcortega/Whatsmeow.NET/immutable-releases`].
- "Running a workflow that references an environment that does not exist will create an environment with the referenced name", and it "will not have any protection rules or secrets configured"; the setup can "disallow bypassing configured protection rules" [observed: docs.github.com "Managing environments for deployment"].
  Environments with required reviewers work on public repositories of free plans; "prevent self-reviews" stops the user who started the run from approving it; deployments can be restricted to tag patterns [observed: docs.github.com "Deployments and environments"].
  "Anyone with read access to the repository" can read an environment through `GET repos/{owner}/{repo}/environments/{name}` [observed: docs.github.com REST "Deployment environments"]; on a public repository so can an anonymous request, reviewers included, and a missing one is a 404 [observed: `curl` without a token on `repos/cli/cli/environments/gh-acceptance-testing` and `repos/tcortega/Whatsmeow.NET/environments/release`, 2026-11-02].
  So the guard job's `GITHUB_TOKEN` with `contents: read` can read it [inferred: a token with read access can do what an anonymous request can; the first rehearsal shows it].
  Whether that token can read an environment's variables is [unknown], so the guard does not try.
- The repository is public and has no environment, no ruleset and no release [observed: `gh api`, 2026-11-02].
- No package id of ours is on nuget.org, and no `Whatsmeow.*` package of anyone else is [observed: flat container 404 for each id; nuget.org search for "whatsmeow"].
- The packed nuspecs carry each dependency of ours as `<dependency id="Whatsmeow.X" version="V"` with `V` the release version, exact (`[V]`) or not [observed: `dotnet pack` at a local tag `v0.0.0-rehearsal.1`].
- `build/ci.sh`'s `buf_breaking` gates from any `v<semver>` tag main can reach, so even a tag whose release was refused freezes `whatsmeow.v1` until it is deleted (ADR 0015) [observed: `build/ci.sh` `buf_breaking`].
- A workflow_dispatch run can only start a workflow that is on the default branch [inferred: GitHub docs on `workflow_dispatch`; not measured here].

## Decision

- **`release.yml` runs on a pushed `v*` tag**, in four jobs: `guard`, `packaging` (`packaging.yml`, the package and install jobs that `ci.yml` now calls too), `publish`, and, on `workflow_dispatch` only, `rehearsal`.
  Every step lives in `build/release.sh`, which `build/release-test.sh` tests in the `contract` job with stand-ins for `gh`, `dotnet` and `curl`.
- **The guard** fails before anything is built unless:
  - the tag is `v` and a SemVer 2.0.0 version: no leading zero in a number or a numeric pre-release identifier, no empty identifier, and no build metadata, which NuGet ignores, so two such tags would be one package version;
  - it points at HEAD, and HEAD is an ancestor of main, fetched there and then;
  - the tag on origin, peeled, is HEAD too, so a tag moved or made again since the run began never publishes other content under its name (a rehearsal's tag is not on origin);
  - and, in a tag push, the `release` environment exists and has required reviewers that name someone, read through the API, because GitHub would otherwise make it unprotected.
- **Verify** runs in the publish job on the artifact it pushes: the feed holds exactly the eight packages at the tag's version, the `version` file and the source archive; each nuspec's id and version are its file's; every dependency of ours is at the release version and comes earlier in the push order, and none is in the first phase.
  It writes the release assets: the six binaries taken from the native packages, so they are the packaged ones, named `whatsmeow-net_<version>_<rid>[.exe]`; `whatsmeow-net_<version>_checksums.txt` with their sha256 in `sha256sum` format; and the source archive.
  The binaries are reproducible (ADR 0023), so a re-run's checksums are the same; the archive is not (ADR 0029), so it is not in the checksums.
- **The publish job waits for approval in the `release` environment**, and has `contents: write` and `id-token: write`; no other job has either.
  It reads `NUGET_USER` from the environment's variable `vars.NUGET_USER`, the username of the policy's creator; `build/release.sh` refuses to publish while it is unset or the placeholder, so a wrong name is fixed in the variable and a re-run, not with a new tag.
- **The order**, each step a no-op when a re-run finds it done:
  1. `draft`: the GitHub release as a draft, a pre-release for a pre-release tag, with every asset it lacks.
     An asset already there must be `uploaded`, and, except the source archive, hold the local file's digest; a different digest fails, saying the release holds different bytes.
     The source archive is never byte-identical from run to run, so only its state is checked and its first upload stays.
  2. The first `NuGet/login`, so a wrong `NUGET_USER` or policy stops the run while the release is still a draft.
  3. `publish`: the draft goes public, before any package, so the Corresponding Source every native package points to is up first (ADR 0029).
     It is Latest only when the tag is the highest stable version on origin, and never for a pre-release.
  4. `push 1`: `Whatsmeow.Sidecar.Native.Linux`, `.MacOS`, `.Windows` and `Whatsmeow.Protocol.WhatsApp`, which depend on no package of ours, one at a time with `--skip-duplicate`, stopping at the first failure.
  5. `wait 1`: up to 45 minutes until the flat container lists all four at the version, each request capped at 30 s; a timeout fails, and a re-run resumes.
  6. The second `NuGet/login`, since the wait can outlast the first key.
  7. `push 2`: `Whatsmeow.Protocol`, `Whatsmeow.Client`, `Whatsmeow.Extensions.Hosting`, `Whatsmeow.Testing`, the same way.
  8. `wait 2`: the same wait, but a timeout only warns, because a package nuget.org has not indexed yet is not a failure.

  So nuget.org never lists a `Whatsmeow.Client` whose native packages are missing.
  Within phase 2 nuget.org may make the packages visible in another order for a few minutes [inferred: it validates each package on its own]; the owner accepted that in the plan review over a wait per package, which could outlast a key.
  A timeout's message says that if nuget.org e-mailed a validation failure for a package, a re-run will not help; whether that version can be pushed again is [unknown].
- **Nothing deletes or unlists anything**: a re-run pushes and uploads only what is missing, and never replaces an asset (`tools/check-repo`, ADR 0029).
- **A mistaken tag**: refuse the approval, so nothing is published, then delete the tag, with the bypass owner action 4 gives, because it would freeze `whatsmeow.v1` otherwise (ADR 0015).
- **The rehearsal**, a manual run of `release.yml` on main, makes a tag `v0.0.0-rehearsal.<run>` in each runner only (the checkout keeps no credentials, so it cannot push it), runs the guard, packaging and verify at that version, reports whether the environment is protected, prints every publishing command instead of running it, and checks that `build/release.sh` refuses to publish.
  It has no environment and no `id-token` permission, and `build/release.sh` refuses `draft`, `publish` and `push` unless the event is a push, the ref type a tag, and the ref that very tag.
- `packaging.yml` checks out full history, which MinVer needs for the height (ADR 0023), and keeps the feed artifact 30 days, so a publish that waited for approval, or its re-run, still finds it.
  A release calls it with its caches off, so it never restores a cache another ref saved.

## Owner actions

For Antonio; the workflow assumes none of them is done, and a tag push fails until actions 1 and 3 are:

1. Create or sign in to the nuget.org account that will own the packages, and set its username (the username of whoever creates the policy in action 2, not the e-mail address) as the variable `NUGET_USER` on the `release` environment of action 3: Settings, Environments, `release`, Environment variables.
   Until then the publish job refuses to publish, after approval and before anything goes up.
2. On nuget.org, username menu, Trusted Publishing, add a policy: owner his user; Repository Owner `tcortega`; Repository `Whatsmeow.NET`; Workflow File `release.yml`; Environment `release`; scopes for new packages and new versions, glob `Whatsmeow.*` (the exact form labels are [unknown]).
   If it shows as temporarily active, the first publish must land within 7 days, or he restarts the window.
3. In GitHub, Settings, Environments, a new environment `release`: required reviewer `tcortega`; "prevent self-reviews" off, or he cannot approve the run his own tag push starts; deployment branches and tags, selected, the tag pattern `v*`; no secrets; disallow bypassing the protection rules.
   The guard fails a tag push until it has a required reviewer.
4. Settings, Rules, Rulesets, two tag rulesets on `v*`, both active, because a ruleset's bypass list covers all of its rules [inferred: GitHub's ruleset form]:
   - one with no bypass: Restrict updates and Block force pushes; optionally Restrict creations, with himself as its bypass;
   - one with himself as the bypass: Restrict deletions, so only he can delete a mistaken tag.
5. Settings, Releases, "Enable release immutability" (off today; it applies to future releases only).
6. Decide on the `Whatsmeow.*` prefix reservation (an e-mail to account@nuget.org with the owner's display name and the prefix).
   The first criterion is that the prefix identifies the owner, and whatsmeow is upstream's name, so a refusal is plausible [inferred]; the recommendation is to decide after #52, and to ask for a public prefix if at all.

## Consequences

- The first tag (#52) is the first run of `draft`, `publish` and `push` against GitHub and nuget.org; until then they are proven by their tests against stand-ins, their dry runs and the rehearsal, not by a publish.
- A rehearsal of a commit that already has a release tag builds at that tag's version, so its verify fails; rehearse a commit after it.
- The release runs the packaging checks only; that main's own `ci` run is green is the owner's check when he tags.
- `release.yml` is the file the Trusted Publishing policy names: renaming it, or moving the push into another workflow, needs a new policy.
- Adding a package or a release target means editing `phase1`/`phase2` or `binaries` in `build/release.sh`; verify fails on the feed until then.
- An asset whose upload never finished stops `draft`, which never deletes one: a person removes it on GitHub, then re-runs.
