#!/usr/bin/env bash
# Tests for build/release.sh (#46, ADR 0050): every guard and every verify check, each case run in a git repository
# and a feed of its own under a temporary directory, with a local bare repository as origin. Nothing here reaches
# GitHub or nuget.org: no case pushes beyond the temporary origin, and the publishing commands run only to be refused.
# build/ci.sh contract runs it; locally, bash build/release-test.sh.
set -uo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
release="$root/build/release.sh"
tmp="$(mktemp -d)"
trap 'rm -rf "$tmp"' EXIT

failed=()

# expect pass|fail MESSAGE NAME COMMAND...: run COMMAND, which must pass, or fail with MESSAGE in its output, so a
# case proves the check it names and not another one that happens to fail first.
expect() {
	local want="$1" message="$2" name="$3" out rc line
	shift 3
	out="$("$@" 2>&1)"
	rc=$?
	if [ "$want" = pass ] && [ "$rc" -eq 0 ]; then echo "ok: $name"; return; fi
	if [ "$want" = fail ] && [ "$rc" -ne 0 ] && grep -qF -- "$message" <<<"$out"; then echo "ok: $name"; return; fi
	echo "FAILED: $name (wanted $want${message:+ with \"$message\"}, exit $rc):"
	while IFS= read -r line; do echo "    $line"; done <<<"$out"
	failed+=("$name")
}

# The environment of a tag push in GitHub Actions, and of a rehearsal.
as_push() { # tag command...
	local tag="$1"
	shift
	env GITHUB_EVENT_NAME=push GITHUB_REF_TYPE=tag GITHUB_REF="refs/tags/$tag" NUGET_USER=someone "$@"
}
as_dispatch() {
	env GITHUB_EVENT_NAME=workflow_dispatch GITHUB_REF_TYPE=branch GITHUB_REF=refs/heads/main NUGET_USER=someone "$@"
}

# --- guard -----------------------------------------------------------------------------------------------------

git_q() { git -c user.name=test -c user.email=test@example.com -c commit.gpgsign=false -c tag.gpgsign=false "$@" >/dev/null 2>&1; }

repo="$tmp/repo"
git init -q --bare -b main "$tmp/origin.git"
git clone -q "$tmp/origin.git" "$repo" 2>/dev/null
cp "$release" "$repo/release.sh"
(
	cd "$repo" || exit 1
	git_q checkout -b main
	git_q commit --allow-empty -m one
	git_q push origin main
	git_q checkout -b side
	git_q commit --allow-empty -m off-main
	git_q checkout main
) || exit 1

guard() { (cd "$repo" && unset GITHUB_EVENT_NAME && bash release.sh guard "$@"); }
at() { git_q -C "$repo" checkout --detach "$1"; }
tag_at() { git_q -C "$repo" tag -f "$1" "$2"; }

tag_at v1.2.3 main
tag_at v1.0.0-rc.1 main
tag_at v2.0.0 side
tag_at vfoo main
tag_at v1.2 main
tag_at v1.2.3+build.5 main
tag_at v01.2.3 main

at main
expect pass "" "guard: a v<semver> tag at HEAD on main" guard v1.2.3
expect pass "" "guard: a pre-release tag at HEAD on main" guard v1.0.0-rc.1
expect fail "is not a release tag" "guard: vfoo is no release tag" guard vfoo
expect fail "is not a release tag" "guard: v1.2 is no release tag" guard v1.2
expect fail "is not a release tag" "guard: a leading zero is no SemVer" guard v01.2.3
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
git_q -C "$repo" fetch origin "$(cat "$tmp/two")"
at "$(cat "$tmp/two")"
tag_at v1.3.0 HEAD
expect pass "" "guard: a commit main reached after the clone (the guard fetches main)" guard v1.3.0
git_q -C "$repo" update-ref -d refs/remotes/origin/main
expect pass "" "guard: no origin/main ref in the clone at all" guard v1.3.0

# A tag push also needs the nuget.org profile name, before anything is built.
at main
push_guard() { (cd "$repo" && as_push v1.2.3 bash release.sh guard v1.2.3); }
placeholder_guard() { (cd "$repo" && as_push v1.2.3 env NUGET_USER=REPLACE-WITH-NUGET-PROFILE-NAME bash release.sh guard v1.2.3); }
other_ref_guard() { (cd "$repo" && as_push v1.0.0-rc.1 bash release.sh guard v1.2.3); }
expect pass "" "guard: a push of the tag, with a profile name" push_guard
expect fail "still the placeholder" "guard: a push of the tag while NUGET_USER is the placeholder" placeholder_guard
expect fail "only in a push of the tag v1.2.3" "guard: a push of another tag than the one guarded" other_ref_guard

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

# --- the order, and publishing only in a tag push --------------------------------------------------------------

