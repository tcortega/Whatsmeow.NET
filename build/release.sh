#!/usr/bin/env bash
# The release (#46, ADR 0050); .github/workflows/release.yml calls this and nothing else, and build/ci.sh package
# calls its baseline.
#
#   guard <tag>                      the tag is v<semver>, points at HEAD here and on origin, and HEAD is on main; in a
#                                    tag push, also that the release environment makes the publish wait for a reviewer
#   environment                      only that last check
#   owners <tag>                     each package id is absent on nuget.org, or NUGET_USER owns it
#   verify <tag> <feed> <assets>     the feed holds exactly the release's packages at the tag's version, in an order
#                                    that pushes every dependency first; writes the release assets
#   draft <tag> <assets>             the GitHub release as a draft with every asset, uploading only what it lacks
#   publish <tag> <assets>           publishes that draft, Latest only when the tag is the highest stable version
#   push <tag> <feed> 1|2            pushes one phase to nuget.org, skipping what is there
#   wait <tag> <feed> 1|2            waits until nuget.org serves the phase; for phase 2 a timeout only warns
#   baseline <version>               prints the published release package validation compares a version with, or
#                                    nothing (ADR 0055)
#
# draft, publish and push publish, so they refuse to run anywhere but in a push of that tag; with --dry-run they and
# wait only print what they would do. Nothing here deletes or unlists anything (tools/check-repo, ADR 0029).
# Tests: build/release-test.sh.
set -uo pipefail

# The release environment, as release.yml's publish job names it, and NUGET_USER until Antonio sets the variable
# there (owner action 1, ADR 0050).
environment=release
nuget_user_placeholder=REPLACE-WITH-NUGET-PROFILE-NAME
nuget_source=https://api.nuget.org/v3/index.json
flat_container=https://api.nuget.org/v3-flatcontainer

# The push order, in two phases. Phase 1 depends on no package of ours, and the second phase starts only once
# nuget.org serves all of it, so it never lists a Whatsmeow.Client whose native packages are missing. Phase 2 pushes
# each package after its dependencies. verify fails when a nuspec breaks either rule.
phase1=(Whatsmeow.Sidecar.Native.Linux Whatsmeow.Sidecar.Native.MacOS Whatsmeow.Sidecar.Native.Windows Whatsmeow.Protocol.WhatsApp)
phase2=(Whatsmeow.Protocol Whatsmeow.Client Whatsmeow.Extensions.Hosting Whatsmeow.Testing)

# The six release targets (tools/check-licenses -targets); build/ci.sh install checks which binaries each native
# package holds, so this only counts them.
binaries=6

wait_minutes="${RELEASE_WAIT_MINUTES:-45}"

die() {
	if [ "${GITHUB_ACTIONS:-}" = true ]; then echo "::error title=release::$*"; else echo "error: $*"; fi >&2
	exit 1
}

warn() {
	if [ "${GITHUB_ACTIONS:-}" = true ]; then echo "::warning title=release::$*"; else echo "warning: $*"; fi >&2
}

# A release tag is v and a SemVer 2.0.0 version without build metadata: no leading zero in a number or a numeric
# pre-release identifier, and no empty identifier. Build metadata is refused because NuGet ignores it, so v1.0.0+a and
# v1.0.0+b would be one package version.
number='(0|[1-9][0-9]*)'
identifier='(0|[1-9][0-9]*|[0-9]*[A-Za-z-][0-9A-Za-z-]*)'
semver="^v$number\\.$number\\.$number(-$identifier(\\.$identifier)*)?\$"
stable="^v$number\\.$number\\.$number\$"

version_of() { # tag
	echo "${1#v}"
}

lowercase() {
	tr '[:upper:]' '[:lower:]' <<<"$1"
}

sha256_of() { # file
	if command -v sha256sum >/dev/null 2>&1; then sha256sum "$1"; else shasum -a 256 "$1"; fi | cut -d' ' -f1
}

