#!/usr/bin/env bash
# Tests for build/release.sh (#46, ADR 0050): every guard, verify, release, push and wait check, each case run in a
# git repository and a feed of its own under a temporary directory, with a local bare repository as origin. Nothing
# here reaches GitHub or nuget.org: gh, dotnet and curl are stand-ins on PATH for the whole run, which record their
# calls and answer from files.
# build/ci.sh contract runs it; locally, bash build/release-test.sh.
set -uo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
release="$root/build/release.sh"
tmp="$(mktemp -d)"
trap 'rm -rf "$tmp"' EXIT

failed=()

# expect pass|fail MESSAGE NAME COMMAND...: run COMMAND, which must pass, or fail with MESSAGE in its output, so a
# case proves the check it names and not another one that happens to fail first. A passing case may also name a
# MESSAGE its output must hold.
expect() {
	local want="$1" message="$2" name="$3" out rc line
	shift 3
	out="$("$@" 2>&1)"
	rc=$?
	if [ "$want" = pass ] && [ "$rc" -eq 0 ] && grep -qF -- "$message" <<<"$out"; then echo "ok: $name"; return; fi
	if [ "$want" = fail ] && [ "$rc" -ne 0 ] && grep -qF -- "$message" <<<"$out"; then echo "ok: $name"; return; fi
	echo "FAILED: $name (wanted $want${message:+ with \"$message\"}, exit $rc):"
	while IFS= read -r line; do echo "    $line"; done <<<"$out"
	failed+=("$name")
}

# --- stand-ins -------------------------------------------------------------------------------------------------

# gh answers from $tmp/gh: env.json is the release environment (none: a 404), release.json the release (none: not
# found); create, upload and edit change release.json as GitHub would. dotnet and curl record their arguments; curl
# serves $tmp/flat/<id>.json for a flat container URL. Every call lands in $tmp/calls.
stubs="$tmp/bin"
mkdir -p "$stubs" "$tmp/gh" "$tmp/flat"
export STUB="$tmp" PATH="$stubs:$PATH" GITHUB_REPOSITORY=tcortega/Whatsmeow.NET
cat >"$stubs/gh" <<'STUB'
#!/usr/bin/env bash
echo "gh $*" >>"$STUB/calls"
release="$STUB/gh/release.json"
case "$1 $2" in
	"api repos/tcortega/Whatsmeow.NET/environments/release")
		[ -f "$STUB/gh/env.json" ] || { echo "gh: Not Found (HTTP 404)" >&2; exit 1; }
		cat "$STUB/gh/env.json" ;;
	"api repos/tcortega/Whatsmeow.NET/environments/release/deployment-branch-policies")
		[ -f "$STUB/gh/policies.json" ] || { echo "gh: Not Found (HTTP 404)" >&2; exit 1; }
		cat "$STUB/gh/policies.json" ;;
	"release view")
		[ -f "$STUB/gh/release.error" ] && { cat "$STUB/gh/release.error" >&2; exit 1; }
		[ -f "$release" ] || { echo "release not found" >&2; exit 1; }
		cat "$release" ;;
	"release create") echo '{"isDraft":true,"assets":[]}' >"$release" ;;
	"release upload")
		sum="$(if command -v sha256sum >/dev/null; then sha256sum "$4"; else shasum -a 256 "$4"; fi | cut -d' ' -f1)"
		jq --arg n "$(basename "$4")" --arg d "sha256:$sum" '.assets += [{name: $n, state: "uploaded", digest: $d}]' "$release" >"$release.new" &&
			mv "$release.new" "$release" ;;
	"release edit") jq '.isDraft = false' "$release" >"$release.new" && mv "$release.new" "$release" ;;
	*) echo "unexpected gh $*" >&2; exit 9 ;;
esac
STUB
cat >"$stubs/dotnet" <<'STUB'
#!/usr/bin/env bash
echo "dotnet $*" >>"$STUB/calls"
# dotnet.fail names a package whose push fails.
[ -f "$STUB/dotnet.fail" ] && [[ "$*" == *"$(cat "$STUB/dotnet.fail")"* ]] && exit 1
exit 0
STUB
cat >"$stubs/curl" <<'STUB'
#!/usr/bin/env bash
# curl [-f] [-o FILE] [-w FORMAT] URL: the service index, the flat container and the search service of nuget.org,
# from files; a missing file is a 404, and $STUB/nuget.down makes every answer a 503.
echo "curl $*" >>"$STUB/calls"
url="${*: -1}" out="" format="" fail=false
while [ $# -gt 1 ]; do
	case "$1" in
		-o) out="$2"; shift ;;
		-w) format="$2"; shift ;;
		-f*) fail=true ;;
	esac
	shift
done
lower() { tr '[:upper:]' '[:lower:]' <<<"$1"; }
code=200 body=""
case "$url" in
	https://api.nuget.org/v3/index.json) file="$STUB/index.json" ;;
	https://api.nuget.org/v3-flatcontainer/*)
		id="${url#https://api.nuget.org/v3-flatcontainer/}"
		id="${id%/index.json}"
		# The script asks for the lowercase id NuGet's API documents (LOWER_ID); nuget.org answers mixed case too
		# [observed: Newtonsoft.Json, 2026-11-02], so this holds the script to the documented form, on any file system.
		[ "$id" = "$(lower "$id")" ] || code=400
		file="$STUB/flat/$id.json"
		# flat.status makes the flat container answer that status.
		[ -f "$STUB/flat.status" ] && { file=""; code="$(cat "$STUB/flat.status")"; } ;;
	https://search.example/query\?q=packageid:*)
		id="${url#*q=packageid:}"
		id="${id%%&*}"
		file="$STUB/search/$(lower "$id").json"
		[ -f "$file" ] || { file=""; body='{"totalHits":0,"data":[]}'; }
		# search.down makes only the search service fail.
		[ -f "$STUB/search.down" ] && { file=""; code=503; } ;;
	*) code=404 file="" ;;
esac
if [ -n "$file" ]; then
	if [ -f "$file" ]; then body="$(cat "$file")"; else code=404; fi
fi
[ -f "$STUB/nuget.down" ] && code=503
if [ "$code" -ge 400 ] && [ "$fail" = true ]; then exit 22; fi
if [ -n "$out" ]; then printf '%s' "$body" >"$out"; else printf '%s' "$body"; fi
[ -z "$format" ] || printf '%s' "$code"
exit 0
STUB
chmod +x "$stubs/"*
echo '{"resources":[{"@id":"https://search.example/query","@type":"SearchQueryService"},{"@id":"https://api.nuget.org/v3-flatcontainer/","@type":"PackageBaseAddress/3.0.0"}]}' >"$tmp/index.json"
mkdir -p "$tmp/search"
calls() { cat "$tmp/calls" 2>/dev/null; }
reset_calls() { rm -f "$tmp/calls"; }

# The environment of a tag push in GitHub Actions, and of a rehearsal.
as_push() { # tag command...
	local tag="$1"
	shift
	env GITHUB_EVENT_NAME=push GITHUB_REF_TYPE=tag GITHUB_REF="refs/tags/$tag" NUGET_USER=someone "$@"
}
as_dispatch() {
	env GITHUB_EVENT_NAME=workflow_dispatch GITHUB_REF_TYPE=branch GITHUB_REF=refs/heads/main NUGET_USER=someone "$@"
}

