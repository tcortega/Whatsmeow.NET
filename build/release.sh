#!/usr/bin/env bash
# The release (#46, ADR 0050); .github/workflows/release.yml calls this and nothing else.
#
#   guard <tag>                      the tag is v<semver>, points at HEAD, and HEAD is on main; in a tag push, also
#                                    that the nuget.org profile name is set
#   verify <tag> <feed> <assets>     the feed holds exactly the release's packages at the tag's version, in an order
#                                    that pushes every dependency first; writes the release assets
#   github <tag> <assets>            creates the GitHub release as a draft, uploads what it lacks, then publishes it
#   push <tag> <feed> 1|2            pushes one phase to nuget.org, skipping what is there
#   wait <tag> <feed>                waits until nuget.org serves every package of the first phase
#
# github and push publish, so they refuse to run anywhere but in a push of that tag; with --dry-run they and wait only
# print what they would do. Nothing here deletes or unlists anything (tools/check-repo, ADR 0029).
# Tests: build/release-test.sh.
set -uo pipefail

# release.yml's NUGET_USER until Antonio names the nuget.org profile (owner action 1, ADR 0050).
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
	if [ "${GITHUB_ACTIONS:-}" = true ]; then echo "::error title=release::$*"; else echo "error: $*"; fi
	exit 1
}

# A release tag: v<major>.<minor>.<patch>, optionally -<pre-release>, as SemVer 2.0.0 writes them. Build metadata is
# refused: NuGet ignores it, so v1.0.0+a and v1.0.0+b would be one package version.
semver='^v(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(-[0-9A-Za-z.-]+)?$'

version_of() { # tag
	echo "${1#v}"
}

lowercase() {
	tr '[:upper:]' '[:lower:]' <<<"$1"
}

sha256_of() { # file
	if command -v sha256sum >/dev/null 2>&1; then sha256sum "$1"; else shasum -a 256 "$1"; fi | cut -d' ' -f1
}

# Publishing runs only in a push of the tag itself. A rehearsal (workflow_dispatch) has no id-token permission either,
# so it could not get a nuget.org key, but this refuses first, with a reason.
require_publish() { # tag
	local tag="$1"
	if [ "${GITHUB_EVENT_NAME:-}" != push ] || [ "${GITHUB_REF_TYPE:-}" != tag ] || [ "${GITHUB_REF:-}" != "refs/tags/$tag" ]; then
		die "publishing runs only in a push of the tag $tag, not in this run (event ${GITHUB_EVENT_NAME:-none}, ref ${GITHUB_REF:-none}); a rehearsal never publishes"
	fi
	if [ -z "${NUGET_USER:-}" ] || [ "$NUGET_USER" = "$nuget_user_placeholder" ]; then
		die "NUGET_USER in .github/workflows/release.yml is still the placeholder $nuget_user_placeholder: set it to the nuget.org profile name that owns the Trusted Publishing policy (owner action 1, ADR 0050)"
	fi
}

guard() { # tag
	local tag="$1" commit head
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
	if [ "${GITHUB_EVENT_NAME:-}" = push ]; then require_publish "$tag"; fi
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

# The GitHub release goes up before any package, so the Corresponding Source every native package points to is there
# first (ADR 0029). Immutable releases take no asset once published, so it is a draft until every asset is on it.
# A re-run uploads only the assets the release lacks, and never replaces one.
github() { # tag assets [--dry-run]
	local tag="$1" assets="$2" dry="${3:-}" version prerelease=() json err existing file name draft
	version="$(version_of "$tag")"
	[[ "$version" == *-* ]] && prerelease=(--prerelease)
	if [ "$dry" = --dry-run ]; then
		echo "would create the draft release: gh release create $tag --draft --verify-tag --title $tag --generate-notes ${prerelease[*]}"
		for file in "$assets"/*; do echo "would upload $(basename "$file"): gh release upload $tag $file"; done
		echo "would publish it: gh release edit $tag --draft=false"
		return 0
	fi
	require_publish "$tag"
	err="$(mktemp)"
	if ! json="$(gh release view "$tag" --json isDraft,assets 2>"$err")"; then
		if ! grep -qF 'release not found' "$err"; then cat "$err"; die "cannot read the release $tag"; fi
		gh release create "$tag" --draft --verify-tag --title "$tag" --generate-notes "${prerelease[@]}" || die "cannot create the draft release $tag"
		json="$(gh release view "$tag" --json isDraft,assets)" || die "cannot read the draft release $tag"
	fi
	rm -f "$err"
	draft="$(jq -r .isDraft <<<"$json")"
	existing="$(jq -r '.assets[].name' <<<"$json")"
	for file in "$assets"/*; do
		name="$(basename "$file")"
		if grep -qxF "$name" <<<"$existing"; then
			echo "$name is already on the release"
			continue
		fi
		if [ "$draft" != true ]; then die "the published release $tag lacks $name, and a published immutable release takes no asset"; fi
		gh release upload "$tag" "$file" || die "cannot upload $name"
	done
	if [ "$draft" = true ]; then
		gh release edit "$tag" --draft=false || die "cannot publish the release $tag"
	fi
	echo "the release $tag is published with every asset"
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
# only then [inferred: nuget.org's documented validation step]. A timeout fails the job, and a re-run resumes.
wait_served() { # tag feed [--dry-run]
	local tag="$1" dry="${3:-}" version id url deadline pending
	version="$(lowercase "$(version_of "$tag")")"
	if [ "$dry" = --dry-run ]; then
		for id in "${phase1[@]}"; do echo "would wait for $version in $flat_container/$(lowercase "$id")/index.json"; done
		return 0
	fi
	deadline=$((SECONDS + wait_minutes * 60))
	while :; do
		pending=()
		for id in "${phase1[@]}"; do
			url="$flat_container/$(lowercase "$id")/index.json"
			curl -fsS "$url" 2>/dev/null | grep -qF "\"$version\"" || pending+=("$id")
		done
		if [ "${#pending[@]}" -eq 0 ]; then
			echo "nuget.org serves every first-phase package at $version"
			return 0
		fi
		if [ "$SECONDS" -ge "$deadline" ]; then
			die "after $wait_minutes minutes nuget.org does not serve ${pending[*]} at $version yet; re-run the job, which skips what is there"
		fi
		echo "waiting for ${pending[*]} at $version"
		sleep 30
	done
}

usage() {
	sed -n '4,12p' "${BASH_SOURCE[0]}" | sed 's/^# \{0,1\}//' >&2
	exit 2
}

cmd="${1:-}"
[ $# -gt 0 ] && shift
case "$cmd" in
	guard) [ $# -eq 1 ] || usage; guard "$@" ;;
	verify) [ $# -eq 3 ] || usage; verify "$@" ;;
	github) [ $# -eq 2 ] || [ $# -eq 3 ] || usage; github "$@" ;;
	push) [ $# -eq 3 ] || [ $# -eq 4 ] || usage; push "$@" ;;
	wait) [ $# -eq 2 ] || [ $# -eq 3 ] || usage; wait_served "$@" ;;
	*) usage ;;
esac
