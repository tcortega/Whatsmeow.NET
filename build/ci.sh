#!/usr/bin/env bash
# The checks CI runs, one subcommand per CI job; .github/workflows/ci.yml calls this and nothing else.
# Run it before pushing: bash build/ci.sh all (or contract, go, dotnet). Works in Git Bash on Windows.
#
# Every check runs and reports, even after an earlier one failed; the script fails at the end if any did.
# A check that only makes sense once something exists (a Go module with no package yet) says so and passes.
set -uo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$root" || exit 1

# Use exactly the Go that mise installs from mise.toml; never download another toolchain.
export GOTOOLCHAIN=local
export DOTNET_NOLOGO=true DOTNET_CLI_TELEMETRY_OPTOUT=true TESTINGPLATFORM_TELEMETRY_OPTOUT=true

failures=()

annotate() { # level title message
	if [ "${GITHUB_ACTIONS:-}" = true ]; then
		echo "::$1 title=$2::$3"
	else
		echo "$1: $2: $3"
	fi
}

# check NAME COMMAND...: run one check, record a failure, keep going.
check() {
	local name="$1"
	shift
	if [ "${GITHUB_ACTIONS:-}" = true ]; then echo "::group::$name"; else echo "==> $name"; fi
	"$@"
	local rc=$?
	if [ "${GITHUB_ACTIONS:-}" = true ]; then echo "::endgroup::"; fi
	if [ "$rc" -ne 0 ]; then
		annotate error "$name" "failed with exit code $rc"
		failures+=("$name")
	fi
	return "$rc"
}

# A snapshot of the working tree, to prove a job left it as it found it, also with local changes present.
tree_state() {
	git status --porcelain=v1 --untracked-files=all
	git diff HEAD --binary | git hash-object --stdin
	git ls-files --others --exclude-standard -z | xargs -0 -r git hash-object --
}

tree_unchanged() { # before-state what
	local after
	after="$(tree_state)"
	if [ "$1" != "$after" ]; then
		annotate error "tree changed" "$2 changed the working tree"
		git status --porcelain=v1 --untracked-files=all
		git --no-pager diff --stat
		return 1
	fi
}

go_toolchain() {
	local want got
	want="$(sed -n 's/^toolchain //p' sidecar/go.mod)"
	if [ "$(sed -n 's/^toolchain //p' tools/go.mod)" != "$want" ]; then
		annotate error "Go toolchain" "tools/go.mod and sidecar/go.mod pin different toolchains"
		return 1
	fi
	got="$(go env GOVERSION)"
	if [ "$got" != "$want" ]; then
		annotate error "Go toolchain" "go is $got, sidecar/go.mod pins $want; align mise.toml and run mise install"
		return 1
	fi
	echo "go is $got, as sidecar/go.mod pins"
}

buf_breaking() {
	# A pull request compares with its merge-base with main, never with a stacked base branch; a push to main
	# compares with the commit main pointed at before. Locally, like a pull request. workflow_dispatch skips it.
	local event="${CI_EVENT:-local}" base
	case "$event" in
		workflow_dispatch)
			annotate notice "buf breaking" "skipped on workflow_dispatch: there is no base to compare with"
			return 0
			;;
		push) base="${CI_BEFORE:-}" ;;
		*) base="$(git merge-base origin/main HEAD)" || return 1 ;;
	esac

	if [ -z "$base" ] || [ "$base" = 0000000000000000000000000000000000000000 ]; then
		annotate notice "buf breaking" "no base commit to compare with"
		return 0
	fi
	if ! git cat-file -e "$base^{commit}" 2>/dev/null; then
		annotate error "buf breaking" "the base commit $base is not in this clone"
		return 1
	fi
	if ! git cat-file -e "$base:buf.yaml" 2>/dev/null; then
		annotate notice "buf breaking" "the base $base has no buf workspace, so there is nothing to compare with"
		return 0
	fi
	# buf compares the images module by module, and fails as a tool error when the base has fewer modules.
	if ! git cat-file -e "$base:proto/whatsmeow/v1" 2>/dev/null; then
		annotate notice "buf breaking" "the base $base has no whatsmeow.v1 module yet, so there is nothing to compare with"
		return 0
	fi

	# buf breaking also exits 100 when either side does not compile; make that an error with buf's own message.
	buf build || return
	buf build ".git#ref=$base" || { annotate error "buf breaking" "the base $base does not compile"; return 1; }

	# Any published v<semver> tag reachable from the base starts the gate, pre-releases included (ADR 0015).
	local gate=false tag
	while IFS= read -r tag; do
		if [[ "$tag" =~ ^v[0-9]+\.[0-9]+\.[0-9]+(-[0-9A-Za-z.-]+)?(\+[0-9A-Za-z.-]+)?$ ]]; then
			gate=true
		fi
	done < <(git tag --merged "$base" --list 'v*')

	local rc=0
	buf breaking --against ".git#ref=$base" || rc=$?
	case "$rc" in
		0) ;;
		100)
			if [ "$gate" = true ]; then
				annotate error "buf breaking" "whatsmeow.v1 breaks against $base after the first published tag (ADR 0015)"
				return 100
			fi
			annotate warning "buf breaking (report only)" "breaking changes against $base; they gate from the first published v<semver> tag (ADR 0015)"
			;;
		*) return "$rc" ;;
	esac
}

