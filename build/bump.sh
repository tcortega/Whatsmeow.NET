#!/usr/bin/env bash
# The weekly whatsmeow bump (#48, ADR 0043); .github/workflows/whatsmeow-bump.yml calls this and nothing else.
#
# It pins whatsmeow's latest main, copies its protos, and regenerates the C# and the licence notices. When a bump PR
# is open it adds a commit to that PR's branch; otherwise it cuts whatsmeow-bump/<commit> from main and opens the PR.
# It never force-pushes or deletes a branch. It never builds or tests: the PR's ci run does, so a bump that breaks
# the build still opens, red, for a person to fix in it. The PR body lists what a reviewer re-checks
# (tools/bump-report), the non-gating WIRE diff of the protos, and the licence findings that keep CI red, cut by
# section to fit GitHub's limit; the full body is in the log.
#
# Locally, bash build/bump.sh --dry-run does all of that in the working tree and prints the PR body instead of
# committing, pushing and opening the PR.
set -uo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$root" || exit 1

# Use exactly the Go that mise installs from mise.toml, as build/ci.sh does.
export GOTOOLCHAIN=local

module=go.mau.fi/whatsmeow
prefix=whatsmeow-bump/
# GitHub refuses a PR body over 65,536 characters; this leaves room for the notes.
limit=60000

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

