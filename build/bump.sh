#!/usr/bin/env bash
# The weekly whatsmeow bump (#48, ADR 0043); .github/workflows/whatsmeow-bump.yml calls this and nothing else.
#
# It pins whatsmeow's latest main, copies its protos, regenerates the C# and the licence notices, and opens the one
# bump PR, or adds a commit to it when it is open. It never builds or tests: the PR's ci run does, so a bump that
# breaks the build still opens, red, for a person to fix in it. The PR body lists what a reviewer re-checks
# (tools/bump-report), the non-gating WIRE diff of the protos, and the licence findings that keep CI red.
#
# Locally, bash build/bump.sh --dry-run does all of that in the working tree and prints the PR body instead of
# committing, pushing and opening the PR.
set -uo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$root" || exit 1

# Use exactly the Go that mise installs from mise.toml, as build/ci.sh does.
export GOTOOLCHAIN=local

module=go.mau.fi/whatsmeow
branch=whatsmeow-bump
max_lines=200 # the most lines of a tool's output the body carries

dry_run=false
case "${1:-}" in
--dry-run) dry_run=true ;;
"") ;;
*)
	echo "usage: bash build/bump.sh [--dry-run]" >&2
	exit 2
	;;
esac

failed=()

# step NAME COMMAND...: run one step and keep going; the PR body names the ones that failed.
step() {
	local name="$1"
	shift
	echo "==> $name"
	if ! "$@"; then
		echo "failed: $name"
		failed+=("$name")
		return 1
	fi
}

# resolve QUERY: prints "<version> <h1 sum>" of whatsmeow at QUERY. A version whose go line is above the local Go
# still prints both, beside an error the go command exits 1 with [observed: go 1.27.3].
resolve() {
	local scratch json
	scratch="$(mktemp -d)"
	json="$(cd "$scratch" && go mod download -json "$module@$1")"
	rm -rf "$scratch"
	jq -r 'select(.Version != null and .Sum != null) | "\(.Version) \(.Sum)"' <<<"$json"
}

version_of() { # VERSION file content
	cut -d' ' -f2 <<<"$1"
}

# go get refuses a version whose go line is above the local Go and leaves go.mod as it was [observed: go 1.27.3]; the
# require is then written by hand, so the PR still pins the version, and CI fails on its go.sum.
pin_module() {
	if go -C sidecar get "$module@$version" && go -C sidecar mod tidy; then
		return 0
	fi
	go -C sidecar mod edit -require="$module@$version"
	return 1
}

# wire_diff: buf's WIRE rules on the protos on main against the copied ones. buf exits 100 when it found a break.
wire_diff() {
	local scratch rc
	scratch="$(mktemp -d)"
	mkdir "$scratch/old" "$scratch/new"
	git archive origin/main third_party/whatsmeow | tar -x -C "$scratch/old" --strip-components 2
	cp -R third_party/whatsmeow/. "$scratch/new"
	(cd "$scratch" && buf breaking new --against old --config '{"version":"v1","breaking":{"use":["WIRE"]}}') >"$scratch/out" 2>&1
	rc=$?
	# Each finding without buf's link to the protobuf docs, so the body stays short.
	wire="$(sed -e 's| See https://.*$||' -e 's|^new/||' "$scratch/out")"
	rm -rf "$scratch"
	[ "$rc" -eq 0 ] || [ "$rc" -eq 100 ]
}

licences() {
	local rc
	licence_report="$(go -C tools run ./check-licenses -write 2>&1)"
	rc=$?
	echo "$licence_report"
	return "$rc"
}

git fetch --quiet origin main || exit 1
main_version="$(version_of "$(git show origin/main:third_party/whatsmeow/VERSION)")"

read -r version sum < <(resolve main)
if [ -z "${version:-}" ] || [ -z "${sum:-}" ]; then
	echo "could not resolve $module@main" >&2
	exit 1
fi

if [ "$version" = "$main_version" ]; then
	echo "main already pins $module $version: nothing to bump"
	exit 0
fi