require_tag_push() { # tag
	local tag="$1"
	if [ "${GITHUB_EVENT_NAME:-}" != push ] || [ "${GITHUB_REF_TYPE:-}" != tag ] || [ "${GITHUB_REF:-}" != "refs/tags/$tag" ]; then
		die "publishing runs only in a push of the tag $tag, not in this run (event ${GITHUB_EVENT_NAME:-none}, ref ${GITHUB_REF:-none}, type ${GITHUB_REF_TYPE:-none}); a rehearsal never publishes"
	fi
}

# Publishing runs only in a push of the tag itself. A rehearsal (workflow_dispatch) has no id-token permission either,
# so it could not get a nuget.org key, but this refuses first, with a reason.
require_publish() { # tag
	require_tag_push "$1"
	if [ -z "${NUGET_USER:-}" ] || [ "$NUGET_USER" = "$nuget_user_placeholder" ]; then
		die "NUGET_USER is unset or still the placeholder $nuget_user_placeholder: set the variable NUGET_USER on the $environment environment to the nuget.org username of the Trusted Publishing policy's creator (owner action 1, ADR 0050)"
	fi
}

# A job that names an environment that does not exist makes it, with no protection rule, so the publish would run
# without approval [observed: docs.github.com "Managing environments for deployment"]. Owner action 3 asks for required
# reviewers, no administrator bypass, and deployments from v* tags only; anything else fails closed. Anyone with read
# access may read an environment and its deployment policies, and on a public repository so may an anonymous request
# [observed: GET repos/cli/cli/environments/gh-acceptance-testing and repos/pypa/pip/environments/PyPI/
# deployment-branch-policies without a token, 2026-11-02]; release.yml passes GITHUB_TOKEN. The response carries
# can_admins_bypass, which the REST schema of "Get an environment" does not list [observed: those responses, and the
# schema at docs.github.com/rest/deployments/environments]; deployment_branch_policy {protected_branches,
# custom_branch_policies} and the policies' {name, type: branch|tag} are in it [observed: the schema].
require_protected_environment() {
	local json reviewers policies
	json="$(gh api "repos/${GITHUB_REPOSITORY:?}/environments/$environment" 2>&1)" ||
		die "no $environment environment in ${GITHUB_REPOSITORY}, or it cannot be read ($json); GitHub would make it unprotected, so the publish would run without approval (owner action 3, ADR 0050)"
	reviewers="$(jq -r '[.protection_rules[]? | select(.type == "required_reviewers") | .reviewers[]? | .reviewer.login // .reviewer.slug // .reviewer.name] | join(", ")' <<<"$json")" ||
		die "cannot read the $environment environment's protection rules"
	if [ -z "$reviewers" ]; then
		die "the $environment environment has no required reviewers, so the publish would run without approval (owner action 3, ADR 0050)"
	fi
	if [ "$(jq -r '.can_admins_bypass' <<<"$json")" != false ]; then
		die "the $environment environment lets administrators bypass its rules (can_admins_bypass is not false), so the publish could run without approval (owner action 3, ADR 0050)"
	fi
	if [ "$(jq -c '.deployment_branch_policy' <<<"$json")" != '{"protected_branches":false,"custom_branch_policies":true}' ]; then
		die "the $environment environment's deployment policy is $(jq -c '.deployment_branch_policy' <<<"$json"), not selected branches and tags, so it may deploy from any branch or tag (owner action 3, ADR 0050)"
	fi
	policies="$(gh api "repos/${GITHUB_REPOSITORY}/environments/$environment/deployment-branch-policies" 2>&1)" ||
		die "cannot read the $environment environment's deployment policies ($policies)"
	if [ "$(jq -c '[.total_count, [.branch_policies[]? | {name, type}]]' <<<"$policies")" != '[1,[{"name":"v*","type":"tag"}]]' ]; then
		die "the $environment environment's deployment policies are not exactly the tag pattern v* ($(jq -c '[.branch_policies[]? | {name, type}]' <<<"$policies")) (owner action 3, ADR 0050)"
	fi
	echo "the $environment environment has required reviewers: $reviewers; no administrator bypass; only v* tags deploy"
}