# As GitHub answers for owner action 3 [observed: the shape of pypa/pip's PyPI environment and its policies, 2026-11-02].
protected='{"name":"release","can_admins_bypass":false,"deployment_branch_policy":{"protected_branches":false,"custom_branch_policies":true},"protection_rules":[{"type":"required_reviewers","prevent_self_review":false,"reviewers":[{"type":"User","reviewer":{"login":"tcortega"}}]},{"type":"branch_policy"}]}'
v_tags='{"total_count":1,"branch_policies":[{"id":1,"name":"v*","type":"tag"}]}'
environment_is() { # json, or none
	if [ "$1" = none ]; then rm -f "$tmp/gh/env.json"; else echo "$1" >"$tmp/gh/env.json"; fi
}
policies_are() { # json, or none
	if [ "$1" = none ]; then rm -f "$tmp/gh/policies.json"; else echo "$1" >"$tmp/gh/policies.json"; fi
}
environment_is "$protected"
policies_are "$v_tags"

# --- guard -----------------------------------------------------------------------------------------------------

git_q() { git -c user.name=test -c user.email=test@example.com -c commit.gpgsign=false -c tag.gpgsign=false "$@" >/dev/null 2>&1; }

repo="$tmp/repo"
git init -q --bare -b main "$tmp/origin.git"
git clone -q "$tmp/origin.git" "$repo" 2>/dev/null
(
	cd "$repo" || exit 1
	git_q checkout -b main
	git_q commit --allow-empty -m one
	git_q push origin main
	git_q checkout -b side
	git_q commit --allow-empty -m off-main
	git_q checkout main
) || exit 1

in_repo() { (cd "$repo" && "$@"); }
guard() { in_repo env -u GITHUB_EVENT_NAME bash "$release" guard "$@"; }
push_guard() { in_repo as_push "$1" bash "$release" guard "$1"; }
at() { git_q -C "$repo" checkout --detach "$1"; }
tag_at() { git_q -C "$repo" tag -f "$1" "$2"; }
publish_tag() { git_q -C "$repo" push -f origin "refs/tags/$1"; }

for tag in v1.2.3 v1.0.0-rc.1 v1.0.0-0.3.7 v1.0.0-x-y.0a vfoo v1.2 v1.2.3+build.5 v01.2.3 v1.0.0-rc..1 v1.0.0-. v1.0.0-rc.01 v1.0.0-; do
	tag_at "$tag" main
done
tag_at v2.0.0 side

at main
expect pass "" "guard: a v<semver> tag at HEAD on main" guard v1.2.3
expect pass "" "guard: a pre-release tag at HEAD on main" guard v1.0.0-rc.1
# A rehearsal at a candidate version (ADR 0064): the tag is in the runner only, so origin has none.
dispatch_guard() { in_repo as_dispatch bash "$release" guard "$1"; }
expect pass "points at HEAD" "guard: a rehearsal at a candidate version, its tag in the runner only" dispatch_guard v1.0.0-rc.1
expect pass "" "guard: numeric pre-release identifiers" guard v1.0.0-0.3.7
expect pass "" "guard: hyphens and a digit-led alphanumeric identifier" guard v1.0.0-x-y.0a
expect fail "is not a release tag" "guard: vfoo is no release tag" guard vfoo
expect fail "is not a release tag" "guard: v1.2 is no release tag" guard v1.2
expect fail "is not a release tag" "guard: a leading zero is no SemVer" guard v01.2.3
expect fail "is not a release tag" "guard: an empty pre-release identifier (rc..1)" guard v1.0.0-rc..1
expect fail "is not a release tag" "guard: an empty pre-release identifier (-.)" guard v1.0.0-.
expect fail "is not a release tag" "guard: a numeric pre-release identifier with a leading zero (rc.01)" guard v1.0.0-rc.01
expect fail "is not a release tag" "guard: an empty pre-release" guard v1.0.0-
expect fail "build metadata" "guard: build metadata, which NuGet ignores, is refused" guard v1.2.3+build.5
expect fail "no tag v9.9.9" "guard: a tag that does not exist" guard v9.9.9
expect fail "does not point at HEAD" "guard: a tag on another commit than HEAD" guard v2.0.0
at side
expect fail "is not on main" "guard: a tag on a commit that is not on main" guard v2.0.0

# main moves on after the clone: the guard fetches main, so a stale origin/main never decides.
(
	cd "$tmp" && git clone -q origin.git other 2>/dev/null && cd other &&
		git_q commit --allow-empty -m two && git_q push origin main && git rev-parse HEAD
) >"$tmp/two" || exit 1
two="$(cat "$tmp/two")"
git_q -C "$repo" fetch origin "$two"
at "$two"
tag_at v1.3.0 HEAD
expect pass "" "guard: a commit main reached after the clone (the guard fetches main)" guard v1.3.0
git_q -C "$repo" update-ref -d refs/remotes/origin/main
expect pass "" "guard: no origin/main ref in the clone at all" guard v1.3.0

# A tag push: the tag on origin is the one at HEAD, and the release environment makes the publish wait for a reviewer.
at main
publish_tag v1.2.3
expect pass "" "guard: a push of the tag, at HEAD on origin too, into a protected environment" push_guard v1.2.3
git_q -C "$repo" tag -a -m "annotated" v1.5.0 main
publish_tag v1.5.0
expect pass "" "guard: an annotated tag, peeled on origin" push_guard v1.5.0
tag_at v1.7.0 main
expect fail "is not on origin" "guard: a push of a tag origin does not have" push_guard v1.7.0
tag_at v1.6.0 "$two"
publish_tag v1.6.0
tag_at v1.6.0 main
expect fail "on origin points at $two" "guard: a tag moved or made again on origin" push_guard v1.6.0
expect fail "on origin points at $two" "guard: in a rehearsal, a tag origin has elsewhere" guard v1.6.0
other_ref_guard() { in_repo as_push v1.0.0-rc.1 bash "$release" guard v1.2.3; }
expect fail "only in a push of the tag v1.2.3" "guard: a push of another tag than the one guarded" other_ref_guard
environment_is none
expect fail "no release environment" "guard: no release environment, which GitHub would make unprotected" push_guard v1.2.3
environment_is "$(jq -c '.protection_rules = []' <<<"$protected")"
expect fail "no required reviewers" "guard: an environment without protection rules" push_guard v1.2.3
environment_is "$(jq -c '.protection_rules = [{"type":"wait_timer","wait_timer":5},{"type":"branch_policy"}]' <<<"$protected")"
expect fail "no required reviewers" "guard: an environment with only a wait timer and a tag policy" push_guard v1.2.3
environment_is "$(jq -c '.protection_rules = [{"type":"required_reviewers","reviewers":[]}]' <<<"$protected")"
expect fail "no required reviewers" "guard: a required reviewers rule that names nobody" push_guard v1.2.3
expect fail "no required reviewers" "environment: the same check, on its own" in_repo bash "$release" environment
environment_is "$(jq -c '.can_admins_bypass = true' <<<"$protected")"
expect fail "lets administrators bypass" "environment: administrators may bypass the rules" in_repo bash "$release" environment
environment_is "$(jq -c 'del(.can_admins_bypass)' <<<"$protected")"
expect fail "lets administrators bypass" "environment: no can_admins_bypass field fails closed" in_repo bash "$release" environment
environment_is "$(jq -c '.deployment_branch_policy = null' <<<"$protected")"
expect fail "deploy from any branch or tag" "environment: no deployment policy, so every ref deploys" in_repo bash "$release" environment
environment_is "$(jq -c '.deployment_branch_policy = {"protected_branches":true,"custom_branch_policies":false}' <<<"$protected")"
expect fail "deploy from any branch or tag" "environment: protected branches only" in_repo bash "$release" environment
environment_is "$protected"
policies_are none
expect fail "cannot read the release environment's deployment policies" "environment: policies that cannot be read fail closed" in_repo bash "$release" environment
policies_are '{"total_count":0,"branch_policies":[]}'
expect fail "not exactly the tag pattern v*" "environment: custom policies, but none" in_repo bash "$release" environment
policies_are '{"total_count":1,"branch_policies":[{"name":"v*","type":"branch"}]}'
expect fail "not exactly the tag pattern v*" "environment: v* as a branch pattern" in_repo bash "$release" environment
policies_are '{"total_count":1,"branch_policies":[{"name":"*","type":"tag"}]}'
expect fail "not exactly the tag pattern v*" "environment: every tag" in_repo bash "$release" environment
policies_are '{"total_count":2,"branch_policies":[{"name":"v*","type":"tag"},{"name":"main","type":"branch"}]}'
expect fail "not exactly the tag pattern v*" "environment: v* and a branch" in_repo bash "$release" environment
policies_are "$v_tags"
expect pass "required reviewers: tcortega" "environment: a protected environment" in_repo bash "$release" environment
expect pass "only v* tags deploy" "environment: the tag pattern is reported" in_repo bash "$release" environment