if [ "$dry_run" = false ]; then
	if git ls-remote --exit-code --heads origin "$branch" >/dev/null; then
		# The open bump PR may carry a person's fixes: add to it, never rewrite it.
		git fetch --quiet origin "$branch" && git switch --quiet -C "$branch" FETCH_HEAD || exit 1
	else
		git switch --quiet -C "$branch" origin/main || exit 1
	fi

	if [ "$(version_of "$(cat third_party/whatsmeow/VERSION)")" = "$version" ]; then
		echo "the $branch branch already pins $module $version: nothing to bump"
		exit 0
	fi
fi

echo "$module $version $sum" >third_party/whatsmeow/VERSION
step "sidecar/go.mod requires $module $version (go get, go mod tidy)" pin_module
synced=false
step "third_party/whatsmeow copies the pinned protos (sync-protos -write)" go -C tools run ./sync-protos -write && synced=true
step "buf generate --template buf.gen.whatsapp.yaml" buf generate --template buf.gen.whatsapp.yaml
licensed=false
step "sidecar/THIRD-PARTY-NOTICES.txt from the allowlist (check-licenses -write)" licences && licensed=true
if [ "$synced" = true ]; then
	step "the WIRE diff (buf breaking)" wire_diff
fi
report="$(go -C tools run ./bump-report -from "$main_version" -to "$version")" || failed+=("tools/bump-report")

body="$(mktemp)"
{
	echo "$report"
	echo
	echo "### Wire diff (non-gating)"
	echo
	if [ "$synced" = false ]; then
		echo "Not computed: the protos were not copied (below)."
	elif [ -z "${wire:-}" ]; then
		echo "buf's WIRE rules find no change on the wire between main's protos and these."
	else
		echo "buf's WIRE rules against main's protos, $(wc -l <<<"$wire" | tr -d ' ') findings; the first $max_lines:"
		echo
		echo '```'
		head -n "$max_lines" <<<"$wire"
		echo '```'
	fi
	echo
	echo "### Licences"
	echo
	if [ "$licensed" = true ]; then
		echo "Every linked module's licence files match sidecar/licenses.allow."
	else
		echo "check-licenses failed, and CI fails with it until it passes: a new module or a changed licence file waits for a reviewer to read the licence and add its line to sidecar/licenses.allow (ADR 0016). The first $max_lines lines:"
		echo
		echo '```'
		head -n "$max_lines" <<<"$licence_report"
		echo '```'
	fi
	if [ "${#failed[@]}" -gt 0 ]; then
		echo
		echo "### Steps that failed"
		echo
		printf -- '- %s\n' "${failed[@]}"
	fi
	echo
	echo "---"
	echo "Opened by the weekly whatsmeow bump (build/bump.sh, ADR 0043). CI runs once someone with write access approves the workflows."
	echo "Merge only after a review re-checks every fact listed above, reads the commit range for facts no changed file names, and moves ARCHITECTURE §14's pin; never automatically."
} >"$body"

if [ "$dry_run" = true ]; then
	echo
	echo "--- dry run: the PR body ---"
	cat "$body"
	echo "--- dry run: the tree ---"
	git status --short
	rm -f "$body"
	exit 0
fi

title="build: bump whatsmeow to $version"
git add -A -- sidecar/go.mod sidecar/go.sum sidecar/THIRD-PARTY-NOTICES.txt third_party/whatsmeow dotnet/src/Whatsmeow.Protocol.WhatsApp/Generated &&
	git -c user.name="github-actions[bot]" -c user.email="41898282+github-actions[bot]@users.noreply.github.com" commit --quiet -m "$title" &&
	git push --quiet origin "$branch" || exit 1

number="$(gh pr list --head "$branch" --base main --state open --json number --jq '.[0].number // empty')" || exit 1
if [ -z "$number" ]; then
	gh pr create --base main --head "$branch" --title "$title" --body-file "$body" || exit 1
else
	gh pr edit "$number" --title "$title" --body-file "$body" || exit 1
fi
rm -f "$body"