guard() { # tag
	local tag="$1" commit head remote peeled
	if [[ ! "$tag" =~ $semver ]]; then
		if [[ "$tag" == *+* ]]; then die "$tag has build metadata, which NuGet ignores; a release tag is v<major>.<minor>.<patch>[-<pre-release>]"; fi
		die "$tag is not a release tag: v<major>.<minor>.<patch>[-<pre-release>], SemVer 2.0.0"
	fi
	commit="$(git rev-parse -q --verify "refs/tags/$tag^{commit}")" || die "no tag $tag in this clone"
	head="$(git rev-parse HEAD)" || die "no HEAD"
	if [ "$commit" != "$head" ]; then die "the tag $tag ($commit) does not point at HEAD ($head)"; fi
	# Fetched here, so a clone's stale or missing origin/main never decides.
	git fetch --quiet --no-tags origin +refs/heads/main:refs/remotes/origin/main || die "cannot fetch main from origin"
	if ! git merge-base --is-ancestor "$head" refs/remotes/origin/main; then
		die "the tag $tag is not on main: $head is not an ancestor of origin/main, and only main is released (ADR 0050)"
	fi
	# The tag on origin, peeled: a tag moved or made again since this run began would otherwise publish other content
	# under its name. A rehearsal's tag is in its runner only.
	remote="$(git ls-remote origin "refs/tags/$tag" "refs/tags/$tag^{}")" || die "cannot list the tags on origin"
	peeled="$(awk -v t="refs/tags/$tag^{}" '$2 == t { print $1 }' <<<"$remote")"
	[ -n "$peeled" ] || peeled="$(awk -v t="refs/tags/$tag" '$2 == t { print $1 }' <<<"$remote")"
	if [ -n "$peeled" ] && [ "$peeled" != "$head" ]; then
		die "the tag $tag on origin points at $peeled, not at HEAD $head: it moved or was made again; push a new tag"
	fi
	if [ "${GITHUB_EVENT_NAME:-}" = push ]; then
		[ -n "$peeled" ] || die "the tag $tag is not on origin"
		require_tag_push "$tag"
		require_protected_environment
	fi
	echo "the tag $tag points at HEAD $head, which is on main"
}

nuspec_of() { # nupkg: the nuspec at its root
	local entry
	entry="$(unzip -Z1 "$1" | grep -E '^[^/]+\.nuspec$')" || return 1
	unzip -p "$1" "$entry"
}