# --- verify ----------------------------------------------------------------------------------------------------

version=1.2.3
natives=(Whatsmeow.Sidecar.Native.Linux Whatsmeow.Sidecar.Native.MacOS Whatsmeow.Sidecar.Native.Windows)

# nupkg DIR ID VERSION DEPENDENCY...: a package as dotnet pack writes one: its nuspec at the root, and for a native
# package its two binaries. A DEPENDENCY is id=range.
nupkg() {
	local dir="$1" id="$2" v="$3" work dep rid rids
	shift 3
	work="$(mktemp -d "$tmp/pkg.XXXXXX")"
	{
		echo '<?xml version="1.0" encoding="utf-8"?>'
		echo '<package xmlns="http://schemas.microsoft.com/packaging/2013/05/nuspec.xsd">'
		echo '  <metadata>'
		echo "    <id>$id</id>"
		echo "    <version>$v</version>"
		echo '    <dependencies>'
		echo '      <group targetFramework="net10.0">'
		for dep in "$@"; do
			echo "        <dependency id=\"${dep%%=*}\" version=\"${dep#*=}\" exclude=\"Build,Analyzers\" />"
		done
		echo '      </group>'
		echo '    </dependencies>'
		echo '  </metadata>'
		echo '</package>'
	} >"$work/$id.nuspec"
	case "$id" in
		*.Linux) rids=(linux-x64 linux-arm64) ;;
		*.MacOS) rids=(osx-x64 osx-arm64) ;;
		*.Windows) rids=(win-x64 win-arm64) ;;
		*) rids=() ;;
	esac
	for rid in "${rids[@]}"; do
		mkdir -p "$work/runtimes/$rid/native"
		echo "whatsmeow-net $v for $rid" >"$work/runtimes/$rid/native/whatsmeow-net$(case "$rid" in win-*) echo .exe ;; esac)"
	done
	(cd "$work" && zip -qr "$dir/$id.$v.nupkg" .)
	rm -rf "$work"
}

# feed NAME: the feed build/ci.sh package writes at $version, with the dependencies the real nuspecs have.
feed() {
	local dir="$tmp/feeds/$1" id
	mkdir -p "$dir"
	for id in "${natives[@]}" Whatsmeow.Protocol.WhatsApp; do nupkg "$dir" "$id" "$version"; done
	nupkg "$dir" Whatsmeow.Protocol "$version" "Whatsmeow.Protocol.WhatsApp=$version" "Google.Protobuf=3.33.0"
	nupkg "$dir" Whatsmeow.Client "$version" "Whatsmeow.Protocol=$version" \
		"Whatsmeow.Sidecar.Native.Linux=[$version]" "Whatsmeow.Sidecar.Native.MacOS=[$version]" "Whatsmeow.Sidecar.Native.Windows=[$version]"
	nupkg "$dir" Whatsmeow.Extensions.Hosting "$version" "Whatsmeow.Client=[$version]"
	nupkg "$dir" Whatsmeow.Testing "$version" "Whatsmeow.Client=[$version]" "Whatsmeow.Extensions.Hosting=[$version]"
	echo "$version" >"$dir/version"
	echo source >"$dir/whatsmeow-net_${version}_source.tar.gz"
	echo "$dir"
}

verify() { # feed-dir [tag]
	rm -rf "$tmp/assets"
	bash "$release" verify "${2:-v$version}" "$1" "$tmp/assets"
}

good="$(feed good)"
expect pass "" "verify: the feed build/ci.sh package writes" verify "$good"
good_assets() {
	verify "$good" >/dev/null || return 1
	local expected
	expected="$(printf '%s\n' whatsmeow-net_1.2.3_checksums.txt whatsmeow-net_1.2.3_source.tar.gz \
		whatsmeow-net_1.2.3_{linux-arm64,linux-x64,osx-arm64,osx-x64} whatsmeow-net_1.2.3_{win-arm64,win-x64}.exe | LC_ALL=C sort)"
	[ "$(cd "$tmp/assets" && find . -type f | sed 's#^\./##' | LC_ALL=C sort)" = "$expected" ] || { echo "assets: $(ls "$tmp/assets")"; return 1; }
	[ "$(wc -l <"$tmp/assets/whatsmeow-net_1.2.3_checksums.txt" | tr -d ' ')" = 6 ] || return 1
	[ "$(cat "$tmp/assets/whatsmeow-net_1.2.3_win-x64.exe")" = "whatsmeow-net 1.2.3 for win-x64" ] || return 1
	(cd "$tmp/assets" && if command -v sha256sum >/dev/null; then sha256sum -c whatsmeow-net_1.2.3_checksums.txt; else shasum -a 256 -c whatsmeow-net_1.2.3_checksums.txt; fi)
}
expect pass "" "verify: the assets are the six packaged binaries, their checksums and the source archive" good_assets

mutant() { # name: a copy of the good feed to break
	rm -rf "$tmp/feeds/$1"
	cp -R "$good" "$tmp/feeds/$1"
	echo "$tmp/feeds/$1"
}

expect fail "is not the version of the tag" "verify: a tag the feed was not built at" verify "$good" v1.2.4

f="$(mutant version-file)"
echo 1.2.4 >"$f/version"
expect fail "is not the version of the tag" "verify: the feed's version file differs from the tag" verify "$f"

f="$(mutant renamed)"
mv "$f/Whatsmeow.Client.1.2.3.nupkg" "$f/Whatsmeow.Client.1.2.4.nupkg"
expect fail "does not hold exactly" "verify: a package at another version than the tag" verify "$f"

f="$(mutant missing)"
rm "$f/Whatsmeow.Sidecar.Native.MacOS.1.2.3.nupkg"
expect fail "does not hold exactly" "verify: a package is missing" verify "$f"

f="$(mutant extra)"
nupkg "$f" Whatsmeow.Extra "$version"
expect fail "does not hold exactly" "verify: a package the release does not push" verify "$f"