generated_unchanged() { # before-state
	buf generate || return
	buf generate --template buf.gen.whatsapp.yaml || return
	tree_unchanged "$1" "buf generate" || { annotate error "generated code differs" "never edit generated code; run buf generate and commit the result"; return 1; }
}

job_contract() {
	local before
	before="$(tree_state)"
	check "shellcheck build/ci.sh" shellcheck build/ci.sh
	check "third_party/whatsmeow matches the pinned module" go -C tools run ./sync-protos
	check "buf lint" buf lint
	check "buf breaking (gates from the first published v<semver> tag, ADR 0015)" buf_breaking
	check "buf generate leaves the tree unchanged" generated_unchanged "$before"
}

has_packages() { # module
	[ -n "$(go -C "$1" list ./... 2>/dev/null)" ]
}

no_cgo() { # module tags
	local cgo
	cgo="$(CGO_ENABLED=1 go -C "$1" list -tags "$2" -f '{{if .CgoFiles}}{{.ImportPath}} {{.CgoFiles}}{{end}}' ./...)" || return
	if [ -n "$cgo" ]; then
		annotate error "cgo" "$1 must be CGO-free (docs/guidelines/go.md): $cgo"
		return 1
	fi
}

# The release build must not link the fake WhatsApp mode (ADR 0019). The fake package is behind the fakewhatsapp
# tag, but its generated control package is not, so this lists what whatsmeow-net links without the tag.
release_links_no_fake() {
	local deps fake
	deps="$(CGO_ENABLED=0 go -C sidecar list -deps -f '{{.ImportPath}}' ./cmd/whatsmeow-net)" || return
	fake="$(grep -E '/internal/fakewhatsapp$|/gen/whatsmeow/fake/' <<<"$deps")"
	if [ -n "$fake" ]; then
		annotate error "fake in the release build" "whatsmeow-net without -tags fakewhatsapp links $fake"
		return 1
	fi
	echo "whatsmeow-net without -tags fakewhatsapp links no fake package"
}

job_go() {
	local before module tags label tagsets
	before="$(tree_state)"
	for module in tools sidecar; do
		check "$module: go.mod and go.sum are tidy" go -C "$module" mod tidy -diff
		if ! has_packages "$module"; then
			annotate notice "$module" "no Go packages yet; build, test and lint start with the first one"
			continue
		fi
		# A file behind a build tag is invisible to build, test, lint and the cgo check unless the tag is passed, so
		# sidecar, the only module that may use fakewhatsapp (tools/check-repo), runs them once more with it.
		tagsets=("")
		if [ "$module" = sidecar ]; then tagsets+=(fakewhatsapp); fi
		for tags in "${tagsets[@]}"; do
			label="$module${tags:+ (-tags $tags)}"
			check "$label: no cgo files" no_cgo "$module" "$tags"
			check "$label: go build (no cgo)" env CGO_ENABLED=0 go -C "$module" build -tags "$tags" ./...
			check "$label: go test -race" go -C "$module" test -race -count=1 -tags "$tags" ./...
			# With cgo on, so a cgo file is visible (its import "C" is reported as unsafe).
			check "$label: golangci-lint" bash -c "cd '$module' && CGO_ENABLED=1 golangci-lint run --build-tags '$tags' ./..."
		done
	done
	check "sidecar: the release build links no fake WhatsApp (ADR 0019)" release_links_no_fake
	check "sidecar: linked modules and licence files match sidecar/licenses.allow (ADR 0016)" go -C tools run ./check-licenses
	check "the go job left the tree unchanged" tree_unchanged "$before" "the go job"
}