verify() { # tag feed assets
	local tag="$1" feed="$2" assets="$3" version expected actual id nuspec dep dep_id dep_range i j phase
	local entry rid name count=0
	version="$(version_of "$tag")"
	[ -d "$feed" ] || die "no feed $feed"
	actual="$(tr -d '\r' <"$feed/version" 2>/dev/null)"
	if [ "$actual" != "$version" ]; then die "the feed's version '$actual' is not the version of the tag $tag, $version"; fi

	expected="$(
		for id in "${phase1[@]}" "${phase2[@]}"; do echo "$id.$version.nupkg"; done
		echo version
		echo "whatsmeow-net_${version}_source.tar.gz"
	)"
	expected="$(LC_ALL=C sort <<<"$expected")"
	actual="$(cd "$feed" && find . -mindepth 1 -maxdepth 1 | sed 's#^\./##' | LC_ALL=C sort)"
	if [ "$expected" != "$actual" ]; then
		diff <(echo "$expected") <(echo "$actual")
		die "the feed does not hold exactly the release's packages at $version, the version file and the source archive"
	fi

	local -a order=("${phase1[@]}" "${phase2[@]}")
	for i in "${!order[@]}"; do
		id="${order[$i]}"
		phase=2
		[ "$i" -lt "${#phase1[@]}" ] && phase=1
		nuspec="$(nuspec_of "$feed/$id.$version.nupkg")" || die "$id.$version.nupkg has no nuspec at its root"
		grep -qF "<id>$id</id>" <<<"$nuspec" || die "the nuspec of $id.$version.nupkg is not $id"
		grep -qF "<version>$version</version>" <<<"$nuspec" || die "the nuspec of $id.$version.nupkg is not version $version"
		while IFS= read -r dep; do
			[ -n "$dep" ] || continue
			dep_id="$(sed -E 's/.*id="([^"]*)".*/\1/' <<<"$dep")"
			dep_range="$(sed -E 's/.*version="([^"]*)".*/\1/' <<<"$dep")"
			if [ "$dep_range" != "$version" ] && [ "$dep_range" != "[$version]" ]; then
				die "$id depends on $dep_id at $dep_range, not at the release version $version"
			fi
			if [ "$phase" = 1 ]; then die "$id is pushed in the first phase, so it may depend on no package of ours, yet it depends on $dep_id"; fi
			for j in "${!order[@]}"; do [ "${order[$j]}" = "$dep_id" ] && break; done
			if [ "${order[$j]}" != "$dep_id" ]; then die "$id depends on $dep_id, which this release does not push"; fi
			if [ "$j" -ge "$i" ]; then die "$id depends on $dep_id, which is pushed after it; reorder phase2 in build/release.sh"; fi
		done < <(grep -oE '<dependency id="Whatsmeow\.[^"]*" version="[^"]*"' <<<"$nuspec" | sort -u)
	done
	echo "the feed holds exactly the release's packages at $version, each pushed after its dependencies"

	if [ -e "$assets" ]; then die "the assets directory $assets already exists"; fi
	mkdir -p "$assets" || die "cannot create $assets"
	for id in "${phase1[@]}"; do
		[[ "$id" == Whatsmeow.Sidecar.Native.* ]] || continue
		while IFS= read -r entry; do
			rid="$(cut -d/ -f2 <<<"$entry")"
			name="whatsmeow-net_${version}_$rid"
			[[ "$entry" == *.exe ]] && name="$name.exe"
			unzip -p "$feed/$id.$version.nupkg" "$entry" >"$assets/$name" || die "cannot extract $entry from $id"
			count=$((count + 1))
		done < <(unzip -Z1 "$feed/$id.$version.nupkg" | grep -E '^runtimes/[^/]+/native/whatsmeow-net(\.exe)?$')
	done
	if [ "$count" -ne "$binaries" ]; then die "the native packages hold $count binaries, not the $binaries binaries of the release targets"; fi
	# sha256sum's format, so sha256sum -c checks a download. Written beside the directory first, or the glob counts it.
	(
		cd "$assets" || exit 1
		for name in whatsmeow-net_"${version}"_*; do echo "$(sha256_of "$name")  $name"; done
	) >"$assets.checksums" || die "cannot write the checksums"
	mv "$assets.checksums" "$assets/whatsmeow-net_${version}_checksums.txt" || die "cannot move the checksums"
	cp "$feed/whatsmeow-net_${version}_source.tar.gz" "$assets/" || die "cannot copy the source archive"
	echo "release assets in $assets:"
	ls -l "$assets"
	cat "$assets/whatsmeow-net_${version}_checksums.txt"
}

# release_json tag: the release as gh shows it, or nothing when there is none. gh finds drafts too, and says
# "release not found" when there is neither [observed: cli/cli pkg/cmd/release/shared/fetch.go].
release_json() {
	local out
	if out="$(gh release view "$1" --json isDraft,assets 2>&1)"; then
		echo "$out"
		return 0
	fi
	grep -qF 'release not found' <<<"$out" || die "cannot read the release $1: $out"
}

# check_asset json file: 0 when the release holds file's bytes, 1 when it lacks the file, and a failure when it holds
# something else. The source archive is not byte-identical from run to run (ADR 0029), so only its state is checked, and
# the first upload stays; every other asset is reproducible, so its digest must be the local file's.
check_asset() {
	local json="$1" file="$2" name state digest
	name="$(basename "$file")"
	state="$(jq -r --arg n "$name" '.assets[] | select(.name == $n) | .state' <<<"$json")"
	[ -n "$state" ] || return 1
	if [ "$state" != uploaded ]; then
		die "the asset $name on the release is $state, not uploaded: its upload never finished; a person removes it on GitHub, then re-runs"
	fi
	if [[ "$name" == *_source.tar.gz ]]; then
		echo "$name is on the release; the source archive keeps its first upload (ADR 0029)"
		return 0
	fi
	digest="$(jq -r --arg n "$name" '.assets[] | select(.name == $n) | .digest // ""' <<<"$json")"
	if [ "$digest" != "sha256:$(sha256_of "$file")" ]; then
		die "the release holds different bytes for $name (${digest:-no digest}) than this run built (sha256:$(sha256_of "$file")); the tag's content changed, so push a new tag"
	fi
	echo "$name is on the release, the same bytes"
}

# The GitHub release is a draft until every asset is on it, since an immutable release takes no asset once published;
# it is published only after the nuget.org login worked, and before any package, so the Corresponding Source every
# native package points to is there first (ADR 0029). A re-run uploads only what the release lacks, and never replaces
# an asset.
draft() { # tag assets [--dry-run]
	local tag="$1" assets="$2" dry="${3:-}" version create json draft file rc
	version="$(version_of "$tag")"
	create=(gh release create "$tag" --draft --verify-tag --title "$tag" --generate-notes)
	[[ "$version" == *-* ]] && create+=(--prerelease)
	if [ "$dry" = --dry-run ]; then
		echo "would create the draft: ${create[*]}"
		for file in "$assets"/*; do echo "would upload $(basename "$file"): gh release upload $tag $file"; done
		return 0
	fi
	require_publish "$tag"
	json="$(release_json "$tag")" || exit 1
	if [ -z "$json" ]; then
		"${create[@]}" || die "cannot create the draft release $tag"
		json="$(release_json "$tag")" || exit 1
		[ -n "$json" ] || die "the draft release $tag is not there after creating it"
	fi
	draft="$(jq -r .isDraft <<<"$json")"
	for file in "$assets"/*; do
		check_asset "$json" "$file"
		rc=$?
		[ "$rc" -eq 0 ] && continue
		if [ "$draft" != true ]; then die "the published release $tag lacks $(basename "$file"), and a published immutable release takes no asset"; fi
		gh release upload "$tag" "$file" || die "cannot upload $(basename "$file")"
	done
	echo "the release $tag holds every asset"
}

# Latest is the highest stable version among the tags on origin and this one; a pre-release never is.
latest_flag() { # tag
	local tag="$1" remote highest
	# Read first: a failure inside the group below would be masked by its last command.
	remote="$(git ls-remote --tags --refs origin 'v*')" || die "cannot list the tags on origin, so cannot tell whether $tag is the latest"
	highest="$({
		awk '{ sub("refs/tags/", "", $2); print $2 }' <<<"$remote"
		echo "$tag"
	} | while IFS= read -r t; do if [[ "$t" =~ $stable ]]; then echo "$t"; fi; done |
		sed 's/^v//' | LC_ALL=C sort -t. -k1,1n -k2,2n -k3,3n | tail -n1)"
	if [ "v$highest" = "$tag" ]; then echo --latest; else echo --latest=false; fi
}

publish() { # tag assets [--dry-run]
	local tag="$1" assets="$2" dry="${3:-}" edit json file latest
	[ "$dry" = --dry-run ] || require_publish "$tag"
	latest="$(latest_flag "$tag")" || exit 1
	edit=(gh release edit "$tag" --draft=false "$latest")
	if [ "$dry" = --dry-run ]; then
		echo "would publish the draft: ${edit[*]}"
		return 0
	fi
	json="$(release_json "$tag")" || exit 1
	[ -n "$json" ] || die "no release $tag: draft makes it"
	for file in "$assets"/*; do
		check_asset "$json" "$file" >/dev/null || die "the release $tag lacks $(basename "$file"); run draft first"
	done
	if [ "$(jq -r .isDraft <<<"$json")" != true ]; then
		echo "the release $tag is already published"
		return 0
	fi
	"${edit[@]}" || die "cannot publish the release $tag"
	echo "the release $tag is published with every asset"
}

# Before the release goes public: each id is absent on nuget.org, or NUGET_USER owns it, so a push is not refused for an
# id someone else holds after the release is up (the nuget.org login alone proves no right to these ids). The flat
# container answers 404 for an id nobody has; search with packageid: matches one id exactly, without case, and lists
# its owners' usernames [observed: azuresearch-usnc.nuget.org/query?q=packageid:newtonsoft.json, 2026-11-02]. An id
# that exists but that search does not list (unlisted, or not indexed yet) fails closed. Search's owners may be a
# string or an array [inferred: NuGet's search API docs].
owners() { # tag
	local tag="$1" index search id lower code hits names
	index="$(curl -fsS --max-time 30 https://api.nuget.org/v3/index.json)" || die "cannot read nuget.org's service index"
	search="$(jq -r '[.resources[]? | select(.["@type"] == "SearchQueryService")][0]["@id"] // empty' <<<"$index")"
	[ -n "$search" ] || die "nuget.org's service index has no SearchQueryService"
	for id in "${phase1[@]}" "${phase2[@]}"; do
		lower="$(lowercase "$id")"
		code="$(curl -sS --max-time 30 -o /dev/null -w '%{http_code}' "$flat_container/$lower/index.json")" || die "cannot reach nuget.org for $id"
		hits="$(curl -fsS --max-time 30 "$search?q=packageid:$id&prerelease=true&semVerLevel=2.0.0")" || die "cannot search nuget.org for $id"
		names="$(jq -r --arg id "$lower" '.data[]? | select((.id | ascii_downcase) == $id) | .owners | if type == "array" then .[] else . end' <<<"$hits")"
		case "$code" in
			404) [ -n "$names" ] || { echo "$id is absent on nuget.org"; continue; } ;;
			200) ;;
			*) die "nuget.org answered $code for $id" ;;
		esac
		[ -n "$names" ] || die "$id is on nuget.org, but search lists no owner (unlisted, or not indexed yet), so it cannot be told ours; re-run later, or check its owners on nuget.org"
		if [ -z "${NUGET_USER:-}" ] || [ "$NUGET_USER" = "$nuget_user_placeholder" ]; then
			die "$id is on nuget.org and NUGET_USER is unset or still the placeholder $nuget_user_placeholder (owner action 1, ADR 0050)"
		fi
		grep -qixF -- "$NUGET_USER" <<<"$names" ||
			die "$id is on nuget.org, owned by $(paste -sd, - <<<"$names"), not $NUGET_USER: its push would be refused after the release went public"
		echo "$id is on nuget.org, owned by $NUGET_USER"
	done
}

phase_ids() { # 1|2
	case "$1" in
		1) printf '%s\n' "${phase1[@]}" ;;
		2) printf '%s\n' "${phase2[@]}" ;;
		*) die "no phase $1: 1 or 2" ;;
	esac
}

# One package at a time, in order, stopping at the first failure. --skip-duplicate makes a version nuget.org already
# has a warning (a 409), so a re-run pushes only what is missing.
push() { # tag feed 1|2 [--dry-run]
	local tag="$1" feed="$2" phase="$3" dry="${4:-}" version id ids nupkg command key="<key>"
	version="$(version_of "$tag")"
	ids="$(phase_ids "$phase")" || exit 1
	if [ "$dry" != --dry-run ]; then
		require_publish "$tag"
		[ -n "${NUGET_API_KEY:-}" ] || die "no NUGET_API_KEY: NuGet/login gives it"
		key="$NUGET_API_KEY"
	fi
	for id in $ids; do
		nupkg="$feed/$id.$version.nupkg"
		[ -f "$nupkg" ] || die "no $nupkg"
		command=(dotnet nuget push "$nupkg" --api-key "$key" --source "$nuget_source" --skip-duplicate)
		if [ "$dry" = --dry-run ]; then
			echo "would push: ${command[*]}"
			continue
		fi
		"${command[@]}" || die "cannot push $id $version"
	done
}

# nuget.org validates a pushed package before it serves it; the flat container, which restore reads, lists a version
# only then [inferred: nuget.org's documented validation step]. A phase 1 timeout fails the job, and a re-run resumes;
# a phase 2 timeout only warns, since a package nuget.org has not indexed yet is not a failure.
wait_served() { # tag feed 1|2 [--dry-run]
	local tag="$1" phase="$3" dry="${4:-}" version id ids url deadline pending message
	version="$(lowercase "$(version_of "$tag")")"
	ids="$(phase_ids "$phase")" || exit 1
	if [ "$dry" = --dry-run ]; then
		for id in $ids; do echo "would wait for $version in $flat_container/$(lowercase "$id")/index.json"; done
		return 0
	fi
	deadline=$((SECONDS + wait_minutes * 60))
	while :; do
		pending=()
		for id in $ids; do
			url="$flat_container/$(lowercase "$id")/index.json"
			curl -fsS --max-time 30 "$url" 2>/dev/null | grep -qF "\"$version\"" || pending+=("$id")
		done
		if [ "${#pending[@]}" -eq 0 ]; then
			echo "nuget.org serves every package of phase $phase at $version"
			return 0
		fi
		if [ "$SECONDS" -ge "$deadline" ]; then
			# Whether nuget.org takes a push of a version whose validation failed is [unknown].
			message="after $wait_minutes minutes nuget.org does not serve ${pending[*]} at $version yet. If nuget.org e-mailed a validation failure for one of them, a re-run will not help: that version stays refused, and whether it can be pushed again is unknown"
			if [ "$phase" = 2 ]; then
				warn "$message; otherwise it is still validating, and nothing waits for it"
				return 0
			fi
			die "$message; otherwise re-run the job, which skips what is there"
		fi
		echo "waiting for ${pending[*]} at $version"
		sleep 30
	done
}

# The package validation baseline of a version (ADR 0055): the highest stable v<major>.<minor>.<patch> tag merged into
# HEAD with the version's major and below it, so every build after a release is compared with what that release
# published. None before 1.0, none for a major's first release or the version of HEAD's own tag, never a pre-release,
# and never a tag of another branch.
baseline() {
	local version="$1" core major tags
	core="${version%%-*}"
	major="${core%%.*}"
	[ "$major" = 0 ] && return 0
	tags="$(git tag --merged HEAD --list "v$major.*")" || die "cannot list the tags merged into HEAD"
	{
		sed -n -E 's/^v((0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*))$/\1/p' <<<"$tags"
		echo "$core"
	} | sort -V | awk -v core="$core" '$0 == core { exit } { print }' | tail -n 1
}

usage() {
	sed -n '5,19p' "${BASH_SOURCE[0]}" | sed 's/^# \{0,1\}//' >&2
	exit 2
}

cmd="${1:-}"
[ $# -gt 0 ] && shift
case "$cmd" in
	guard) [ $# -eq 1 ] || usage; guard "$@" ;;
	environment) [ $# -eq 0 ] || usage; require_protected_environment ;;
	owners) [ $# -eq 1 ] || usage; owners "$@" ;;
	verify) [ $# -eq 3 ] || usage; verify "$@" ;;
	draft) [ $# -eq 2 ] || [ $# -eq 3 ] || usage; draft "$@" ;;
	publish) [ $# -eq 2 ] || [ $# -eq 3 ] || usage; publish "$@" ;;
	push) [ $# -eq 3 ] || [ $# -eq 4 ] || usage; push "$@" ;;
	wait) [ $# -eq 3 ] || [ $# -eq 4 ] || usage; wait_served "$@" ;;
	baseline) [ $# -eq 1 ] || usage; baseline "$@" ;;
	*) usage ;;
esac