f="$(mutant stray)"
echo x >"$f/notes.txt"
expect fail "does not hold exactly" "verify: a stray file in the feed" verify "$f"

f="$(mutant no-source)"
rm "$f/whatsmeow-net_1.2.3_source.tar.gz"
expect fail "does not hold exactly" "verify: no Corresponding Source archive" verify "$f"

f="$(mutant nuspec-version)"
rm "$f/Whatsmeow.Testing.1.2.3.nupkg"
nupkg "$f" Whatsmeow.Testing 1.2.4 "Whatsmeow.Client=[$version]" "Whatsmeow.Extensions.Hosting=[$version]"
mv "$f/Whatsmeow.Testing.1.2.4.nupkg" "$f/Whatsmeow.Testing.1.2.3.nupkg"
expect fail "is not version 1.2.3" "verify: a nuspec version differs from its file name" verify "$f"

f="$(mutant nuspec-id)"
rm "$f/Whatsmeow.Testing.1.2.3.nupkg"
nupkg "$f" Whatsmeow.Fake "$version"
mv "$f/Whatsmeow.Fake.1.2.3.nupkg" "$f/Whatsmeow.Testing.1.2.3.nupkg"
expect fail "is not Whatsmeow.Testing" "verify: a nuspec id differs from its file name" verify "$f"

f="$(mutant dep-version)"
rm "$f/Whatsmeow.Client.1.2.3.nupkg"
nupkg "$f" Whatsmeow.Client "$version" "Whatsmeow.Protocol=$version" \
	"Whatsmeow.Sidecar.Native.Linux=[1.2.2]" "Whatsmeow.Sidecar.Native.MacOS=[$version]" "Whatsmeow.Sidecar.Native.Windows=[$version]"
expect fail "depends on Whatsmeow.Sidecar.Native.Linux at [1.2.2]" "verify: a dependency on a package of ours at another version" verify "$f"

f="$(mutant dep-unknown)"
rm "$f/Whatsmeow.Extensions.Hosting.1.2.3.nupkg"
nupkg "$f" Whatsmeow.Extensions.Hosting "$version" "Whatsmeow.Client=[$version]" "Whatsmeow.Missing=[$version]"
expect fail "depends on Whatsmeow.Missing, which this release does not push" "verify: a dependency on a package the release does not push" verify "$f"

f="$(mutant dep-later)"
rm "$f/Whatsmeow.Protocol.1.2.3.nupkg"
nupkg "$f" Whatsmeow.Protocol "$version" "Whatsmeow.Protocol.WhatsApp=$version" "Whatsmeow.Client=$version"
expect fail "depends on Whatsmeow.Client, which is pushed after it" "verify: a dependency pushed after its dependent" verify "$f"

f="$(mutant dep-first-phase)"
rm "$f/Whatsmeow.Protocol.WhatsApp.1.2.3.nupkg"
nupkg "$f" Whatsmeow.Protocol.WhatsApp "$version" "Whatsmeow.Sidecar.Native.Linux=[$version]"
expect fail "is pushed in the first phase, so it may depend on no package of ours" "verify: a first-phase package with a dependency of ours" verify "$f"

f="$(mutant five-binaries)"
(cd "$f" && zip -qd Whatsmeow.Sidecar.Native.Windows.1.2.3.nupkg 'runtimes/win-arm64/*')
expect fail "6 binaries" "verify: a native package without one of its binaries" verify "$f"

assets_taken() {
	rm -rf "$tmp/assets" && mkdir -p "$tmp/assets" && echo old >"$tmp/assets/old"
	bash "$release" verify "v$version" "$good" "$tmp/assets"
}
expect fail "already exists" "verify: an assets directory that already exists" assets_taken

# --- push and wait ---------------------------------------------------------------------------------------------

verify "$good" >/dev/null

push_order() {
	local out
	out="$(env NUGET_API_KEY=secret bash "$release" push "v$version" "$good" 1 --dry-run &&
		env NUGET_API_KEY=secret bash "$release" push "v$version" "$good" 2 --dry-run)" || return 1
	echo "$out"
	[ "$(grep -o 'Whatsmeow\.[A-Za-z.]*\.1\.2\.3\.nupkg' <<<"$out" | tr '\n' ' ')" = \
		"Whatsmeow.Sidecar.Native.Linux.1.2.3.nupkg Whatsmeow.Sidecar.Native.MacOS.1.2.3.nupkg Whatsmeow.Sidecar.Native.Windows.1.2.3.nupkg Whatsmeow.Protocol.WhatsApp.1.2.3.nupkg Whatsmeow.Protocol.1.2.3.nupkg Whatsmeow.Client.1.2.3.nupkg Whatsmeow.Extensions.Hosting.1.2.3.nupkg Whatsmeow.Testing.1.2.3.nupkg " ] &&
		[ "$(grep -c -- '--skip-duplicate' <<<"$out")" = 8 ] && ! grep -qF 'secret' <<<"$out"
}
expect pass "" "push --dry-run: the native packages first, every package once, with --skip-duplicate, never the key" push_order

pushed() { # phase: the packages dotnet was given, in order
	reset_calls
	as_push "v$version" env NUGET_API_KEY=secret bash "$release" push "v$version" "$good" "$1" || return 1
	calls
	grep '^dotnet nuget push' "$tmp/calls" | grep -F -- '--skip-duplicate' | grep -F -- '--api-key secret' |
		grep -F -- '--source https://api.nuget.org/v3/index.json' | grep -oE 'Whatsmeow\.[A-Za-z.]*\.1\.2\.3\.nupkg' | tr '\n' ' '
}
push_phase() { # phase expected
	local out
	out="$(pushed "$1")" || return 1
	echo "$out"
	[ "$(tail -n1 <<<"$out")" = "$2" ]
}
expect pass "" "push 1: the native packages, then Whatsmeow.Protocol.WhatsApp, each with the key and --skip-duplicate" push_phase 1 \
	"Whatsmeow.Sidecar.Native.Linux.1.2.3.nupkg Whatsmeow.Sidecar.Native.MacOS.1.2.3.nupkg Whatsmeow.Sidecar.Native.Windows.1.2.3.nupkg Whatsmeow.Protocol.WhatsApp.1.2.3.nupkg "
expect pass "" "push 2: the packages that depend on them, in dependency order" push_phase 2 \
	"Whatsmeow.Protocol.1.2.3.nupkg Whatsmeow.Client.1.2.3.nupkg Whatsmeow.Extensions.Hosting.1.2.3.nupkg Whatsmeow.Testing.1.2.3.nupkg "

stops_at_failure() {
	echo Whatsmeow.Client. >"$tmp/dotnet.fail"
	pushed 2
	local rc=$?
	rm -f "$tmp/dotnet.fail"
	[ "$rc" -ne 0 ] || return 0
	! grep -qE 'Whatsmeow\.(Extensions\.Hosting|Testing)\.' "$tmp/calls" || { echo "pushed past the failure"; return 0; }
	return 1
}
expect fail "cannot push Whatsmeow.Client" "push: stops at the first package that fails, before its dependents" stops_at_failure

wait_urls() { # phase count
	local out
	out="$(bash "$release" wait "v$version" "$good" "$1" --dry-run)" || return 1
	echo "$out"
	[ "$(grep -c 'https://api.nuget.org/v3-flatcontainer/whatsmeow\.[a-z.]*/index.json' <<<"$out")" = "$2" ]
}
expect pass "whatsmeow.sidecar.native.windows/index.json" "wait 1 --dry-run: the four first-phase packages, lowercased" wait_urls 1 4
expect pass "whatsmeow.testing/index.json" "wait 2 --dry-run: the four second-phase packages" wait_urls 2 4