commit_of() { # pseudo-version: its commit, the last dash-separated part
	echo "${1##*-}"
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

# wire_diff: buf's WIRE rules on the protos on main against the copied ones. buf exits 100 when it found a break, and
# anything else but 0 when it could not run.
wire_diff() {
	local scratch
	scratch="$(mktemp -d)"
	mkdir "$scratch/old" "$scratch/new"
	git archive origin/main third_party/whatsmeow | tar -x -C "$scratch/old" --strip-components 2
	cp -R third_party/whatsmeow/. "$scratch/new"
	(cd "$scratch" && buf breaking new --against old --config '{"version":"v1","breaking":{"use":["WIRE"]}}') >"$scratch/out" 2>&1
	wire_rc=$?
	# Each finding without buf's link to the protobuf docs, so the body stays short.
	wire="$(sed -e 's| See https://.*$||' -e 's|^new/||' "$scratch/out")"
	rm -rf "$scratch"
	[ "$wire_rc" -eq 0 ] || [ "$wire_rc" -eq 100 ]
}

# fit TEXT BUDGET: TEXT when it fits in BUDGET characters, else its first whole lines that fit with the note after them.
fit() {
	local text="$1" budget="$2" out="" line
	if [ "${#text}" -le "$budget" ]; then
		printf '%s' "$text"
		return
	fi
	budget=$((budget - ${#note} - 1))
	while IFS= read -r line; do
		[ $((${#out} + ${#line} + 1)) -le "$budget" ] || break
		out+="$line"$'\n'
	done <<<"$text"
	printf '%s%s' "$out" "$note"
}

fenced() { # TEXT
	local fence='```'
	printf '%s\n%s\n%s\n' "$fence" "$1" "$fence"
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

number=""
if [ "$dry_run" = false ]; then
	# A PR lands on main as new commits (linear history) and its branch stays, so only an open PR's branch is built
	# on; any other bump starts from main, on a branch of its own.
	open="$(gh pr list --base main --state open --json number,headRefName \
		--jq ".[] | select(.headRefName | startswith(\"$prefix\")) | \"\(.number) \(.headRefName)\"" | head -n 1)" || exit 1
	if [ -n "$open" ]; then
		read -r number branch <<<"$open"
	else
		branch="$prefix$(commit_of "$version")"
	fi

	if git ls-remote --exit-code --heads origin "$branch" >/dev/null; then
		# An open PR's branch may carry a person's fixes, and a branch with no PR is a push whose PR was never opened:
		# add to it, never rewrite it.
		git fetch --quiet origin "$branch" && git switch --quiet -C "$branch" FETCH_HEAD || exit 1
	else
		git switch --quiet -C "$branch" origin/main || exit 1
	fi

	if [ -n "$number" ] && [ "$(version_of "$(cat third_party/whatsmeow/VERSION)")" = "$version" ]; then
		echo "PR #$number already pins $module $version: nothing to bump"
		exit 0
	fi
fi

echo "$module $version $sum" >third_party/whatsmeow/VERSION
pinned=false
step "sidecar/go.mod requires $module $version (go get, go mod tidy)" pin_module && pinned=true
synced=false
step "third_party/whatsmeow copies the pinned protos (sync-protos -write)" go -C tools run ./sync-protos -write && synced=true
step "buf generate --template buf.gen.whatsapp.yaml" buf generate --template buf.gen.whatsapp.yaml
licensed=false
step "sidecar/THIRD-PARTY-NOTICES.txt from the allowlist (check-licenses -write)" licences && licensed=true
if [ "$synced" = true ]; then
	step "the WIRE diff (buf breaking)" wire_diff
fi
report="$(go -C tools run ./bump-report -from "$main_version" -to "$version")" || failed+=("tools/bump-report")

log="the bump's log"
if [ -n "${GITHUB_RUN_ID:-}" ]; then
	log="the log of [this run](${GITHUB_SERVER_URL}/${GITHUB_REPOSITORY}/actions/runs/${GITHUB_RUN_ID})"
fi
note="(cut to fit GitHub's limit on a PR body; the whole report is in $log)"

# The sections that can be long, each without its heading and fence.
wire_text=""
licence_text=""
if [ "$synced" = false ]; then
	wire_head="Not computed: the protos were not copied (Steps that failed)."
elif [ "${wire_rc:-0}" -ne 0 ] && [ "${wire_rc:-0}" -ne 100 ]; then
	wire_head="buf breaking errored (exit $wire_rc), so there is no WIRE diff:"
	wire_text="$wire"
elif [ -z "${wire:-}" ]; then
	wire_head="buf's WIRE rules find no change on the wire between main's protos and these."
else
	wire_head="buf's WIRE rules against main's protos, $(wc -l <<<"$wire" | tr -d ' ') findings:"
	wire_text="$wire"
fi
if [ "$pinned" = false ]; then
	licence_head="Not checked: sidecar/go.mod could not require the new version (Steps that failed), so the linked modules cannot be listed."
elif [ "$licensed" = true ]; then
	licence_head="Every linked module's licence files match sidecar/licenses.allow."
else
	licence_head="check-licenses failed, and CI fails with it until it passes: a new module or a changed licence file waits for a reviewer to read the licence and add its line to sidecar/licenses.allow (ADR 0016)."
	licence_text="$licence_report"
fi
failed_text=""
if [ "${#failed[@]}" -gt 0 ]; then
	failed_text="$(printf '\n### Steps that failed\n\n'; printf -- '- %s\n' "${failed[@]}")"
fi
footer="---
Opened by the weekly whatsmeow bump (build/bump.sh, ADR 0043). CI runs once someone with write access approves the workflows.
Merge only after a review re-checks every fact listed above, reads the commit range for facts no changed file names, and moves ARCHITECTURE §14's pin; never automatically."

# body REPORT WIRE LICENCES: the PR body with these three sections.
body() {
	printf '%s\n\n### Wire diff (non-gating)\n\n%s\n' "$1" "$wire_head"
	[ -z "$2" ] || printf '\n%s\n' "$(fenced "$2")"
	printf '\n### Licences\n\n%s\n' "$licence_head"
	[ -z "$3" ] || printf '\n%s\n' "$(fenced "$3")"
	[ -z "$failed_text" ] || printf '%s\n' "$failed_text"
	printf '\n%s\n' "$footer"
}

full="$(body "$report" "$wire_text" "$licence_text")"
# Cut the WIRE diff first, then the licence findings, then the report, until the body fits.
over=$((${#full} - limit))
for section in wire_text licence_text report; do
	[ "$over" -gt 0 ] || break
	text="${!section}"
	[ -n "$text" ] || continue
	shorter="$(fit "$text" $((${#text} - over)))"
	over=$((over - ${#text} + ${#shorter}))
	printf -v "$section" '%s' "$shorter"
done
pr_body="$(mktemp)"
body "$report" "$wire_text" "$licence_text" >"$pr_body"

if [ "$dry_run" = true ]; then
	echo
	echo "--- dry run: the PR body ($(wc -c <"$pr_body" | tr -d ' ') bytes) ---"
	cat "$pr_body"
	echo "--- dry run: the tree ---"
	git status --short
	rm -f "$pr_body"
	exit 0
fi

echo "--- the whole PR body ---"
echo "$full"

title="build: bump whatsmeow to $version"
git add -A -- sidecar/go.mod sidecar/go.sum sidecar/THIRD-PARTY-NOTICES.txt third_party/whatsmeow dotnet/src/Whatsmeow.Protocol.WhatsApp/Generated || exit 1
if ! git diff --cached --quiet; then
	git -c user.name="github-actions[bot]" -c user.email="41898282+github-actions[bot]@users.noreply.github.com" commit --quiet -m "$title" || exit 1
fi
git push --quiet origin "$branch" || exit 1

if [ -z "$number" ]; then
	gh pr create --base main --head "$branch" --title "$title" --body-file "$pr_body" || exit 1
else
	gh pr edit "$number" --title "$title" --body-file "$pr_body" || exit 1
fi
rm -f "$pr_body"
