# 0050. The release workflow: a guarded tag on main, the GitHub release first, then nuget.org in two phases through Trusted Publishing

- Status: Accepted
- Date: 2026-11-02
- Evidence: issue #46, its plan review, `build/release-test.sh` and its 31 mutations (PR body), `build/release.sh verify` on a feed `dotnet pack` wrote at a local tag, and the docs and settings cited below, read on 2026-11-02
- Amends: ARCHITECTURE §11 (where the packaging jobs live) and §12 (the release)

## Context

One `v<semver>` tag has to build the six binaries and the eight packages at its version, run the packaging checks (#43), and publish, with no long-lived NuGet API key (§12).
Nothing a release does can be taken back: packages stay on nuget.org, and the first published tag freezes `whatsmeow.v1` (ADR 0015).

Measured:

- nuget.org Trusted Publishing trades a GitHub OIDC token for an API key that is valid for 1 hour; one token buys one key.
  A policy names the repository owner, the repository, the workflow file name and, optionally, a GitHub environment, case-insensitive; its scopes allow publishing new packages and/or new versions, for a package glob, and it belongs to a user or an organization.
  A policy can start "temporarily active" for 7 days, "usually" for a private repository, and becomes permanent after a publish [observed: learn.microsoft.com/nuget/nuget-org/trusted-publishing, updated 2026-09-01].
  Whether a policy can also restrict the ref or tag is [unknown]: the docs list no such field for GitHub.
- `NuGet/login` v1.2.0 is commit `8d196754b4036150537f80ac539e15c2f1028841`, which `v1` also points to; its one required input, `user`, is the nuget.org profile name, not the e-mail address, and it outputs `NUGET_API_KEY`.
  The job needs `id-token: write`, which a job can set for itself [observed: `NuGet/login` `action.yml`, README and tags; docs.github.com OIDC reference].
- `dotnet nuget push --skip-duplicate` "treats any 409 Conflict response as a warning so that other pushes can continue" [observed: learn.microsoft.com dotnet-nuget-push].
- `gh release view <tag>` finds a draft as well as a published release, and says `release not found` when there is neither [observed: cli/cli `pkg/cmd/release/shared/fetch.go`, `FetchRelease` and `ErrReleaseNotFound`].
- An immutable release locks its assets and tag once published, and GitHub recommends a draft, then the assets, then publishing; it applies to future releases only [observed: docs.github.com "Immutable releases" and "Preventing changes to your releases"].
  It is off here [observed: `repos/tcortega/Whatsmeow.NET/immutable-releases`].
- Environments with required reviewers work on public repositories of free plans; "prevent self-reviews" stops the user who started the run from approving it; deployments can be restricted to tag patterns [observed: docs.github.com "Deployments and environments"].
  The repository is public and has no environment, no ruleset and no release [observed: `gh api`, 2026-11-02].
- No package id of ours is on nuget.org, and no `Whatsmeow.*` package of anyone else is [observed: flat container 404 for each id; nuget.org search for "whatsmeow"].
- The packed nuspecs carry each dependency of ours as `<dependency id="Whatsmeow.X" version="V"` with `V` the release version, exact (`[V]`) or not [observed: `dotnet pack` at a local tag `v0.0.0-rehearsal.1`].
- A workflow_dispatch run can only start a workflow that is on the default branch [inferred: GitHub docs on `workflow_dispatch`; not measured here].

## Decision

- **`release.yml` runs on a pushed `v*` tag**, in four jobs: `guard`, `packaging` (`packaging.yml`, the package and install jobs that `ci.yml` now calls too), `publish`, and, on `workflow_dispatch` only, `rehearsal`. Every step lives in `build/release.sh`, which `build/release-test.sh` tests in the `contract` job.
- **The guard** fails before anything is built unless the tag is `v<major>.<minor>.<patch>[-<pre-release>]`, points at HEAD, and HEAD is an ancestor of main, fetched there and then.
  Build metadata is refused: NuGet ignores it, so two such tags would be one package version.
  In a tag push it also fails while `NUGET_USER` in `release.yml` is still the placeholder.
- **Verify** runs in the publish job on the artifact it pushes: the feed holds exactly the eight packages at the tag's version, the `version` file and the source archive; each nuspec's id and version are its file's; every dependency of ours is at the release version and comes earlier in the push order, and none is in the first phase.
  It writes the release assets: the six binaries taken from the native packages, so they are the packaged ones, named `whatsmeow-net_<version>_<rid>[.exe]`; `whatsmeow-net_<version>_checksums.txt` with their sha256 in `sha256sum` format; and the source archive.
  The binaries are reproducible (ADR 0023), so a re-run's checksums are the same; the archive is not (ADR 0029), so it is not in the checksums.
- **The publish job waits for approval in the `release` environment**, and has `contents: write` and `id-token: write`; no other job has either.
- **The GitHub release goes first**, so the Corresponding Source every native package points to is up before any package (ADR 0029): a draft, then every asset it lacks, then published; a pre-release tag makes a pre-release.
  It never replaces or deletes an asset.
- **Then nuget.org, in two phases**, one package at a time with `--skip-duplicate`, stopping at the first failure:
  1. `Whatsmeow.Sidecar.Native.Linux`, `.MacOS`, `.Windows` and `Whatsmeow.Protocol.WhatsApp`, which depend on no package of ours; then the job waits, up to 45 minutes, until the flat container lists all four at the version.
  2. With a new key, since the wait can outlast the first: `Whatsmeow.Protocol`, `Whatsmeow.Client`, `Whatsmeow.Extensions.Hosting`, `Whatsmeow.Testing`, not waited for, because a package nuget.org has not indexed yet is not a failure.

  So nuget.org never lists a `Whatsmeow.Client` whose native packages are missing.
  Within phase 2 nuget.org may make the packages visible in another order for a few minutes [inferred: it validates each package on its own]; the owner accepted that in the plan review over a wait per package, which could outlast a key.
- **A re-run pushes and uploads only what is missing**, and nothing deletes or unlists anything (`tools/check-repo`, ADR 0029).
- **The rehearsal**, a manual run of `release.yml` on main, makes a tag `v0.0.0-rehearsal.<run>` in each runner only (the checkout keeps no credentials, so it cannot push it), and runs the guard, packaging and verify at that version, prints every publishing command instead of running it, and checks that `build/release.sh` refuses to publish.
  It has no environment and no `id-token` permission, and `build/release.sh` refuses `github` and `push` unless the event is a push of that very tag.
- `packaging.yml` checks out full history, which MinVer needs for the height (ADR 0023), and keeps the feed artifact 30 days, so a publish that waited for approval, or its re-run, still finds it.

## Owner actions

For Antonio; the workflow assumes none of them is done, and a tag push fails in the guard until the first one is:

1. Create or sign in to the nuget.org account that will own the packages, and give the exact profile name (not the e-mail address); it replaces `REPLACE-WITH-NUGET-PROFILE-NAME` in `release.yml`.
2. On nuget.org, username menu, Trusted Publishing, add a policy: owner his user; Repository Owner `tcortega`; Repository `Whatsmeow.NET`; Workflow File `release.yml`; Environment `release`; scopes for new packages and new versions, glob `Whatsmeow.*` (the exact form labels are [unknown]).
   If it shows as temporarily active, the first publish must land within 7 days, or he restarts the window.
3. In GitHub, Settings, Environments, a new environment `release`: required reviewer `tcortega`; "prevent self-reviews" off, or he cannot approve the run his own tag push starts; deployment branches and tags, selected, the tag pattern `v*`; no secrets; untick the administrators' bypass if offered [inferred: that the option exists].
4. Settings, Rules, Rulesets, a new tag ruleset on `v*`, active, no bypass: Restrict deletions, Restrict updates, Block force pushes; optionally Restrict creations with himself as the bypass.
5. Settings, Releases, "Enable release immutability" (off today; it applies to future releases only).
6. Decide on the `Whatsmeow.*` prefix reservation (an e-mail to account@nuget.org with the owner's display name and the prefix). The first criterion is that the prefix identifies the owner, and whatsmeow is upstream's name, so a refusal is plausible [inferred]; the recommendation is to decide after #52, and to ask for a public prefix if at all.

## Consequences

- The first tag (#52) is the first run of `github` and `push` against GitHub and nuget.org; until then they are proven by their dry runs, their tests and the rehearsal, not by a publish.
- A rehearsal of a commit that already has a release tag builds at that tag's version, so its verify fails; rehearse a commit after it.
- The release runs the packaging checks only; that main's own `ci` run is green is the owner's check when he tags.
- `release.yml` is the file the Trusted Publishing policy names: renaming it, or moving the push into another workflow, needs a new policy.
- Adding a package or a release target means editing `phase1`/`phase2` or `binaries` in `build/release.sh`; verify fails on the feed until then.