served() { # ids...: the flat container lists 1.2.3 for these
	local id
	rm -f "$tmp/flat/"*
	for id in "$@"; do echo '{"versions":["1.2.2","1.2.3"]}' >"$tmp/flat/$id.json"; done
}
waited() { # phase
	reset_calls
	RELEASE_WAIT_MINUTES=0 bash "$release" wait "v$version" "$good" "$1"
}
served whatsmeow.sidecar.native.linux whatsmeow.sidecar.native.macos whatsmeow.sidecar.native.windows whatsmeow.protocol.whatsapp
expect pass "serves every package of phase 1" "wait 1: nuget.org serves all four" waited 1
curl_bounded() { waited 1 >/dev/null && [ "$(grep -c '^curl .*--max-time 30 ' "$tmp/calls")" = 4 ]; }
expect pass "" "wait: every request is bounded by --max-time 30" curl_bounded
served whatsmeow.sidecar.native.linux whatsmeow.sidecar.native.macos whatsmeow.protocol.whatsapp
expect fail "a re-run will not help" "wait 1: a package nuget.org does not serve fails, naming a validation e-mail" waited 1
expect fail "Whatsmeow.Sidecar.Native.Windows" "wait 1: the timeout names the package" waited 1
served whatsmeow.protocol whatsmeow.client whatsmeow.extensions.hosting
expect pass "does not serve Whatsmeow.Testing at 1.2.3 yet" "wait 2: only warns, since not indexed yet is not a failure" waited 2

# --- owners ----------------------------------------------------------------------------------------------------

# Each of the eight ids is absent on nuget.org, or NUGET_USER owns it, before the release goes public.
ids=(Whatsmeow.Sidecar.Native.Linux Whatsmeow.Sidecar.Native.MacOS Whatsmeow.Sidecar.Native.Windows Whatsmeow.Protocol.WhatsApp
	Whatsmeow.Protocol Whatsmeow.Client Whatsmeow.Extensions.Hosting Whatsmeow.Testing)
nothing_on_nuget() { rm -f "$tmp/flat/"* "$tmp/search/"* "$tmp/nuget.down"; }
on_nuget() { # id owners-json: the id exists, and search lists it with those owners
	local lower
	lower="$(tr '[:upper:]' '[:lower:]' <<<"$1")"
	echo '{"versions":["0.9.0"]}' >"$tmp/flat/$lower.json"
	jq -n --arg id "$1" --argjson o "$2" '{totalHits: 1, data: [{id: $id, owners: $o, verified: false}]}' >"$tmp/search/$lower.json"
}
owners() { reset_calls; env NUGET_USER="${1:-someone}" bash "$release" owners "v$version"; }
nothing_on_nuget
expect pass "absent on nuget.org" "owners: eight ids nobody has" owners
expect pass "absent on nuget.org" "owners: absent ids need no NUGET_USER (a rehearsal)" env -u NUGET_USER bash "$release" owners "v$version"
all_bounded() { owners >/dev/null && [ "$(grep -c '^curl ' "$tmp/calls")" -gt 0 ] && ! grep '^curl ' "$tmp/calls" | grep -qvF -- '--max-time 30'; }
expect pass "" "owners: every request is bounded by --max-time 30" all_bounded
for id in "${ids[@]}"; do on_nuget "$id" '["SomeOne","an-org"]'; done
expect pass "owned by someone" "owners: eight ids NUGET_USER owns, matched without case (a re-run)" owners
on_nuget Whatsmeow.Testing '"someone"'
expect pass "owned by someone" "owners: owners as one string" owners
on_nuget Whatsmeow.Client '["mallory"]'
expect fail "Whatsmeow.Client is on nuget.org, owned by mallory, not someone" "owners: an id someone else owns" owners
on_nuget Whatsmeow.Client '["someones"]'
expect fail "owned by someones, not someone" "owners: an owner whose name only contains NUGET_USER's" owners
nothing_on_nuget
on_nuget Whatsmeow.Testing '["mallory"]'
rm -f "$tmp/flat/whatsmeow.testing.json"
expect fail "owned by mallory, not someone" "owners: an id search lists while the flat container says 404 is not absent" owners
nothing_on_nuget
touch "$tmp/search.down"
expect fail "cannot search nuget.org for Whatsmeow.Sidecar.Native.Linux" "owners: a search failure fails closed" owners
rm -f "$tmp/search.down"
nothing_on_nuget
echo '{"versions":["1.0.0"]}' >"$tmp/flat/whatsmeow.protocol.json"
expect fail "search lists no owner" "owners: an id on nuget.org that search does not list fails closed" owners
nothing_on_nuget
on_nuget Whatsmeow.Protocol '["someone"]'
expect fail "still the placeholder" "owners: an existing id while NUGET_USER is the placeholder" owners REPLACE-WITH-NUGET-PROFILE-NAME
nothing_on_nuget
touch "$tmp/nuget.down"
expect fail "cannot read nuget.org's service index" "owners: nuget.org down fails closed" owners
rm -f "$tmp/nuget.down"
cp "$tmp/index.json" "$tmp/index.good"
echo '{"resources":[]}' >"$tmp/index.json"
expect fail "no SearchQueryService" "owners: a service index without search fails closed" owners
cp "$tmp/index.good" "$tmp/index.json"
flat_error() { echo 500 >"$tmp/flat.status"; owners; local rc=$?; rm -f "$tmp/flat.status"; return "$rc"; }
expect fail "nuget.org answered 500 for Whatsmeow.Sidecar.Native.Linux" "owners: a flat container error is no absent id" flat_error

# --- the GitHub release ----------------------------------------------------------------------------------------

release_is() { # json, or none
	if [ "$1" = none ]; then rm -f "$tmp/gh/release.json"; else echo "$1" >"$tmp/gh/release.json"; fi
}
asset() { # name state [digest]: one asset of the release, by default with the digest of the local file
	local digest="${3:-sha256:$(if command -v sha256sum >/dev/null; then sha256sum "$tmp/assets/$1"; else shasum -a 256 "$tmp/assets/$1"; fi | cut -d' ' -f1)}"
	jq -n --arg n "$1" --arg s "$2" --arg d "$digest" '{name: $n, state: $s, digest: $d}'
}
release_with() { # draft asset-json...
	local draft="$1"
	shift
	jq -n --argjson d "$draft" --slurpfile a <(printf '%s\n' "$@") '{isDraft: $d, assets: $a}'
}
gh_calls() { grep -c "^gh release $1" "$tmp/calls"; }
draft() { reset_calls; in_repo as_push "v$version" bash "$release" draft "v$version" "$tmp/assets"; }
publish() { reset_calls; in_repo as_push "${1:-v$version}" bash "$release" publish "${1:-v$version}" "$tmp/assets"; }
names=()
while IFS= read -r name; do names+=("$name"); done < <(cd "$tmp/assets" && find . -type f | sed 's#^\./##' | LC_ALL=C sort)
binary=whatsmeow-net_1.2.3_linux-x64
source_archive=whatsmeow-net_1.2.3_source.tar.gz