# AotSmoke starts a real whatsmeow-net under Native AOT, so the sidecar is built here at the client's version.
aot_smoke_run() {
	local rid exe="" version sidecar
	rid="$(dotnet msbuild dotnet/tests/AotSmoke/AotSmoke.csproj -getProperty:RuntimeIdentifier)" || return
	case "$rid" in win-*) exe=.exe ;; esac
	version="$(dotnet msbuild dotnet/src/Whatsmeow.Client/Whatsmeow.Client.csproj -getProperty:Version)" || return
	sidecar="dotnet/artifacts/aot-smoke/whatsmeow-net$exe"
	CGO_ENABLED=0 go -C sidecar build -ldflags "-X main.version=$version" -o "../$sidecar" ./cmd/whatsmeow-net || return
	"dotnet/artifacts/publish/AotSmoke/release_$rid/AotSmoke$exe" "$sidecar"
}

# The noexec copy fallback test (ARCHITECTURE §8.5) needs a noexec mount, and only root can make one, so CI makes it
# on Linux; the test fails there without it (docs/guidelines/tests.md). Elsewhere the test is skipped.
noexec_mount() {
	if [ "${GITHUB_ACTIONS:-}" != true ] || [ "$(uname -s)" != Linux ]; then
		echo "not Linux CI: the noexec test is skipped"
		return 0
	fi
	local dir="${RUNNER_TEMP:?}/noexec"
	mkdir -p "$dir" || return
	sudo mount -t tmpfs -o noexec,nosuid,nodev,mode=1777,size=256m tmpfs "$dir" || return
	export WHATSMEOW_NET_TEST_NOEXEC_DIR="$dir"
	echo "noexec tmpfs at $dir"
}

# The fake WhatsApp control's C# is generated into the integration tests only (ADR 0019): no assembly under
# dotnet/src, all of which ship, may contain its service type. The test assembly must, or this grep proves nothing.
shipped_have_no_fake() {
	local project name dll rc=0
	if ! grep -q -a FakeWhatsAppService dotnet/artifacts/bin/Whatsmeow.IntegrationTests/release/Whatsmeow.IntegrationTests.dll; then
		annotate error "fake in a shipped assembly" "Whatsmeow.IntegrationTests.dll has no FakeWhatsAppService either, so this check cannot see one"
		return 1
	fi
	for project in dotnet/src/*/; do
		name="$(basename "$project")"
		dll="dotnet/artifacts/bin/$name/release/$name.dll"
		if [ ! -f "$dll" ]; then
			annotate error "fake in a shipped assembly" "$dll was not built"
			rc=1
		elif grep -q -a FakeWhatsAppService "$dll"; then
			annotate error "fake in a shipped assembly" "$dll contains the fake WhatsApp control"
			rc=1
		fi
	done
	[ "$rc" -eq 0 ] && echo "no assembly under dotnet/src contains the fake WhatsApp control"
	return "$rc"
}

job_dotnet() {
	local before
	before="$(tree_state)"
	check "noexec mount for the copy fallback test" noexec_mount
	check "repository guard (allowlists, suppressions, generated markers)" go -C tools run ./check-repo
	check "dotnet restore (locked)" dotnet restore dotnet/Whatsmeow.slnx --locked-mode &&
		check "dotnet build" dotnet build dotnet/Whatsmeow.slnx -c Release --no-restore -p:ContinuousIntegrationBuild=true &&
		{
			check "no shipped assembly contains the fake WhatsApp control (ADR 0019)" shipped_have_no_fake
			check "dotnet test" dotnet test --solution dotnet/Whatsmeow.slnx -c Release --no-build
			check "AotSmoke publish (zero trim and AOT warnings)" dotnet publish dotnet/tests/AotSmoke/AotSmoke.csproj -c Release --no-restore -p:ContinuousIntegrationBuild=true &&
				check "AotSmoke run" aot_smoke_run
		}
	check "the dotnet job left the tree unchanged (a stale packages.lock.json shows here too)" tree_unchanged "$before" "the dotnet job"
}

usage() {
	echo "usage: bash build/ci.sh setup|contract|go|dotnet|all" >&2
	exit 2
}

[ $# -eq 1 ] || usage

check "Go from mise equals the go.mod toolchain" go_toolchain || {
	echo "fix the toolchain first" >&2
	exit 1
}

case "$1" in
	setup) ;;
	contract) job_contract ;;
	go) job_go ;;
	dotnet) job_dotnet ;;
	all) job_contract; job_go; job_dotnet ;;
	*) usage ;;
esac

if [ "${#failures[@]}" -gt 0 ]; then
	echo
	echo "failed checks:"
	printf '  %s\n' "${failures[@]}"
	exit 1
fi
echo "all checks passed"