push_order() {
	local out
	out="$(bash "$release" push "v$version" "$good" 1 --dry-run && bash "$release" push "v$version" "$good" 2 --dry-run)" || return 1
	echo "$out"
	[ "$(grep -o 'Whatsmeow\.[A-Za-z.]*\.1\.2\.3\.nupkg' <<<"$out" | tr '\n' ' ')" = \
		"Whatsmeow.Sidecar.Native.Linux.1.2.3.nupkg Whatsmeow.Sidecar.Native.MacOS.1.2.3.nupkg Whatsmeow.Sidecar.Native.Windows.1.2.3.nupkg Whatsmeow.Protocol.WhatsApp.1.2.3.nupkg Whatsmeow.Protocol.1.2.3.nupkg Whatsmeow.Client.1.2.3.nupkg Whatsmeow.Extensions.Hosting.1.2.3.nupkg Whatsmeow.Testing.1.2.3.nupkg " ] &&
		grep -qF -- '--skip-duplicate' <<<"$out"
}
expect pass "" "push --dry-run: the native packages first, every package once, with --skip-duplicate" push_order

wait_urls() {
	local out
	out="$(bash "$release" wait "v$version" "$good" --dry-run)" || return 1
	echo "$out"
	[ "$(grep -c 'https://api.nuget.org/v3-flatcontainer/whatsmeow\.[a-z.]*/index.json' <<<"$out")" = 4 ] &&
		grep -qF 'whatsmeow.sidecar.native.windows/index.json' <<<"$out"
}
expect pass "" "wait --dry-run: waits for the four first-phase packages" wait_urls

github_plan() {
	local out
	verify "$good" >/dev/null || return 1
	out="$(bash "$release" github "v$version" "$tmp/assets" --dry-run)" || return 1
	echo "$out"
	grep -qF 'draft' <<<"$out" && [ "$(grep -c 'upload' <<<"$out")" = 8 ] && grep -qF 'publish' <<<"$out"
}
expect pass "" "github --dry-run: a draft, eight uploads, then publish" github_plan

prerelease_plan() {
	rm -rf "$tmp/assets"
	mkdir -p "$tmp/assets" && echo x >"$tmp/assets/whatsmeow-net_1.0.0-rc.1_linux-x64"
	local out
	out="$(bash "$release" github v1.0.0-rc.1 "$tmp/assets" --dry-run)" || return 1
	echo "$out"
	grep -qF -- '--prerelease' <<<"$out"
}
expect pass "" "github --dry-run: a pre-release tag makes a pre-release" prerelease_plan

refused() { # name env-runner subcommand args...: a publishing command that must refuse before it does anything
	local runner="$1"
	shift
	PATH="$tmp/no-publish:$PATH" "$runner" bash "$release" "$@"
}
# Stand-ins that fail loudly, so a refusal that came too late shows as the wrong message, never as a publish.
mkdir -p "$tmp/no-publish"
printf '#!/usr/bin/env bash\necho "dotnet ran: $*"\nexit 3\n' >"$tmp/no-publish/dotnet"
printf '#!/usr/bin/env bash\necho "gh ran: $*"\nexit 3\n' >"$tmp/no-publish/gh"
printf '#!/usr/bin/env bash\necho "curl ran: $*"\nexit 3\n' >"$tmp/no-publish/curl"
chmod +x "$tmp/no-publish/"*
verify "$good" >/dev/null
none() { env -u GITHUB_EVENT_NAME -u GITHUB_REF -u GITHUB_REF_TYPE NUGET_USER=someone "$@"; }
push_other() { as_push v1.0.0-rc.1 "$@"; }
push_branch() { env GITHUB_EVENT_NAME=push GITHUB_REF_TYPE=branch GITHUB_REF=refs/heads/main NUGET_USER=someone "$@"; }
push_placeholder() { as_push "v$version" env NUGET_USER=REPLACE-WITH-NUGET-PROFILE-NAME "$@"; }
for sub in "push v$version $good 1" "push v$version $good 2" "github v$version $tmp/assets"; do
	read -r -a args <<<"$sub"
	what="${args[0]}${args[3]:+ ${args[3]}}"
	expect fail "only in a push of the tag v1.2.3" "$what: refused in a rehearsal (workflow_dispatch)" refused as_dispatch "${args[@]}"
	expect fail "only in a push of the tag v1.2.3" "$what: refused outside GitHub Actions" refused none "${args[@]}"
	expect fail "only in a push of the tag v1.2.3" "$what: refused in a push of another tag" refused push_other "${args[@]}"
	expect fail "only in a push of the tag v1.2.3" "$what: refused in a push to a branch" refused push_branch "${args[@]}"
	expect fail "still the placeholder" "$what: refused while NUGET_USER is the placeholder" refused push_placeholder "${args[@]}"
done
push_without_key() { as_push "v$version" env -u NUGET_API_KEY "$@"; }
expect fail "no NUGET_API_KEY" "push: refused without an API key" refused push_without_key push "v$version" "$good" 1

if [ "${#failed[@]}" -gt 0 ]; then
	echo
	echo "failed cases:"
	printf '  %s\n' "${failed[@]}"
	exit 1
fi
echo "all release.sh cases passed"