new_draft() { release_is none; draft || return 1; calls; [ "$(gh_calls create)" = 1 ] && [ "$(gh_calls upload)" = 8 ] && [ "$(gh_calls edit)" = 0 ]; }
expect pass "" "draft: a new release is a draft with all eight assets, not yet published" new_draft
create_flags() { release_is none; draft >/dev/null || return 1; grep '^gh release create' "$tmp/calls" | grep -F -- '--draft' | grep -F -- '--verify-tag' | grep -vF -- '--prerelease'; }
expect pass "" "draft: created with --draft --verify-tag, no --prerelease for a stable tag" create_flags
draft_again() { draft || return 1; [ "$(gh_calls create)" = 0 ] && [ "$(gh_calls upload)" = 0 ]; }
expect pass "" "draft: a re-run on a complete draft uploads nothing" draft_again
partial() { release_with true "$(asset "${names[0]}" uploaded)" "$(asset "${names[1]}" uploaded)" "$(asset "${names[2]}" uploaded)" >"$tmp/gh/release.json"; draft || return 1; [ "$(gh_calls upload)" = 5 ]; }
expect pass "" "draft: a re-run on a partial draft uploads only the five missing assets" partial
release_with true "$(asset "$binary" uploaded sha256:0000)" >"$tmp/gh/release.json"
expect fail "holds different bytes" "draft: a binary on the release that is not the local one fails" draft
source_kept() { release_with true "$(asset "$source_archive" uploaded sha256:0000)" >"$tmp/gh/release.json"; draft || return 1; ! grep -q "upload.*$source_archive" "$tmp/calls" && [ "$(gh_calls upload)" = 7 ]; }
expect pass "keeps its first upload" "draft: the source archive, never byte-identical, keeps its first upload" source_kept
release_with true "$(asset "$binary" starter)" >"$tmp/gh/release.json"
expect fail "is starter, not uploaded" "draft: an asset whose upload never finished fails" draft
release_with true "$(asset "$source_archive" starter)" >"$tmp/gh/release.json"
expect fail "is starter, not uploaded" "draft: the source archive's state is checked too" draft
complete() { # draft: every local asset on the release
	local name list=()
	for name in "${names[@]}"; do list+=("$(asset "$name" uploaded)"); done
	release_with "$1" "${list[@]}"
}
complete false >"$tmp/gh/release.json"
published_done() { draft || return 1; [ "$(gh_calls upload)" = 0 ]; }
expect pass "" "draft: a published release with every asset is left as it is" published_done
release_with false "$(asset "${names[0]}" uploaded)" >"$tmp/gh/release.json"
expect fail "takes no asset" "draft: a published release that lacks an asset fails" draft
prerelease_draft() {
	release_is none
	reset_calls
	in_repo as_push v1.0.0-rc.1 bash "$release" draft v1.0.0-rc.1 "$tmp/assets" || return 1
	grep '^gh release create' "$tmp/calls" | grep -qF -- '--prerelease'
}
expect pass "" "draft: a pre-release tag makes a pre-release" prerelease_draft

complete true >"$tmp/gh/release.json"
publish_draft() { publish || return 1; calls; [ "$(gh_calls edit)" = 1 ] && grep '^gh release edit' "$tmp/calls" | grep -qF -- '--draft=false'; }
expect pass "" "publish: a complete draft is published" publish_draft
complete false >"$tmp/gh/release.json"
publish_again() { publish || return 1; [ "$(gh_calls edit)" = 0 ]; }
expect pass "" "publish: a published release is left as it is" publish_again
release_with true "$(asset "${names[0]}" uploaded)" >"$tmp/gh/release.json"
expect fail "lacks" "publish: a draft without every asset is not published" publish
release_is none
expect fail "no release v1.2.3" "publish: no draft at all" publish
echo "HTTP 502: Bad Gateway" >"$tmp/gh/release.error"
no_create() { draft; local rc=$?; ! grep -q '^gh release create' "$tmp/calls" || { echo "created a release"; return 0; }; return "$rc"; }
expect fail "cannot read the release v1.2.3: HTTP 502" "draft: a failure to read the release is no missing release" no_create
rm -f "$tmp/gh/release.error"

# Latest: the highest stable v<semver> on origin, never a pre-release; origin has v1.2.3, v1.5.0 and v1.6.0 here.
latest_flag() { # tag: the --latest flag publish would pass
	in_repo bash "$release" publish "$1" "$tmp/assets" --dry-run | grep -oE -- '--latest(=false)?'
}
latest_is() { # tag expected
	local got
	got="$(latest_flag "$1")"
	echo "$1: $got"
	[ "$got" = "$2" ]
}
expect pass "" "publish --dry-run: a stable tag below the highest is not latest" latest_is v1.2.3 --latest=false
expect pass "" "publish --dry-run: the highest stable tag is latest" latest_is v1.6.0 --latest
expect pass "" "publish --dry-run: a pre-release is never latest" latest_is v2.0.0-rc.1 --latest=false
tag_at v1.9.0 main
tag_at v1.10.0 main
publish_tag v1.9.0
publish_tag v1.10.0
expect pass "" "publish --dry-run: versions compare as numbers (1.10.0 over 1.9.0)" latest_is v1.10.0 --latest
expect pass "" "publish --dry-run: 1.9.0 is not latest beside 1.10.0" latest_is v1.9.0 --latest=false
expect pass "" "publish --dry-run: a new tag origin does not have yet is compared too" latest_is v1.11.0 --latest
no_origin() {
	local url rc
	url="$(git -C "$repo" remote get-url origin)"
	git -C "$repo" remote set-url origin "$tmp/no-such-origin.git"
	in_repo bash "$release" publish v1.4.1 "$tmp/assets" --dry-run
	rc=$?
	git -C "$repo" remote set-url origin "$url"
	return "$rc"
}
expect fail "cannot list the tags on origin" "publish --dry-run: tags origin cannot list fail, never a guess at latest" no_origin
real_latest() { complete true >"$tmp/gh/release.json"; publish v1.10.0 >/dev/null || return 1; grep '^gh release edit' "$tmp/calls" | grep -qE -- '--latest( |$)'; }
expect pass "" "publish: the edit passes the same --latest" real_latest
draft_plan() {
	local out
	out="$(in_repo bash "$release" draft "v$version" "$tmp/assets" --dry-run)" || return 1
	echo "$out"
	grep -qF -- '--draft' <<<"$out" && [ "$(grep -c 'would upload' <<<"$out")" = 8 ]
}
expect pass "" "draft --dry-run: a draft and eight uploads" draft_plan

# --- publishing only in a push of the tag ----------------------------------------------------------------------

# A refusal must come before any side effect: a call to gh release create, upload or edit, or to dotnet.
refused() { # runner subcommand args...
	local runner="$1" rc
	shift
	reset_calls
	release_is none
	in_repo "$runner" bash "$release" "$@"
	rc=$?
	if grep -qE '^(gh release (create|upload|edit)|dotnet)' "$tmp/calls" 2>/dev/null; then
		echo "SIDE EFFECT:"
		calls
		return 0
	fi
	return "$rc"
}
none() { env -u GITHUB_EVENT_NAME -u GITHUB_REF -u GITHUB_REF_TYPE NUGET_USER=someone NUGET_API_KEY=secret "$@"; }
dispatch() { as_dispatch env NUGET_API_KEY=secret "$@"; }
dispatch_on_tag() { env GITHUB_EVENT_NAME=workflow_dispatch GITHUB_REF_TYPE=tag GITHUB_REF=refs/tags/v1.2.3 NUGET_USER=someone NUGET_API_KEY=secret "$@"; }
push_other() { as_push v1.0.0-rc.1 env NUGET_API_KEY=secret "$@"; }
push_branch() { env GITHUB_EVENT_NAME=push GITHUB_REF_TYPE=branch GITHUB_REF=refs/heads/main NUGET_USER=someone NUGET_API_KEY=secret "$@"; }
push_branch_named_like_tag() { env GITHUB_EVENT_NAME=push GITHUB_REF_TYPE=branch GITHUB_REF=refs/tags/v1.2.3 NUGET_USER=someone NUGET_API_KEY=secret "$@"; }
push_placeholder() { as_push "v$version" env NUGET_USER=REPLACE-WITH-NUGET-PROFILE-NAME NUGET_API_KEY=secret "$@"; }
push_no_user() { as_push "v$version" env -u NUGET_USER NUGET_API_KEY=secret "$@"; }
for sub in "push v$version $good 1" "push v$version $good 2" "draft v$version $tmp/assets" "publish v$version $tmp/assets"; do
	read -r -a args <<<"$sub"
	what="${args[0]}${args[3]:+ ${args[3]}}"
	expect fail "only in a push of the tag v1.2.3" "$what: refused in a rehearsal (workflow_dispatch)" refused dispatch "${args[@]}"
	expect fail "only in a push of the tag v1.2.3" "$what: refused in a workflow_dispatch on the tag's ref" refused dispatch_on_tag "${args[@]}"
	expect fail "only in a push of the tag v1.2.3" "$what: refused outside GitHub Actions" refused none "${args[@]}"
	expect fail "only in a push of the tag v1.2.3" "$what: refused in a push of another tag" refused push_other "${args[@]}"
	expect fail "only in a push of the tag v1.2.3" "$what: refused in a push to a branch" refused push_branch "${args[@]}"
	expect fail "only in a push of the tag v1.2.3" "$what: refused when the ref is no tag" refused push_branch_named_like_tag "${args[@]}"
	expect fail "still the placeholder" "$what: refused while NUGET_USER is the placeholder" refused push_placeholder "${args[@]}"
	expect fail "still the placeholder" "$what: refused without NUGET_USER" refused push_no_user "${args[@]}"
done
push_without_key() { as_push "v$version" env -u NUGET_API_KEY "$@"; }
expect fail "no NUGET_API_KEY" "push: refused without an API key" refused push_without_key push "v$version" "$good" 1

# --- baseline (ADR 0055) ----------------------------------------------------------------------------------------

# A history of its own: a, b, ..., with the tags each case adds.
history="$tmp/history"
git_q init -b main "$history"
commit() { git_q -C "$history" commit --allow-empty -m "$1"; }
tag() { git_q -C "$history" tag "$@"; }
# baseline_is version want: the baseline, exactly, which may be nothing.
baseline_is() {
	local got
	got="$(cd "$history" && bash "$release" baseline "$1")" || return
	[ "$got" = "$2" ] || { echo "the baseline of $1 is \"$got\", not \"$2\""; return 1; }
}

commit a
expect pass "" "baseline: none before any tag" baseline_is 0.0.0-alpha.0.3 ""
expect pass "" "baseline: none for 1.0.0's release candidate" baseline_is 1.0.0-rc.1 ""
expect fail "no stable v1 release below 1.0.1-alpha.0.1" "baseline: a version past a major's first release without a release of that major fails, as a clone without the tags would" baseline_is 1.0.1-alpha.0.1 ""
expect fail "no stable v1 release below 1.1.0" "baseline: so does a stable version past it" baseline_is 1.1.0 ""
tag v0.0.0-rehearsal.7
expect pass "" "baseline: none for a rehearsal before any stable release" baseline_is 0.0.0-rehearsal.7 ""
tag v0.9.0
commit b
expect pass "" "baseline: none before 1.0, though 0.9.0 is published" baseline_is 0.9.1-alpha.0.1 ""
tag v1.0.0
expect pass "" "baseline: none for 1.0.0 itself" baseline_is 1.0.0 ""
commit c
expect pass "" "baseline: 1.0.0 after it" baseline_is 1.0.1-alpha.0.1 1.0.0
expect pass "" "baseline: a rehearsal compares with the last stable release, as the next one will" baseline_is 0.0.0-rehearsal.8 1.0.0
tag v1.1.0-rc.1
commit d
expect pass "" "baseline: a release candidate compares with the last stable release" baseline_is 1.1.0-rc.1.1 1.0.0
expect pass "" "baseline: a rehearsal after a release candidate compares with what its release will" baseline_is 0.0.0-rehearsal.9 1.0.0
tag v1.1.0
tag v1.10.0-x
tag v1.0.5
commit e
expect pass "" "baseline: the highest stable release below, by version, not by tag order" baseline_is 1.1.1-alpha.0.1 1.1.0
expect pass "" "baseline: a release's own build compares with the release below it" baseline_is 1.1.0 1.0.5
tag v1.3.0-rc.1
expect pass "" "baseline: never a pre-release" baseline_is 1.4.0-alpha.0.1 1.1.0
tag v1.2.0
expect pass "" "baseline: never a release above the version" baseline_is 1.1.1-alpha.0.1 1.1.0
tag v1.9.0
tag v1.10.0
expect pass "" "baseline: versions compare by number, so 1.9.0 is below 1.10.0" baseline_is 1.10.0 1.9.0
expect pass "" "baseline: a rehearsal compares with the highest stable release, by version" baseline_is 0.0.0-rehearsal.10 1.10.0
tag v2.0.0-rc.1
expect pass "" "baseline: a rehearsal after a new major's pre-release compares with nothing, as that major's first release will" baseline_is 0.0.0-rehearsal.11 ""
expect fail "no stable v2 release below 2.0.1-alpha.0.1" "baseline: a new major's version past its first release fails before that release" baseline_is 2.0.1-alpha.0.1 ""
tag v2.0.0
expect pass "" "baseline: none for a new major's first release" baseline_is 2.0.0 ""
commit f
expect pass "" "baseline: the new major's release after it" baseline_is 2.0.1-alpha.0.1 2.0.0
expect pass "" "baseline: a rehearsal after the new major's release compares with it" baseline_is 0.0.0-rehearsal.12 2.0.0
git_q clone --depth 1 --branch main "file://$history" "$tmp/shallow"
shallow_baseline() { (cd "$tmp/shallow" && bash "$release" baseline 2.0.1-alpha.0.1); }
expect fail "shallow" "baseline: a shallow clone fails, since it may lack the baseline's tag" shallow_baseline
shallow_fallback_baseline() { (cd "$tmp/shallow" && bash "$release" baseline 0.0.0-alpha.0); }
expect fail "shallow" "baseline: MinVer's fallback version cannot hide a shallow clone's missing stable tags" shallow_fallback_baseline
git_q -C "$history" checkout -b hotfix main~4
commit hotfix
tag v1.0.1
git_q -C "$history" checkout --detach main~3
expect pass "" "baseline: never a release of another branch, only the tags HEAD's history holds" baseline_is 1.0.2-alpha.0.2 1.0.0

# --- shipped-api (ADR 0055) -------------------------------------------------------------------------------------

# A repository of its own, whose working tree each case edits: shipped-api reads the files as they are.
api="$tmp/api"
git_q init -b main "$api"
api_file() { # package line...
	mkdir -p "$api/dotnet/src/$1"
	printf '%s\n' "${@:2}" >"$api/dotnet/src/$1/PublicAPI.Shipped.txt"
}
api_commit() { git_q -C "$api" add -A && git_q -C "$api" commit -m "$1"; }
shipped() { (cd "$api" && bash "$release" shipped-api); }
raw='[WMN1001]A.B.Raw.get -> Whatsmeow.Proto.X.M?'

api_file A '#nullable enable' 'A.B.C() -> void' 'A.B.D.get -> int' "$raw"
api_file H '#nullable enable' 'H.I() -> void'
mkdir -p "$api/dotnet/src/A/Sub"
api_commit one
expect pass "no stable release" "shipped-api: nothing to keep before a stable release" shipped
git_q -C "$api" tag v1.0.0
expect pass "every member v1.0.0 shipped" "shipped-api: the released files as they were" shipped
api_file A '#nullable enable' 'A.B.C() -> void' 'A.B.E() -> void' 'A.B.D.get -> int' "$raw"
expect pass "every member v1.0.0 shipped" "shipped-api: a new member" shipped
printf '%s\r\n' '#nullable enable' 'A.B.C() -> void' 'A.B.D.get -> int' "$raw" >"$api/dotnet/src/A/PublicAPI.Shipped.txt"
expect pass "every member v1.0.0 shipped" "shipped-api: line endings do not matter" shipped
api_file A '#nullable enable' 'A.B.C() -> void' "$raw"
expect fail "A.B.D.get -> int" "shipped-api: a member dropped from PublicAPI.Shipped.txt fails" shipped
api_file A '#nullable enable' 'A.B.C() -> void' 'A.B.D.get -> long' "$raw"
expect fail "A.B.D.get -> int" "shipped-api: a changed member fails" shipped
api_file A '#nullable enable' 'A.B.C() -> void' 'A.B.D.get -> int'
expect pass "every member v1.0.0 shipped" "shipped-api: the [WMN1001] Raw hatch is outside the promise" shipped
rm -r "$api/dotnet/src/H"
expect fail "H.I() -> void" "shipped-api: a package's file deleted fails" shipped
api_commit two
git_q -C "$api" tag v1.0.1-rc.1
expect fail "H.I() -> void" "shipped-api: a pre-release of the same major keeps the promise" shipped
git_q -C "$api" tag v2.0.0-rc.1
expect pass "no stable release" "shipped-api: a new major's pre-release lifts the last major's promise" shipped
git_q clone --depth 1 --branch main "file://$api" "$tmp/api-shallow"
shallow_shipped() { (cd "$tmp/api-shallow" && bash "$release" shipped-api); }
expect fail "shallow" "shipped-api: a shallow clone fails" shallow_shipped

# --- ids -------------------------------------------------------------------------------------------------------

ids_line() { bash "$release" ids | paste -sd' ' -; }
expect pass "Whatsmeow.Sidecar.Native.Linux Whatsmeow.Sidecar.Native.MacOS Whatsmeow.Sidecar.Native.Windows Whatsmeow.Protocol.WhatsApp Whatsmeow.Protocol Whatsmeow.Client Whatsmeow.Extensions.Hosting Whatsmeow.Testing" \
	"ids: the eight packages, phase 1 then phase 2" ids_line
expect fail "every package id a release publishes" "ids: takes no argument, and prints the usage" bash "$release" ids extra

# --- release.yml wiring (ADR 0064) -----------------------------------------------------------------------------

# The workflow's text, not GitHub's evaluation of it: a rehearsal may take a candidate version, the guard exports the tag
# it guarded, packaging builds at that tag, and only a tag push publishes. Exact lines, so a reformat fails loudly.
workflow="$root/.github/workflows/release.yml"
# block KEY: the lines of the key at two spaces (a job, or workflow_dispatch under on:), up to the next line indented two
# spaces or less.
block() {
	awk -v key="  $1:" '
		$0 == key { inside = 1; print; next }
		inside && $0 ~ /[^ ]/ { match($0, /^ */); if (RLENGTH <= 2) exit }
		inside { print }
	' "$workflow"
}
# once NAME TEXT LINE...: each LINE occurs in TEXT exactly once.
once() {
	local name="$1" text="$2" line rc=0
	shift 2
	for line in "$@"; do
		[ "$(grep -cxF -- "$line" <<<"$text")" -eq 1 ] || { echo "not exactly once in $name: $line"; rc=1; }
	done
	return "$rc"
}
tag_line="  TAG: \${{ github.event_name == 'push' && github.ref_name || (inputs.version != '' && format('v{0}', inputs.version) || format('v0.0.0-rehearsal.{0}', github.run_number)) }}"
# The workflow's own lines, so ${{ }} and $TAG stay text.
# shellcheck disable=SC2016
guard_step='        run: bash build/release.sh guard "$TAG"'
# shellcheck disable=SC2016
export_step='        run: echo "tag=$TAG" >>"$GITHUB_OUTPUT"'
# shellcheck disable=SC2016
output_line='      tag: ${{ steps.tag.outputs.tag }}'
packaging_tag="      rehearsal-tag: \${{ github.event_name == 'workflow_dispatch' && needs.guard.outputs.tag || '' }}"

dispatch_takes_version() {
	once workflow_dispatch "$(block workflow_dispatch)" '    inputs:' '      version:' '        type: string' '        default: ""'
}
tag_chosen() { once release.yml "$(cat "$workflow")" "$tag_line"; }
guard_exports() {
	local text guarded exported
	text="$(block guard)"
	once guard "$text" '    outputs:' "$output_line" '        id: tag' "$export_step" "$guard_step" || return
	guarded="$(grep -nxF -- "$guard_step" <<<"$text" | cut -d: -f1)"
	exported="$(grep -nxF -- '        id: tag' <<<"$text" | cut -d: -f1)"
	[ "$exported" -gt "$guarded" ] || { echo "the step id: tag (line $exported) comes before the guard (line $guarded)"; return 1; }
}
packaging_at_guarded() { once packaging "$(block packaging)" "$packaging_tag"; }
rehearsal_tag_once() {
	local count
	count="$(grep -oF 'v0.0.0-rehearsal.' "$workflow" | wc -l | tr -d ' ')"
	[ "$count" -eq 1 ] || { echo "v0.0.0-rehearsal. occurs $count times in release.yml"; return 1; }
}
only_push_publishes() {
	local push="    if: github.event_name == 'push'" rc=0
	once publish "$(block publish)" "$push" '    environment: release' '      id-token: write' || rc=1
	once nuget-install "$(block nuget-install)" "$push" || rc=1
	[ "$(grep -cE '^ *(environment|id-token):' "$workflow")" -eq 2 ] || { echo "environment or id-token outside publish"; rc=1; }
	return "$rc"
}
expect pass "" "workflow: workflow_dispatch takes an optional version" dispatch_takes_version
expect pass "" "workflow: TAG is the push's tag, the candidate's, or the rehearsal's" tag_chosen
expect pass "" "workflow: guard exports the tag it guarded" guard_exports
expect pass "" "workflow: packaging builds at the guarded tag" packaging_at_guarded
expect pass "" "workflow: the rehearsal tag is chosen in one place" rehearsal_tag_once
expect pass "" "workflow: only a tag push publishes" only_push_publishes

if [ "${#failed[@]}" -gt 0 ]; then
	echo
	echo "failed cases:"
	printf '  %s\n' "${failed[@]}"
	exit 1
fi
echo "all release.sh cases passed"
