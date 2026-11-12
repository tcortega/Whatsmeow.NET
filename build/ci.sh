#!/usr/bin/env bash
# The checks CI runs, one subcommand per CI job; .github/workflows/ci.yml calls this and nothing else.
# Subcommands: setup, contract, go, dotnet, external, package, all; install <feed>; aot <feed> <rid>; release-pins v<version>.
# Locally, run the one for what changed; all runs every job in turn, only to reproduce a CI failure (AGENTS.md, Commands).
# After a release, install nuget.org:<version> and aot nuget.org:<version> <rid> run the same checks against nuget.org.
# Works in Git Bash on Windows.
#
# A check runs and reports even after an unrelated one failed (one that needs a failed restore or build is skipped);
# the script fails at the end if any did.
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

# The go.mod toolchain pins of this checkout, or of a published release's commit when one is given (ADR 0059).
go_toolchain() { # [release commit]
	local want got at="${1:+ at $1}"
	pin() { if [ -n "${1:-}" ]; then git show "$1:$2"; else cat "$2"; fi | sed -n 's/^toolchain //p'; }
	want="$(pin "${1:-}" sidecar/go.mod)"
	if [ "$(pin "${1:-}" tools/go.mod)" != "$want" ]; then
		annotate error "Go toolchain" "tools/go.mod and sidecar/go.mod pin different toolchains$at"
		return 1
	fi
	got="$(go env GOVERSION)"
	if [ "$got" != "$want" ]; then
		annotate error "Go toolchain" "go is $got, sidecar/go.mod pins $want$at; align mise.toml and run mise install"
		return 1
	fi
	echo "go is $got, as sidecar/go.mod pins$at"
}

# The Alpine SDK images the package job runs the musl consumers in carry global.json's SDK, which pull requests check
# here, since the package job never runs on one.
alpine_images_follow_sdk() {
	local sdk image rc=0
	sdk="$(jq -r .sdk.version global.json | tr -d '\r')" || return
	for image in "$alpine_image" "$alpine_image_arm64"; do
		if [[ "$image" != "mcr.microsoft.com/dotnet/sdk:$sdk-"* ]]; then
			annotate error "Alpine SDK image" "global.json pins SDK $sdk, but build/ci.sh's Alpine image is ${image%@*}; move both together"
			rc=1
		fi
	done
	[ "$rc" -eq 0 ] && echo "both Alpine SDK images carry SDK $sdk, as global.json pins"
	return "$rc"
}

# A published release's version as its tag names it, v<major>.<minor>.<patch>[-<pre-release>], so the tag is never a
# revision expression or an option; and the tag's commit, in release_commit, which must be on HEAD's line (ADR 0059).
release_commit_of() { # version
	local commit
	release_commit=""
	if ! [[ "$1" =~ ^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(-[0-9A-Za-z-]+(\.[0-9A-Za-z-]+)*)?$ ]]; then
		annotate error "nuget.org" "'$1' is not a release version: <major>.<minor>.<patch>[-<pre-release>]"
		return 1
	fi
	commit="$(git rev-parse -q --verify "refs/tags/v$1^{commit}")"
	if [ -z "$commit" ] || ! git merge-base --is-ancestor "$commit" HEAD; then
		annotate error "nuget.org" "no tag v$1 on HEAD's line: fetch the tags, and run the checker at the release or a later commit after it"
		return 1
	fi
	release_commit="$commit"
}

# The release's tool pins into this checkout, before its tools are installed, so a later checker installs and checks with
# the release's SDK and Go (install.yml runs it; GOTOOLCHAIN stays local).
release_pins() { # tag
	release_commit_of "${1#v}" || return
	git show "$release_commit:mise.toml" >mise.toml && git show "$release_commit:global.json" >global.json || return
	echo "mise.toml and global.json are v${1#v}'s ($release_commit)"
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
	check "shellcheck build/*.sh" shellcheck build/*.sh
	check "the Alpine SDK images carry global.json's SDK" alpine_images_follow_sdk
	check "build/release.sh refuses every tag and feed it must not publish (build/release-test.sh, ADR 0050)" bash build/release-test.sh
	check "every member the last stable release shipped is still shipped (ADR 0055)" bash build/release.sh shipped-api
	check "third_party/whatsmeow matches the pinned module" go -C tools run ./sync-protos
	check "buf lint" buf lint
	# Our protos only: third_party/whatsmeow is upstream's, copied unmodified (ARCHITECTURE §6).
	check "buf format" buf format proto -d --exit-code
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
	fake="$(grep -E '/internal/fakewhatsapp(/|$)|/gen/whatsmeow/fake/' <<<"$deps")"
	if [ -n "$fake" ]; then
		annotate error "fake in the release build" "whatsmeow-net without -tags fakewhatsapp links $fake"
		return 1
	fi
	echo "whatsmeow-net without -tags fakewhatsapp links no fake package"
}

# The release build must not link the live tests' seams (ADR 0032): they are the files named livetest_* behind the
# livetest tag (tools/check-repo allows the tag nowhere else), and a release build may link only their livetest_off.go
# twins. The tagged build must link a livetest_on.go, so this check can see the seams at all.
# livetest_seams reads "package file..." lines and prints each livetest_* file but livetest_off.go as package/file.
livetest_seams() {
	local -a fields
	local file
	while read -r -a fields; do
		# The generated control package's files are named livetest*.go too; it is checked as a package.
		case "${fields[0]:-}" in */gen/whatsmeow/livetest/*) continue ;; esac
		for file in "${fields[@]:1}"; do
			case "$file" in
			livetest_off.go) ;;
			livetest_*) echo "${fields[0]}/$file" ;;
			esac
		done
	done
}

# The live tests' control (ADR 0060) is generated outside the tag, like the fake's, so its package is checked too: the
# tagged build must link it, and the release build must not.
release_links_no_livetest() {
	local files='{{.ImportPath}} {{join .GoFiles " "}}' control='/gen/whatsmeow/livetest/' release live seams leaked
	release="$(CGO_ENABLED=0 go -C sidecar list -deps -f "$files" ./cmd/whatsmeow-net)" || return
	live="$(CGO_ENABLED=0 go -C sidecar list -deps -tags livetest -f "$files" ./cmd/whatsmeow-net)" || return
	# Captured first: grep -q ends the pipe at its first match, which pipefail would report as a failure.
	seams="$(livetest_seams <<<"$live")"
	if ! grep -q '/livetest_on\.go$' <<<"$seams"; then
		annotate error "livetest in the release build" "whatsmeow-net with -tags livetest links no livetest_on.go, so this check cannot see the seams"
		return 1
	fi
	if ! cut -d' ' -f1 <<<"$live" | grep -q -F "$control"; then
		annotate error "livetest in the release build" "whatsmeow-net with -tags livetest links no live test control package, so this check cannot see it"
		return 1
	fi
	leaked="$( (livetest_seams <<<"$release"; cut -d' ' -f1 <<<"$release" | grep -F "$control" || true) | paste -sd ' ' -)"
	if [ -n "$leaked" ]; then
		annotate error "livetest in the release build" "whatsmeow-net without -tags livetest links $leaked"
		return 1
	fi
	echo "whatsmeow-net without -tags livetest links no live test seam or control"
}

# The whatsmeow Client parity report (ADR 0048): shown in the log and the job summary, a warning for each run with a
# missing method, and never a failure, not even when the report itself cannot run; its unit tests gate instead.
parity_report() {
	local report missing
	if ! report="$(go -C tools run ./parity-report)"; then
		annotate warning "parity report" "tools/parity-report did not run; the report is not shown"
		return 0
	fi
	echo "$report"
	if [ -n "${GITHUB_STEP_SUMMARY:-}" ]; then echo "$report" >>"$GITHUB_STEP_SUMMARY"; fi
	missing="$(sed -n 's/.*\*\*\([0-9][0-9]*\) missing\*\*.*/\1/p' <<<"$report")"
	if [ "${missing:-0}" != 0 ]; then
		annotate warning "parity report" "$missing whatsmeow Client methods are missing: cover each or give its reason in sidecar/parity.excluded"
	fi
	return 0
}

# Known vulnerabilities in what a module calls, from the Go standard library and every dependency, whatsmeow or not.
# It reports only, never failing the job, until a maintainer decides to gate on it: then return 1 on exit code 3.
govulncheck_report() { # module
	local report rc ids
	report="$(cd "$1" && govulncheck ./... 2>&1)"
	rc=$?
	echo "$report"
	case "$rc" in
		0) ;;
		3)
			ids="$(sed -n 's/^Vulnerability #[0-9]*: \(GO-[0-9-]*\).*/\1/p' <<<"$report" | tr '\n' ' ')"
			annotate warning "govulncheck" "$1 calls code with known vulnerabilities: ${ids% }; the check's output names the fixed versions"
			;;
		*) annotate warning "govulncheck" "govulncheck did not run on $1 (exit code $rc); the vulnerability report is not shown" ;;
	esac
	return 0
}

job_go() {
	local before module tags label tagsets tested
	before="$(tree_state)"
	for module in tools sidecar; do
		check "$module: go.mod and go.sum are tidy" go -C "$module" mod tidy -diff
		if ! has_packages "$module"; then
			annotate notice "$module" "no Go packages yet; build, test and lint start with the first one"
			continue
		fi
		# A file behind a build tag is invisible to build, test, lint and the cgo check unless the tag is passed, so
		# sidecar, the only module that may use fakewhatsapp and livetest (tools/check-repo), runs them once more with each.
		tagsets=("")
		if [ "$module" = sidecar ]; then tagsets+=(fakewhatsapp livetest); fi
		for tags in "${tagsets[@]}"; do
			label="$module${tags:+ (-tags $tags)}"
			# A tag's test pass tests only the packages the tag changes: tools/check-repo's allowlist for it, which fails
			# the build if the tag appears anywhere else. Change these lists with it.
			case "$tags" in
				fakewhatsapp) tested=(./internal/fakewhatsapp/... ./cmd/whatsmeow-net/...) ;;
				livetest) tested=(./internal/events ./internal/messaging ./internal/contacts) ;;
				*) tested=(./...) ;;
			esac
			check "$label: no cgo files" no_cgo "$module" "$tags"
			check "$label: go build (no cgo)" env CGO_ENABLED=0 go -C "$module" build -tags "$tags" ./...
			check "$label: go test -race ${tested[*]}" go -C "$module" test -race -count=1 -tags "$tags" "${tested[@]}"
			# With cgo on, so a cgo file is visible (its import "C" is reported as unsafe).
			check "$label: golangci-lint" bash -c "cd '$module' && CGO_ENABLED=1 golangci-lint run --build-tags '$tags' ./..."
		done
		check "$module: govulncheck (report only)" govulncheck_report "$module"
	done
	check "sidecar: the release build links no fake WhatsApp (ADR 0019)" release_links_no_fake
	check "sidecar: the release build links no live test seam or control (ADR 0032, 0060)" release_links_no_livetest
	check "sidecar: linked modules and licence files match sidecar/licenses.allow (ADR 0016)" go -C tools run ./check-licenses
	check "whatsmeow Client parity (report only, ADR 0048)" parity_report
	check "the go job left the tree unchanged" tree_unchanged "$before" "the go job"
}

# The client's version, in a restored tree. MinVer sets Version in its MinVer target, so -getProperty alone would read
# the SDK default, 1.0.0 (ADR 0023).
client_version() {
	dotnet msbuild dotnet/src/Whatsmeow.Client/Whatsmeow.Client.csproj -t:MinVer -getProperty:Version | tr -d '\r'
}

# AotSmoke starts a real whatsmeow-net under Native AOT, so the sidecar is built here at the client's version.
aot_smoke_run() {
	local rid exe="" version sidecar
	rid="$(dotnet msbuild dotnet/tests/AotSmoke/AotSmoke.csproj -getProperty:RuntimeIdentifier)" || return
	case "$rid" in win-*) exe=.exe ;; esac
	version="$(client_version)" || return
	sidecar="dotnet/artifacts/aot-smoke/whatsmeow-net$exe"
	CGO_ENABLED=0 go -C sidecar build -ldflags "-X main.version=$version" -o "../$sidecar" ./cmd/whatsmeow-net || return
	"dotnet/artifacts/publish/AotSmoke/release_$rid/AotSmoke$exe" "$version" "$rid" "$sidecar"
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

# The test-only controls' C# is generated into their test assembly only: the fake WhatsApp's into the integration tests
# (ADR 0019), the live tests' into the live tests (ADR 0060). No assembly under dotnet/src, all of which ship, may
# contain a type in either namespace, which buf.gen.yaml gives every generated type of the control, messages and
# service alike. Each home assembly must, or this grep proves nothing.
shipped_have_no_control() {
	local namespace="$1" home="$2" what="$3" project name dll rc=0
	if ! grep -q -a -F "$namespace" "dotnet/artifacts/bin/$home/release/$home.dll"; then
		annotate error "$what in a shipped assembly" "$home.dll has no $namespace type either, so this check cannot see one"
		return 1
	fi
	for project in dotnet/src/*/; do
		name="$(basename "$project")"
		dll="dotnet/artifacts/bin/$name/release/$name.dll"
		if [ ! -f "$dll" ]; then
			annotate error "$what in a shipped assembly" "$dll was not built"
			rc=1
		elif grep -q -a -F "$namespace" "$dll"; then
			annotate error "$what in a shipped assembly" "$dll contains the $what"
			rc=1
		fi
	done
	[ "$rc" -eq 0 ] && echo "no assembly under dotnet/src contains the $what"
	return "$rc"
}

shipped_have_no_fake() { shipped_have_no_control Whatsmeow.IntegrationTests.Fake Whatsmeow.IntegrationTests "fake WhatsApp control"; }

shipped_have_no_livetest() { shipped_have_no_control Whatsmeow.LiveTests.Control Whatsmeow.LiveTests "live tests' control"; }

# The live tests (docs/guidelines/tests.md Live WhatsApp) are contained: [LiveFact] appears only in
# Whatsmeow.LiveTests, Explicit, a declared exception to the banned list, only in its LiveFactAttribute.cs, every
# test there is a [LiveFact] (no [Fact], [FactAttribute], [Xunit.Fact] or [Theory]), and QRCoder, which it and the sample use, is in no shipped
# project; nor is OpenTelemetry, which only the sample uses (ADR 0049).
# Each grep has a positive control, or it could prove nothing.
live_tests_contained() {
	local live=dotnet/tests/Whatsmeow.LiveTests attribute=dotnet/tests/Whatsmeow.LiveTests/LiveFactAttribute.cs sample=dotnet/samples/EchoBot
	local explicit='(^|[^A-Za-z0-9_])Explicit[[:space:]]*=[^=]' found lock rc=0
	if ! grep -rqF --include='*.cs' '[LiveFact' "$live" || ! grep -qE "$explicit" "$attribute" ||
		! grep -qF '"QRCoder"' "$live/packages.lock.json" || ! grep -qF '"OpenTelemetry"' "$sample/packages.lock.json"; then
		annotate error "live tests" "no [LiveFact] in $live, no Explicit in $attribute, no QRCoder in its lock file, or no OpenTelemetry in $sample's, so this check cannot see them"
		return 1
	fi
	# Generated/ is buf's output, which nobody writes (docs/guidelines/csharp.md); its protos have Explicit fields.
	found="$(grep -rnE --include='*.cs' --exclude-dir=artifacts --exclude-dir=Generated "LiveFact|$explicit" dotnet |
		grep -v "^$live/" ; grep -rnE --include='*.cs' "$explicit" "$live" | grep -v "^$attribute:" ;
		grep -rnE --include='*.cs' '\[(Xunit\.)?(Fact|Theory)' "$live")"
	if [ -n "$found" ]; then
		annotate error "live tests" "LiveFact belongs to $live, Explicit to $attribute, and a live test is a [LiveFact] (docs/guidelines/tests.md)"
		echo "$found"
		rc=1
	fi
	for lock in dotnet/src/*/packages.lock.json; do
		if grep -qF '"QRCoder"' "$lock"; then
			annotate error "live tests" "$lock depends on QRCoder, which only $live and the sample may use"
			rc=1
		fi
		if grep -qF '"OpenTelemetry' "$lock"; then
			annotate error "live tests" "$lock depends on OpenTelemetry, which only $sample may use"
			rc=1
		fi
	done
	[ "$rc" -eq 0 ] && echo "LiveFact, Explicit, the live tests and QRCoder stay in $live and the sample, OpenTelemetry in $sample"
	return "$rc"
}

# without_live COMMAND...: COMMAND with no WHATSMEOW_LIVE_* variable, so dotnet test never runs the live tests (or
# reads their settings), even with them exported in the shell that runs this.
without_live() {
	(
		for name in $(compgen -e); do
			case "$name" in WHATSMEOW_LIVE_*) unset "$name" ;; esac
		done
		exec "$@"
	)
}

job_dotnet() {
	local before
	before="$(tree_state)"
	check "noexec mount for the copy fallback test" noexec_mount
	check "repository guard (allowlists, suppressions, generated markers)" go -C tools run ./check-repo
	check "the live tests are contained (docs/guidelines/tests.md)" live_tests_contained
	check "dotnet restore (locked)" dotnet restore dotnet/Whatsmeow.slnx --locked-mode &&
		check "dotnet build" dotnet build dotnet/Whatsmeow.slnx -c Release --no-restore -p:ContinuousIntegrationBuild=true &&
		{
			check "dotnet format leaves the code unchanged (docs/guidelines/csharp.md)" dotnet format dotnet/Whatsmeow.slnx --verify-no-changes --no-restore
			check "no shipped assembly contains the fake WhatsApp control (ADR 0019)" shipped_have_no_fake
			check "no shipped assembly contains the live tests' control (ADR 0060)" shipped_have_no_livetest
			# Never the live tests, even with their variables exported in the shell that runs this.
			rm -f dotnet/artifacts/TestResults/media-transfers.txt dotnet/artifacts/TestResults/fake-sidecar.log
			check "dotnet test" without_live dotnet test --solution dotnet/Whatsmeow.slnx -c Release --no-build
			# The media transfer test's throughput and memory on each transport, for ARCHITECTURE §8.6 (ADR 0026).
			if [ -f dotnet/artifacts/TestResults/media-transfers.txt ]; then
				cat dotnet/artifacts/TestResults/media-transfers.txt
			fi
			check "AotSmoke publish (zero trim and AOT warnings)" dotnet publish dotnet/tests/AotSmoke/AotSmoke.csproj -c Release --no-restore -p:ContinuousIntegrationBuild=true &&
				check "AotSmoke run" aot_smoke_run
		}
	check "the dotnet job left the tree unchanged (a stale packages.lock.json shows here too)" tree_unchanged "$before" "the dotnet job"
}

# The integration suite once more, its shared fixture's sidecar standalone over TCP, as an orchestrator runs it
# (External mode, ADR 0046): every slice's FullCycle against an external sidecar. Its own CI job, on each OS (ADR 0066).
job_external() {
	# The noexec tests run in this pass too, and fail on linux-x64 CI without the mount.
	check "noexec mount for the copy fallback test" noexec_mount
	check "dotnet restore (locked)" dotnet restore dotnet/Whatsmeow.slnx --locked-mode &&
		check "dotnet build" dotnet build dotnet/tests/Whatsmeow.IntegrationTests -c Release --no-restore -p:ContinuousIntegrationBuild=true &&
		rm -f dotnet/artifacts/TestResults/fake-sidecar.log &&
		check "dotnet test, External mode" without_live env WHATSMEOW_NET_TEST_SIDECAR=external dotnet test --project dotnet/tests/Whatsmeow.IntegrationTests -c Release --no-build
}

# Native packages (ADR 0023). The package job builds whatsmeow-net for every release target at the client's version and
# packs every package into a local feed with the Corresponding Source archive beside them (ADR 0029); locally it installs
# from it too. CI packs on Windows x64, and install.yml's jobs install that feed on every native host, so Windows-built
# packages run on all of them; after a release, the same jobs install it from nuget.org (#52).
package_feed=dotnet/artifacts/package
sidecar_binaries=dotnet/artifacts/sidecar
alpine_image=mcr.microsoft.com/dotnet/sdk:11.0.100-alpine3.24-amd64@sha256:8234f4b51a16ff4416c013729616bd5df1211ced20e746067ddc66be7f526063
alpine_image_arm64=mcr.microsoft.com/dotnet/sdk:11.0.100-alpine3.24-arm64v8@sha256:0de9ddd207f8aeb52aa36e4d8b54a22e06b26d1f8c4159264912900772c88d93

# rid GOOS/GOARCH: the .NET RID of a Go target.
rid() {
	local os="${1%/*}" arch="${1#*/}"
	case "$os" in darwin) os=osx ;; windows) os=win ;; esac
	case "$arch" in amd64) arch=x64 ;; esac
	echo "$os-$arch"
}

exe_of() { # GOOS/GOARCH
	case "$1" in windows/*) echo .exe ;; *) echo "" ;; esac
}

# The release targets: the one list tools/check-licenses audits (ADR 0016).
release_targets() {
	go -C "${release_tree:-.}/tools" run ./check-licenses -targets | tr -d '\r'
}

# A path for dotnet: Git Bash on Windows hands POSIX paths to native programs only in arguments it recognizes.
native_path() {
	if command -v cygpath >/dev/null 2>&1; then cygpath -m "$1"; else printf '%s\n' "$1"; fi
}

# The runner's architecture, not Go's or Git Bash's process architecture (ADR 0058).
native_host() {
	local os arch
	case "${RUNNER_OS:-$(uname -s)}" in Linux) os=linux ;; macOS | Darwin) os=osx ;; Windows | MINGW* | MSYS*) os=win ;; *) os="$(uname -s)" ;; esac
	case "${RUNNER_ARCH:-$(uname -m)}" in X64 | x86_64 | amd64) arch=x64 ;; ARM64 | aarch64 | arm64) arch=arm64 ;; *) arch="${RUNNER_ARCH:-$(uname -m)}" ;; esac
	native_host_rid="$os-$arch"
	host_os_arch="$os $arch"
	case "$native_host_rid" in
		linux-x64 | linux-arm64 | osx-x64 | osx-arm64 | win-x64 | win-arm64) ;;
		*) annotate error "native host" "unsupported host $native_host_rid"; return 1 ;;
	esac
	echo "native host: $host_os_arch (RUNNER_OS/RUNNER_ARCH when set, uname otherwise)"
}

alpine_for_arch() { # x64|arm64
	case "$1" in
		x64) echo "$alpine_image" ;;
		arm64) echo "$alpine_image_arm64" ;;
		*) annotate error "Alpine" "unsupported architecture $1"; return 1 ;;
	esac
}

linux_ci() {
	[ "${GITHUB_ACTIONS:-}" = true ] && [ "$(uname -s)" = Linux ]
}

# One version for every package and binary. Untagged, it must be a pre-release, so it can never pass for a release.
package_version() {
	version="$(client_version)" || return
	if [ -z "$version" ]; then
		annotate error "version" "MinVer gave no version for Whatsmeow.Client"
		return 1
	fi
	if ! git describe --exact-match --tags --match 'v[0-9]*' HEAD >/dev/null 2>&1 && [[ "$version" != *-* ]]; then
		annotate error "version" "HEAD has no v<semver> tag, yet the version $version is not a pre-release (MinVer, ADR 0023)"
		return 1
	fi
	echo "every package and binary is $version"
}

# Package validation's baseline (ADR 0055), from the tags: build/release.sh baseline, tested by build/release-test.sh. A
# rehearsal's comes from the next stable release's, so it compares with what that release will.
package_baseline() {
	baseline="$(bash build/release.sh baseline "$version")" || return
	if [ -z "$baseline" ]; then
		echo "no package validation baseline: no stable release of this major version below $version"
		return 0
	fi
	echo "package validation compares the stable packages with their published $baseline"
}

# The baseline packages come from nuget.org through restore (a PackageDownload, outside the lock file). Only NU1102,
# no such version on nuget.org, means the release is missing; any other failure is restore's own.
restore_baseline() {
	local out rc
	[ -n "$baseline" ] || return 0
	out="$(dotnet restore dotnet/Whatsmeow.slnx --locked-mode -p:PackageValidationBaselineVersion="$baseline" 2>&1)"
	rc=$?
	printf '%s\n' "$out"
	[ "$rc" -eq 0 ] && return 0
	if grep -q 'NU1102' <<<"$out"; then
		annotate error "package validation" "restore found no $baseline of a stable package on nuget.org (NU1102): the v$baseline release is not published yet, or was refused and its tag must be deleted (ADR 0050, ADR 0055)"
		return 1
	fi
	annotate error "package validation" "restore of the $baseline baseline packages failed; see its output above (ADR 0055)"
	return 1
}

# A stable release ships its public API (ADR 0055): at a stable tag every PublicAPI.Unshipped.txt holds only its
# #nullable enable line, so PublicAPI.Shipped.txt is what the release publishes and RS0017 guards all of it. A
# rehearsal stands for the next stable release, so it is held to that too.
release_api_shipped() {
	local file rc=0
	if [[ "$version" == *-* ]] && [[ "$version" != 0.0.0-rehearsal.* ]]; then
		echo "$version is a pre-release, so its public API may stay unshipped"
		return 0
	fi
	for file in dotnet/src/*/PublicAPI.Unshipped.txt; do
		if grep -q -v -x -e '#nullable enable' -e '' "$file"; then
			annotate error "public API" "$file is not empty at $version, a stable release or the rehearsal of one: move its lines to PublicAPI.Shipped.txt before tagging (ADR 0055)"
			rc=1
		fi
	done
	[ "$rc" -eq 0 ] && echo "every stable package's public API is shipped at $version"
	return "$rc"
}

# Release builds only: no fakewhatsapp tag (ADR 0019); -trimpath and -s -w, reproducible from the commit (ADR 0023).
build_sidecars() {
	local targets target
	targets="$(release_targets)" || return
	rm -rf "$sidecar_binaries"
	for target in $targets; do
		CGO_ENABLED=0 GOOS="${target%/*}" GOARCH="${target#*/}" go -C sidecar build -trimpath -ldflags "-s -w -X main.version=$version" \
			-o "../$sidecar_binaries/$(rid "$target")/whatsmeow-net$(exe_of "$target")" ./cmd/whatsmeow-net || return
		echo "built whatsmeow-net $version for $target: $(wc -c <"$sidecar_binaries/$(rid "$target")/whatsmeow-net$(exe_of "$target")" | tr -d ' ') bytes"
	done
}

pack_feed() {
	local nupkg line
	rm -rf "$package_feed"
	dotnet pack dotnet/Whatsmeow.slnx -c Release --no-restore -p:ContinuousIntegrationBuild=true ${baseline:+-p:PackageValidationBaselineVersion="$baseline"} -o "$package_feed" || return
	echo "$version" >"$package_feed/version"
	nupkg="$package_feed/Whatsmeow.Sidecar.Native.$version.nupkg"
	line="$(basename "$nupkg"): $(wc -c <"$nupkg" | tr -d ' ') bytes"
	echo "$line"
	if [ -n "${GITHUB_STEP_SUMMARY:-}" ]; then echo "- $line" >>"$GITHUB_STEP_SUMMARY"; fi
}

# The Corresponding Source of the binaries (ADR 0016, ADR 0029): the commit, as git archive gives it, plus the output
# of go mod vendor for sidecar/. It goes into the feed beside the packages, so the install jobs build from the archive
# the release workflow (#46) attaches. A local run archives HEAD, without uncommitted changes.
source_archive_name() {
	echo "whatsmeow-net_${version}_source.tar.gz"
}

source_archive() {
	local prefix="whatsmeow-net_$version" archive work size line rc=0
	archive="$package_feed/$(source_archive_name)"
	work="$(mktemp -d)" || return
	# One exit path, so the temporary copy (about 150 MB with the vendor tree) is removed on failure too.
	{
		git archive --format=tar --prefix="$prefix/" HEAD >"$work/source.tar" &&
			git archive --format=tar --prefix="$prefix/" HEAD sidecar | tar -x -C "$work" &&
			go -C "$(native_path "$work/$prefix/sidecar")" mod vendor &&
			(cd "$work" && tar -rf source.tar "$prefix/sidecar/vendor") &&
			gzip -9n <"$work/source.tar" >"$archive"
	} || rc=$?
	rm -rf "$work"
	[ "$rc" -eq 0 ] || return "$rc"
	size="$(wc -c <"$archive" | tr -d ' ')"
	line="$(basename "$archive"): $size bytes"
	echo "$line"
	if [ -n "${GITHUB_STEP_SUMMARY:-}" ]; then echo "- $line" >>"$GITHUB_STEP_SUMMARY"; fi
}

# A new console app in a directory of its own, outside the repository, so none of our MSBuild files, central versions
# or nuget.config apply. It restores Whatsmeow.* from one place only (source mapping) into an empty packages folder and an
# empty HTTP cache, so it gets exactly what that place holds, never a cached package of the same version: a feed
# directory, or nuget.org for a published release (nuget.org:<version>, #52).
consumer_setup() { # feed | nuget.org:<version>
	# The physical path: macOS's temporary directory is under /var, a symlink to /private/var, and there
	# `dotnet add package` fails with "The project does not support adding package references through the add package
	# command" [observed: SDK 11.0.100, macOS; the same command passes from /private/var].
	work="$(cd "$(mktemp -d)" && pwd -P)" || return
	packages="$work/packages"
	package_origin "$1" || return
	native_host || return
	host_rid="$native_host_rid"
	exe=""
	case "$host_rid" in win-*) exe=.exe ;; esac
	cp "$release_tree/global.json" "$work/" || return
	consumer_config "$origin" >"$work/nuget.config" || return
	in_consumer dotnet new console -o app -n Consumer || return
	cp "$release_tree/build/package-smoke/Program.cs" "$work/app/" || return
	for id in Whatsmeow.Client Whatsmeow.Extensions.Hosting Whatsmeow.Testing; do
		in_consumer dotnet add app/Consumer.csproj package "$id" --version "$version" 2>&1 | tee -a "$work/restore.log" || return
	done
	if [ -n "$published" ]; then served_feed || return; fi
	echo "a consumer of Whatsmeow.Client, Whatsmeow.Extensions.Hosting and Whatsmeow.Testing $version from $origin in $work"
}

# Where the packages come from, and the release whose inputs the checks compare with. A feed directory gives its version
# file and its packages, and the release is this checkout (HEAD). nuget.org:<version> gives what nuget.org serves, which
# served_feed copies into $work/feed after the restore, and the release is the tag v<version>: its tree, extracted into
# $work/release, gives every input (the consumer's global.json and Program.cs, AotSmoke, the notices and READMEs, the
# package ids, the release targets, the Go toolchain), so a later checker revision can check a published release (ADR
# 0059). The tag must be on HEAD's line: the checker is the release itself, or a later commit after it.
nuget_org=https://api.nuget.org/v3/index.json
package_origin() { # feed | nuget.org:<version>
	local feed
	case "$1" in
		nuget.org:*)
			published=nuget.org version="${1#nuget.org:}" origin="$nuget_org" package_source="$work/feed" release_tree="$work/release"
			release_commit_of "$version" || return
			mkdir -p "$package_source" "$release_tree" && git archive "$release_commit" | tar -x -C "$release_tree" || return
			# The SDK and tools installed are the release's, which release-pins arranges before they are installed.
			if ! cmp -s "$release_tree/global.json" global.json || ! cmp -s "$release_tree/mise.toml" mise.toml; then
				annotate error "nuget.org" "this checkout pins another SDK or tools than v$version: run bash build/ci.sh release-pins v$version, then mise install, as install.yml does"
				return 1
			fi
			# And the Alpine SDK images the release pinned beside its global.json, whose rollForward is disable.
			alpine_image="$(sed -n 's/^alpine_image=//p' "$release_tree/build/ci.sh")"
			alpine_image_arm64="$(sed -n 's/^alpine_image_arm64=//p' "$release_tree/build/ci.sh")"
			if [ -z "$alpine_image" ] || [ -z "$alpine_image_arm64" ]; then
				annotate error "nuget.org" "v$version's build/ci.sh names no Alpine SDK images"
				return 1
			fi
			echo "the release's Alpine SDK images: $alpine_image $alpine_image_arm64"
			echo "the release's inputs from v$version ($release_commit); the checker is $(git rev-parse HEAD)"
			;;
		*)
			published="" feed="$(cd "$1" && pwd)" || return
			# GNU tar treats a Windows drive prefix as a remote host; keep the source archive path POSIX too.
			package_source="$feed" release_tree="$root" release_commit="$(git rev-parse HEAD)"
			version="$(tr -d '\r' <"$feed/version")" || return
			origin="$(native_path "$feed")"
			;;
	esac
}

# Exactly the release's packages (its build/release.sh ids), each restored from nuget.org at exactly the version, as the
# source restore records for it says, and no other Whatsmeow package, copied into $work/feed under its id, so the
# packaged_* checks read the bytes nuget.org served; and the release's Corresponding Source from its GitHub release,
# which publishes before any push (ADR 0050).
served_feed() {
	local ids want got id lower dir nupkg asset
	ids="$(bash "$release_tree/build/release.sh" ids)" || return
	want="$(lowercase "$ids" | LC_ALL=C sort)"
	got="$(cd "$packages" && find . -maxdepth 1 -name 'whatsmeow.*' | sed 's#^\./##' | LC_ALL=C sort)"
	if [ -z "$ids" ] || [ "$want" != "$got" ]; then
		annotate error "nuget.org" "the restore did not get exactly the release's packages (build/release.sh ids)"
		diff <(echo "$want") <(echo "$got")
		return 1
	fi
	for id in $ids; do
		lower="$(lowercase "$id")" && dir="$packages/$lower/$version" && nupkg="$dir/$lower.$version.nupkg"
		if [ ! -f "$nupkg" ] || ! grep -qF "<id>$id</id>" "$dir/$lower.nuspec" || ! grep -qF "<version>$version</version>" "$dir/$lower.nuspec"; then
			annotate error "nuget.org" "nuget.org served no $id $version"
			return 1
		fi
		if ! tr -d ' \r\n' <"$dir/.nupkg.metadata" | grep -qF "\"source\":\"$nuget_org\""; then
			annotate error "nuget.org" "$id $version was not restored from $nuget_org (its .nupkg.metadata source)"
			return 1
		fi
		cp "$nupkg" "$package_source/$id.$version.nupkg" || return
	done
	echo "$version" >"$package_source/version" || return
	asset="$repository_url/releases/download/v$version/$(source_archive_name)"
	if ! curl -fsSL --retry 3 -o "$package_source/$(source_archive_name)" "$asset"; then
		annotate error "nuget.org" "cannot download the Corresponding Source $asset (ADR 0016)"
		return 1
	fi
	echo "nuget.org served exactly the $(wc -w <<<"$ids" | tr -d ' ') packages of $version; the source archive is the release asset"
}

# Whatsmeow.* from the feed only, everything else from nuget.org; for a published release, nuget.org alone.
consumer_config() { # feed path | nuget.org's index
	if [ "$1" = "$nuget_org" ]; then
		cat <<EOF
<?xml version="1.0" encoding="utf-8"?>
<configuration>
	<packageSources>
		<clear />
		<add key="nuget.org" value="$nuget_org" protocolVersion="3" />
	</packageSources>
	<packageSourceMapping>
		<packageSource key="nuget.org"><package pattern="*" /></packageSource>
	</packageSourceMapping>
</configuration>
EOF
		return
	fi
	cat <<EOF
<?xml version="1.0" encoding="utf-8"?>
<configuration>
	<packageSources>
		<clear />
		<add key="local" value="$1" />
		<add key="nuget.org" value="$nuget_org" protocolVersion="3" />
	</packageSources>
	<packageSourceMapping>
		<packageSource key="local"><package pattern="Whatsmeow.*" /></packageSource>
		<packageSource key="nuget.org"><package pattern="*" /></packageSource>
	</packageSourceMapping>
</configuration>
EOF
}

# Never with WHATSMEOW_NET_SIDECAR_PATH: an exported override would make every consumer path pass without the packages.
# Never on the shared compiler server either: a long-lived one failed Vogen's generator with CS8785 (Vogen.SharedTypes not
# found) on a later run's throwaway packages folder, 4 local runs on osx-arm64, none with it off [observed: #122 log].
in_consumer() {
	(unset WHATSMEOW_NET_SIDECAR_PATH && cd "$work" && NUGET_PACKAGES="$(native_path "$packages")" NUGET_HTTP_CACHE_PATH="$(native_path "$work/http-cache")" UseSharedCompilation=false "$@")
}

# The native package, which holds the binary of every RID (ADR 0069). A packages folder names it in lowercase.
native_id=Whatsmeow.Sidecar.Native

lowercase() {
	tr '[:upper:]' '[:lower:]' <<<"$1"
}

# Whatsmeow.Extensions.Hosting uses Whatsmeow.Client internals, so it depends on it at exactly its own version (ADR 0034).
packaged_hosting_pin() {
	local nuspec="$packages/whatsmeow.extensions.hosting/$version/whatsmeow.extensions.hosting.nuspec"
	if [ ! -f "$nuspec" ]; then
		annotate error "exact pins" "the consumer restored no $nuspec"
		return 1
	fi
	if ! grep -qF "<dependency id=\"Whatsmeow.Client\" version=\"[$version]\"" "$nuspec"; then
		annotate error "exact pins" "Whatsmeow.Extensions.Hosting does not depend on Whatsmeow.Client at exactly [$version] (ADR 0034); its ProjectReference needs PackExactVersion=\"true\" (_WhatsmeowExactProjectVersions, ADR 0023)"
		grep -F 'Whatsmeow.Client' "$nuspec"
		return 1
	fi
	grep -F 'Whatsmeow.Client' "$nuspec"
}

# Whatsmeow.Testing uses Whatsmeow.Client and Whatsmeow.Extensions.Hosting internals, so it depends on both at exactly
# its own version (ADR 0036).
packaged_testing_pins() {
	local nuspec="$packages/whatsmeow.testing/$version/whatsmeow.testing.nuspec" id rc=0
	if [ ! -f "$nuspec" ]; then
		annotate error "exact pins" "the consumer restored no $nuspec"
		return 1
	fi
	for id in Whatsmeow.Client Whatsmeow.Extensions.Hosting; do
		if ! grep -qF "<dependency id=\"$id\" version=\"[$version]\"" "$nuspec"; then
			annotate error "exact pins" "Whatsmeow.Testing does not depend on $id at exactly [$version] (ADR 0036); its ProjectReference needs PackExactVersion=\"true\" (_WhatsmeowExactProjectVersions, ADR 0023)"
			rc=1
		fi
	done
	grep -F 'Whatsmeow.' "$nuspec"
	return "$rc"
}

# Whatsmeow.Client is compiled against Whatsmeow.Protocol's generated code and internals, and that code against
# Whatsmeow.Protocol.WhatsApp's types, which the Raw hatch exposes; each depends on the next at exactly its own version
# (ADR 0055).
packaged_protocol_pins() {
	local pair nuspec dependency rc=0
	for pair in whatsmeow.client:Whatsmeow.Protocol whatsmeow.protocol:Whatsmeow.Protocol.WhatsApp; do
		nuspec="$packages/${pair%%:*}/$version/${pair%%:*}.nuspec" dependency="${pair#*:}"
		if [ ! -f "$nuspec" ]; then
			annotate error "exact pins" "the consumer restored no $nuspec"
			rc=1
			continue
		fi
		if ! grep -qF "<dependency id=\"$dependency\" version=\"[$version]\"" "$nuspec"; then
			annotate error "exact pins" "${pair%%:*} does not depend on $dependency at exactly [$version] (ADR 0055); its ProjectReference needs PackExactVersion=\"true\" (_WhatsmeowExactProjectVersions, ADR 0023)"
			rc=1
		fi
		grep -F "\"$dependency\"" "$nuspec"
	done
	return "$rc"
}

native_package() {
	echo "$packages/$(lowercase "$native_id")/$version"
}

packaged_pins() {
	local nuspec="$packages/whatsmeow.client/$version/whatsmeow.client.nuspec"
	if [ ! -f "$nuspec" ]; then
		annotate error "exact pins" "the consumer restored no $nuspec"
		return 1
	fi
	if ! grep -qF "<dependency id=\"$native_id\" version=\"[$version]\"" "$nuspec"; then
		annotate error "exact pins" "Whatsmeow.Client does not depend on $native_id at exactly [$version] (§13 invariant 12). _WhatsmeowExactProjectVersions in dotnet/Directory.Build.targets makes the pin by rewriting NuGet's internal _ProjectReferencesWithVersions item; after an SDK bump, check that NuGet.Build.Tasks.Pack.targets still fills it in _GetProjectReferenceVersions (ADR 0023)"
		grep -F 'Whatsmeow.Sidecar.Native' "$nuspec"
		return 1
	fi
	grep -F 'Whatsmeow.Sidecar.Native' "$nuspec"
}

# The native package holds exactly the binaries, one per release target, its buildTransitive targets, its README and the
# GPL files, beside the files NuGet writes when it extracts a package: no build output, nothing else (ADR 0023, ADR
# 0029, ADR 0069).
packaged_files() {
	local targets target name expected actual
	targets="$(release_targets)" || return
	name="$(lowercase "$native_id")"
	expected="$({
		for target in $targets; do echo "runtimes/$(rid "$target")/native/whatsmeow-net$(exe_of "$target")"; done
		printf '%s\n' "buildTransitive/$native_id.targets" README.md "${native_notices[@]}" \
			.nupkg.metadata "$name.$version.nupkg" "$name.$version.nupkg.sha512" "$name.nuspec"
		# nuget.org adds a repository signature, extracted at the package root [observed: every package restored from
		# nuget.org in a local packages folder, 2026-11-06; inferred for ours, whose first publish decides]. A presence
		# check of the served file, not a cryptographic verification.
		if [ -n "$published" ]; then echo .signature.p7s; fi
	} | LC_ALL=C sort)"
	actual="$(cd "$(native_package)" && find . -type f | sed 's#^\./##' | LC_ALL=C sort)" || return
	if [ "$expected" != "$actual" ]; then
		annotate error "packaged files" "$native_id does not hold exactly the binaries for the release targets (go -C tools run ./check-licenses -targets), buildTransitive/$native_id.targets, README.md and ${native_notices[*]}"
		diff <(echo "$expected") <(echo "$actual")
		return 1
	fi
	echo "the native package holds exactly the binaries, its buildTransitive targets, its README and ${native_notices[*]}"
}

# The GPL files at the root of the native package (ADR 0016, ADR 0029), and the repository file each must equal.
native_notices=(COPYING NOTICE THIRD-PARTY-NOTICES.txt)
notice_source() { # packaged file
	printf '%s/' "${release_tree:-.}"
	case "$1" in
		COPYING) echo sidecar/COPYING ;;
		NOTICE) echo NOTICE ;;
		THIRD-PARTY-NOTICES.txt) echo sidecar/THIRD-PARTY-NOTICES.txt ;;
		README.md) echo dotnet/src/Whatsmeow.Sidecar.Native/README.md ;;
	esac
}
# sha256 of https://www.gnu.org/licenses/gpl-3.0.txt, the GPL-3.0 text unmodified [observed 2026-11-02]. libsignal's LICENSE
# is not it: its appendix names libsignal, and its URLs are http.
gpl3_sha256=3972dc9744f6499f0f9b2dbf76696f2ae7ad8af9b23dde66d6af86c9dfb36986

sha256_of() { # file
	if command -v sha256sum >/dev/null 2>&1; then sha256sum "$1"; else shasum -a 256 "$1"; fi | cut -d' ' -f1
}

# What the README and NOTICE must say, whatever else changes: the name of the source archive, and in the README the
# evidence tags ADR 0016 gave each claim about what redistributing owes.
readme_tags=('[inferred: GPLv3 §5(c) and §14, ADR 0016]' '[inferred: FSF FAQ #MereAggregation, ADR 0016]' '[inferred: GPLv3 §0]'
	"conveys it, and you take on GPLv3 §6 for \`whatsmeow-net\` [inferred]" '[inferred: GPLv3 §6 "User Product"]'
	"which ships no binary in the app [observed: \`build/ci.sh install\`, ADR 0046]" 'Legal review of all of the above is [unknown]')

# The native package's README and GPL files equal the repository's, so they ship exactly what was reviewed; its
# COPYING is the GPL-3.0 text itself, its NOTICE and README name the source archive, and its README keeps the tags.
packaged_notices() {
	local dir file tag rc=0
	dir="$(native_package)"
	for file in README.md "${native_notices[@]}"; do
		if ! cmp -s "$dir/$file" "$(notice_source "$file")"; then
			annotate error "packaged notices" "$native_id/$file is not $(notice_source "$file")"
			rc=1
		fi
	done
	if [ -f "$dir/COPYING" ] && [ "$(sha256_of "$dir/COPYING")" != "$gpl3_sha256" ]; then
		annotate error "packaged notices" "$native_id/COPYING is not the GPL-3.0 text (sha256 $gpl3_sha256, gnu.org/licenses/gpl-3.0.txt)"
		rc=1
	fi
	for file in NOTICE README.md; do
		if ! grep -qF 'whatsmeow-net_<version>_source.tar.gz' "$dir/$file" 2>/dev/null; then
			annotate error "packaged notices" "$native_id/$file does not name the source archive, whatsmeow-net_<version>_source.tar.gz"
			rc=1
		fi
	done
	for tag in "${readme_tags[@]}"; do
		if ! grep -qF -- "$tag" "$dir/README.md" 2>/dev/null; then
			annotate error "packaged notices" "$native_id/README.md lost ADR 0016's evidence tag: $tag"
			rc=1
		fi
	done
	[ "$rc" -eq 0 ] && echo "the native package ships the repository's README.md and ${native_notices[*]}, and COPYING is the GPL-3.0 text"
	return "$rc"
}

# Every package in the feed restored into the consumer, with its licence expression: GPL-3.0-only for the native
# package, which holds whatsmeow-net, and MPL-2.0 for the .NET packages (ADR 0016).
packaged_licences() {
	local nupkg id name nuspec want rc=0
	for nupkg in "$package_source"/*.nupkg; do
		id="$(basename "$nupkg" ".$version.nupkg")" name="$(lowercase "$id")"
		nuspec="$packages/$name/$version/$name.nuspec"
		want=MPL-2.0
		if [ "$id" = "$native_id" ]; then want=GPL-3.0-only; fi
		if [ ! -f "$nuspec" ]; then
			annotate error "packaged licences" "$id from ${published:-the feed} was not restored, so its licence is unchecked"
			rc=1
		elif ! grep -qF "<license type=\"expression\">$want</license>" "$nuspec"; then
			annotate error "packaged licences" "$id does not declare the licence expression $want"
			grep -F '<license' "$nuspec"
			rc=1
		else
			echo "$id: $want"
		fi
	done
	return "$rc"
}

# Every package's nuget.org page (ADR 0054): a description of its own, the tags, the project and repository URLs,
# NOTICE's author, and an embedded README that is the repository's: the project's README.md.
repository_url=https://github.com/tcortega/Whatsmeow.NET
packaged_metadata() {
	local nupkg id name dir nuspec field readme rc=0
	for nupkg in "$package_source"/*.nupkg; do
		id="$(basename "$nupkg" ".$version.nupkg")" name="$(lowercase "$id")"
		dir="$packages/$name/$version" nuspec="$packages/$name/$version/$name.nuspec"
		if [ ! -f "$nuspec" ]; then
			annotate error "package metadata" "$id from ${published:-the feed} was not restored, so its metadata is unchecked"
			rc=1
			continue
		fi
		for field in '<description>' '<tags>whatsapp whatsmeow ' "<projectUrl>$repository_url</projectUrl>" \
			"<repository type=\"git\" url=\"$repository_url\"" '<readme>README.md</readme>' '<authors>Tarcisio Ortega</authors>'; do
			if ! grep -qF -- "$field" "$nuspec"; then
				annotate error "package metadata" "$id's nuspec has no $field"
				rc=1
			fi
		done
		if grep -qF '<description>Package Description</description>' "$nuspec"; then
			annotate error "package metadata" "$id has the SDK's default description; set Description in its csproj"
			rc=1
		fi
		readme="$release_tree/dotnet/src/$id/README.md"
		if ! cmp -s "$dir/README.md" "$readme"; then
			annotate error "package metadata" "$id's README.md is not $readme"
			rc=1
		fi
	done
	[ "$rc" -eq 0 ] && echo "every package has its description, tags, URLs, author and README"
	return "$rc"
}

# Each packaged binary is a release build of the target its RID folder names: CGO-free, -trimpath, and without the
# fake (ADR 0019), as go version -m reads it from the binaries the consumer restored.
packaged_binaries() {
	local targets target r goos goarch binary info setting rc=0
	targets="$(release_targets)" || return
	for target in $targets; do
		goos="${target%/*}" goarch="${target#*/}" r="$(rid "$target")"
		binary="$(native_package)/runtimes/$r/native/whatsmeow-net$(exe_of "$target")"
		info="$(go version -m "$binary")" || return
		for setting in "GOOS=$goos" "GOARCH=$goarch" CGO_ENABLED=0 -trimpath=true; do
			if ! grep -qE "^[[:space:]]+build[[:space:]]+$setting\$" <<<"$info"; then
				annotate error "packaged binaries" "runtimes/$r/native/whatsmeow-net is not a release build for $target: go version -m has no build $setting"
				rc=1
			fi
		done
		if grep -E '^[[:space:]]+build[[:space:]]+-tags=' <<<"$info" | grep -qF fakewhatsapp; then
			annotate error "fake in a packaged binary" "runtimes/$r/native/whatsmeow-net was built with -tags fakewhatsapp (ADR 0019)"
			rc=1
		fi
		if grep -E '^[[:space:]]+build[[:space:]]+-tags=' <<<"$info" | grep -qF livetest; then
			annotate error "livetest in a packaged binary" "runtimes/$r/native/whatsmeow-net was built with -tags livetest (ADR 0032)"
			rc=1
		fi
	done
	[ "$rc" -eq 0 ] && echo "every packaged binary is a CGO-free -trimpath build of its RID's target, none with -tags fakewhatsapp or livetest"
	return "$rc"
}

packaged_refuses_fake() {
	local binary stderr rc=0
	binary="$(native_package)/runtimes/$host_rid/native/whatsmeow-net$exe"
	stderr="$("$binary" --nonce n --fake-whatsapp --database "$work/refuse.db" </dev/null 2>&1 >/dev/null)" || rc=$?
	if [ "$rc" -ne 2 ] || [[ "$stderr" != *"never fakes WhatsApp"* ]]; then
		annotate error "fake in a packaged binary" "the packaged $host_rid binary did not refuse --fake-whatsapp with exit code 2 (exit $rc)"
		echo "$stderr"
		return 1
	fi
	echo "the packaged $host_rid binary refuses --fake-whatsapp"
}

# Each packaged consumer checks its native runtime and expected asset layout before start/GetInfo/stop.
consumer_start() { # path layout command...
	local path="$1" layout="$2"
	shift 2
	if ! (unset WHATSMEOW_NET_SIDECAR_PATH && "$@" start "$version" "$host_rid" "$layout"); then
		annotate error "consumer: $path" "the consumer did not find or start the packaged sidecar on this path"
		return 1
	fi
}

consumer_publish() { # path output publish-arguments...
	local path="$1" output="$2"
	shift 2
	if ! in_consumer dotnet publish app -c Release -warnaserror -o "$(native_path "$output")" "$@"; then
		annotate error "consumer: $path" "dotnet publish $* failed"
		return 1
	fi
}

install_portable() {
	local path="portable build and dotnet run, bin/runtimes/<rid>/native"
	if ! in_consumer dotnet build app -c Release -warnaserror; then
		annotate error "consumer: $path" "dotnet build failed"
		return 1
	fi
	consumer_start "$path" portable in_consumer dotnet run --project app -c Release --no-build --
}

install_portable_publish() {
	local path="portable publish" output="$work/portable"
	consumer_publish "$path" "$output" --self-contained false && consumer_start "$path" portable dotnet "$output/Consumer.dll"
}

install_rid() { # path output publish-arguments...
	local path="$1" output="$work/$2"
	shift 2
	consumer_publish "$path" "$output" "$@" && consumer_start "$path" flat "$output/Consumer$exe"
}

# Single-file: the sidecar is published beside the app, never bundled and extracted (ExcludeFromSingleFile, ADR 0023).
install_single_file() { # path output publish-arguments...
	local path="$1" output="$work/$2"
	shift 2
	consumer_publish "$path" "$output" -r "$host_rid" --self-contained true -p:PublishSingleFile=true "$@" || return
	if [ ! -f "$output/whatsmeow-net$exe" ]; then
		annotate error "consumer: $path" "whatsmeow-net$exe is not beside the app: the bundle took it"
		ls -l "$output"
		return 1
	fi
	consumer_start "$path" flat "$output/Consumer$exe"
}

# 32-bit Windows ships no binary: start fails naming both bundled paths (#4). Runs under WOW64 on the x64 runner.
install_win_x86() {
	local path="win-x86 self-contained publish, no binary ships" output="$work/win-x86"
	if [ "$host_rid" != win-x64 ]; then
		echo "not win-x64: the win-x86 consumer is skipped"
		return 0
	fi
	consumer_publish "$path" "$output" -r win-x86 --self-contained true || return
	if ! (unset WHATSMEOW_NET_SIDECAR_PATH && "$output/Consumer.exe" missing win-x86); then
		annotate error "consumer: $path" "start did not fail with SidecarUnavailableException naming both bundled paths"
		return 1
	fi
}

# On Alpine (musl), in the SDK container matching the Linux host architecture. Portable outputs use linux-<arch>;
# RID-specific outputs get the same binary through the RID graph (ADR 0058).
install_alpine() { # path script
	local path="$1" src="$work/alpine" image musl_rid="linux-musl-${host_rid##*-}"
	if ! linux_ci; then
		echo "not Linux CI: the Alpine consumer is skipped"
		return 0
	fi
	image="$(alpine_for_arch "${host_rid##*-}")" || return
	rm -rf "$src" && mkdir -p "$src" || return
	cp "$release_tree/global.json" "$release_tree/build/package-smoke/Program.cs" "$src/" || return
	if [ -n "$published" ]; then consumer_config "$nuget_org"; else consumer_config /feed; fi >"$src/nuget.config" || return
	if ! docker run --rm -e VERSION="$version" -e RID="$musl_rid" -v "$(cd "$package_source" && pwd):/feed:ro" -v "$src:/src:ro" "$image" sh -euc "
		cp -r /src /work && cd /work && export DOTNET_NOLOGO=true DOTNET_CLI_TELEMETRY_OPTOUT=true
		dotnet new console -o app -n Consumer --no-restore && cp Program.cs app/
		dotnet add app/Consumer.csproj package Whatsmeow.Client --version \"\$VERSION\"
		$2"; then
		annotate error "consumer: $path" "the consumer did not find or start the packaged sidecar on Alpine"
		return 1
	fi
}

# Every entry of a tree with its kind: a link with its target (never followed), an executable file (not on Windows, whose
# checkouts do not keep the bit), a directory, a file.
tree_listing() { # dir
	local windows=false
	case "$(uname -s)" in MINGW* | MSYS* | CYGWIN*) windows=true ;; esac
	(cd "$1" && find . -mindepth 1 | LC_ALL=C sort | while IFS= read -r e; do
		if [ -L "$e" ]; then echo "l $e -> $(readlink "$e")"
		elif [ -d "$e" ]; then echo "d $e"
		elif [ "$windows" = false ] && [ -x "$e" ]; then echo "x $e"
		else echo "f $e"; fi
	done)
}

# Two trees with the same entries, kinds and link targets, and every file's bytes equal.
same_tree() { # reference got
	local a b e
	a="$(tree_listing "$1")" b="$(tree_listing "$2")" || return
	if [ "$a" != "$b" ]; then
		diff <(echo "$a") <(echo "$b") | head -20
		return 1
	fi
	while IFS= read -r e; do
		case "$e" in f\ * | x\ *) cmp -s "$1/${e#? }" "$2/${e#? }" || { echo "bytes differ: ${e#? }"; return 1; } ;; esac
	done <<<"$a"
}

# The source archive holds exactly the release commit's files, byte for byte, plus the vendored modules of sidecar/:
# compared with that commit's own git archive (HEAD for a feed, the tag for a published release). The vendored modules
# are checked by source_build (built from them alone) and source_corresponds (the packaged binary's module versions).
source_contents() {
	local archive prefix="whatsmeow-net_$version" entries expected actual got="$work/source-check" ref="$work/source-ref" rc=0
	archive="$package_source/$(source_archive_name)"
	if [ ! -f "$archive" ]; then
		annotate error "source archive" "${published:-the feed} has no $(source_archive_name), the Corresponding Source (ADR 0016)"
		return 1
	fi
	entries="$(tar -tzf "$archive" | tr -d '\r' | grep -v '/$')" || return
	expected="$(git -c core.quotePath=false ls-tree -r --name-only "$release_commit" | sed "s#^#$prefix/#" | LC_ALL=C sort)" || return
	actual="$(grep -v "^$prefix/sidecar/vendor/" <<<"$entries" | LC_ALL=C sort)"
	if [ "$expected" != "$actual" ]; then
		annotate error "source archive" "$(source_archive_name) does not hold exactly the files of $release_commit"
		diff <(echo "$expected") <(echo "$actual") | head -20
		return 1
	fi
	if ! grep -qx "$prefix/sidecar/vendor/modules.txt" <<<"$entries"; then
		annotate error "source archive" "$(source_archive_name) has no go mod vendor output for sidecar/"
		return 1
	fi
	rm -rf "$got" "$ref" && mkdir -p "$got" "$ref" || return
	{ tar -xzf "$archive" -C "$got" && rm -rf "${got:?}/$prefix/sidecar/vendor" && git archive "$release_commit" | tar -x -C "$ref"; } || rc=$?
	if [ "$rc" -eq 0 ] && ! same_tree "$ref" "$got/$prefix"; then
		annotate error "source archive" "$(source_archive_name) holds the files of $release_commit, but not their bytes, kinds or link targets"
		rc=1
	fi
	rm -rf "$got" "$ref"
	[ "$rc" -eq 0 ] || return "$rc"
	echo "$(source_archive_name) holds the $(wc -l <<<"$expected" | tr -d ' ') files of $release_commit, byte for byte, and $(grep -c "^$prefix/sidecar/vendor/" <<<"$entries") vendored files"
}

# Build whatsmeow-net for every release target from the source archive with no network, the release flags and the
# archive's vendored modules only, into $source_out/<rid>/. On Linux CI it builds in a container without a network
# (--network none, observed: the container has only lo); elsewhere GOPROXY=off, -mod=vendor and an empty module cache
# leave the go command no way to download (inferred: from the go command's documentation, the host still has a network).
source_build() {
	local archive src="$work/source" prefix="whatsmeow-net_$version" targets target builds want gocache gomodcache image
	archive="$package_source/$(source_archive_name)"
	source_out="$work/source-out"
	targets="$(release_targets)" || return
	want="$(sed -n 's/^toolchain //p' "$release_tree/sidecar/go.mod")"
	# The release's Go: a later checker revision with another toolchain cannot rebuild it as released (ADR 0059).
	if [ "$(go env GOVERSION)" != "$want" ]; then
		annotate error "source archive" "go is $(go env GOVERSION), the release's sidecar/go.mod pins $want: run the checker from a revision with that toolchain"
		return 1
	fi
	rm -rf "$src" "$source_out" && mkdir -p "$src" "$source_out" || return
	tar -xzf "$archive" -C "$src" || return
	builds="$(for target in $targets; do echo "$target $(rid "$target") $(exe_of "$target")"; done)"
	if linux_ci; then
		image="$(alpine_for_arch "${host_rid##*-}")" || return
		# The pinned SDK image only provides a Linux without a network; the Go is mise's own, mounted read-only.
		docker run --rm --network none --user "$(id -u):$(id -g)" -e BUILDS="$builds" -e VERSION="$version" -e WANT="$want" \
			-v "$(go env GOROOT):/goroot:ro" -v "$src/$prefix:/src:ro" -v "$source_out:/out" "$image" sh -euc '
			if [ "$(ls /sys/class/net)" != lo ]; then echo "the container has a network: $(ls /sys/class/net)"; exit 1; fi
			export PATH="/goroot/bin:$PATH" GOTOOLCHAIN=local GOPROXY=off GOFLAGS=-mod=vendor GOSUMDB=off CGO_ENABLED=0 \
				HOME=/tmp GOCACHE=/tmp/go-build GOMODCACHE=/tmp/go-mod
			if [ "$(go env GOVERSION)" != "$WANT" ]; then echo "go is $(go env GOVERSION), sidecar/go.mod pins $WANT"; exit 1; fi
			cd /src/sidecar
			echo "$BUILDS" | while read -r target rid exe; do
				GOOS="${target%/*}" GOARCH="${target#*/}" go build -trimpath -ldflags "-s -w -X main.version=$VERSION" -o "/out/$rid/whatsmeow-net$exe" ./cmd/whatsmeow-net
				echo "built whatsmeow-net $VERSION for $target from the source archive, in a container with no network"
			done' || return
		return 0
	fi
	mkdir -p "$work/source-gocache" "$work/source-gomod" || return
	gocache="$(native_path "$work/source-gocache")" gomodcache="$(native_path "$work/source-gomod")"
	(
		export GOPROXY=off GOFLAGS=-mod=vendor GOSUMDB=off CGO_ENABLED=0 GOCACHE="$gocache" GOMODCACHE="$gomodcache"
		echo "$builds" | while read -r target r suffix; do
			GOOS="${target%/*}" GOARCH="${target#*/}" go -C "$(native_path "$src/$prefix/sidecar")" build -trimpath -ldflags "-s -w -X main.version=$version" -o "$(native_path "$source_out/$r/whatsmeow-net$suffix")" ./cmd/whatsmeow-net || exit
			echo "built whatsmeow-net $version for $target from the source archive, with GOPROXY=off and -mod=vendor"
		done
	)
}

# The modules a binary links, as go version -m reads them: path and version, one per line.
linked_deps() { # binary
	go version -m "$1" | awk '$1 == "dep" { print $2, $3 }'
}

# The archive is the source of the packaged binaries: each binary built from it links the same module versions as
# the packaged one (go version -m), and the host's reports the release version through GetInfo. Both lists must name
# libsignal, so two empty lists never compare equal.
source_corresponds() {
	local targets target r packaged built packaged_deps built_deps rc=0
	targets="$(release_targets)" || return
	for target in $targets; do
		r="$(rid "$target")"
		packaged="$(native_package)/runtimes/$r/native/whatsmeow-net$(exe_of "$target")"
		built="$source_out/$r/whatsmeow-net$(exe_of "$target")"
		packaged_deps="$(linked_deps "$packaged")" built_deps="$(linked_deps "$built")"
		if ! grep -q '^go\.mau\.fi/libsignal ' <<<"$packaged_deps" || ! grep -q '^go\.mau\.fi/libsignal ' <<<"$built_deps"; then
			annotate error "source archive" "go version -m lists no go.mau.fi/libsignal for $target in the packaged binary or the one built from the archive, so the comparison would prove nothing"
			rc=1
		elif ! diff <(echo "$packaged_deps") <(echo "$built_deps"); then
			annotate error "source archive" "whatsmeow-net for $target built from the archive links other modules than the packaged one"
			rc=1
		fi
	done
	[ "$rc" -eq 0 ] || return "$rc"
	echo "every binary built from the archive links the module versions of the packaged one"
	if ! (cd "$work" && NUGET_PACKAGES="$(native_path "$packages")" WHATSMEOW_NET_SIDECAR_PATH="$(native_path "$source_out/$host_rid/whatsmeow-net$exe")" \
		dotnet run --project app -c Release --no-build -- start "$version"); then
		annotate error "source archive" "whatsmeow-net built from the archive for $host_rid did not report $version through GetInfo"
		return 1
	fi
}

# The binary on a noexec mount: the client runs a copy from its local application data (ARCHITECTURE §8.5). Linux CI
# provides the mount (noexec_mount); elsewhere it is skipped.
install_noexec() {
	local path="build output on a noexec mount, copy fallback" output copy="$work/xdg/whatsmeow-net/$version/whatsmeow-net"
	if [ -z "${WHATSMEOW_NET_TEST_NOEXEC_DIR:-}" ]; then
		if linux_ci; then
			annotate error "consumer: $path" "Linux CI must provide a noexec mount (noexec_mount)"
			return 1
		fi
		echo "no noexec mount: the noexec consumer is skipped"
		return 0
	fi
	output="$WHATSMEOW_NET_TEST_NOEXEC_DIR/consumer"
	if ! in_consumer dotnet build app -c Release -warnaserror -o "$output"; then
		annotate error "consumer: $path" "dotnet build failed"
		return 1
	fi
	XDG_DATA_HOME="$work/xdg" consumer_start "$path" portable dotnet "$output/Consumer.dll" || return
	if [ ! -f "$copy" ]; then
		annotate error "consumer: $path" "the sidecar started, but not from the copy $copy"
		return 1
	fi
	echo "the sidecar ran from $copy"
}

# External mode (ADR 0046): a consumer that leaves the native package's assets out has no sidecar binary in its
# publish output, and reaches a sidecar the feed's own binary runs standalone, as an orchestrator would. Linux and
# macOS: the background process and its kill are POSIX.
install_external() {
	local path="External mode, the native assets excluded" app="$work/external-app" output="$work/external" dir="$work/standalone"
	local os="${host_rid%%-*}" binary pid rc=0
	if [ "$os" = win ]; then
		echo "Windows: External mode's consumer runs on Linux and macOS"
		return 0
	fi
	cp -R "$work/app" "$app" && rm -rf "${app:?}/bin" "${app:?}/obj" || return
	sed -i.bak "s|</Project>|  <ItemGroup><PackageReference Include=\"$native_id\" Version=\"[$version]\" ExcludeAssets=\"all\" /></ItemGroup>\n</Project>|" "$app/Consumer.csproj" || return
	rm -f "$app/Consumer.csproj.bak"
	if ! in_consumer dotnet publish external-app -c Release -warnaserror -r "$host_rid" --self-contained false -o "$output"; then
		annotate error "consumer: $path" "dotnet publish failed"
		return 1
	fi
	if [ -n "$(find "$output" -name 'whatsmeow-net*')" ]; then
		annotate error "consumer: $path" "the publish output holds a sidecar binary although the native package's assets are excluded"
		find "$output" -name 'whatsmeow-net*'
		return 1
	fi

	binary="$(native_package)/runtimes/$host_rid/native/whatsmeow-net"
	mkdir -p "$dir" && cp "$binary" "$dir/whatsmeow-net" && chmod 0755 "$dir/whatsmeow-net" || return
	od -An -tx1 -N32 /dev/urandom | tr -d ' \n' >"$dir/token" && chmod 0600 "$dir/token" || return
	"$dir/whatsmeow-net" --database "$dir/whatsmeow-net.db" --listen "unix:$dir/s" --token-file "$dir/token" </dev/null >"$dir/stdout" 2>"$dir/stderr" &
	pid=$!
	if ! (unset WHATSMEOW_NET_SIDECAR_PATH && "$output/Consumer" external "$version" "unix:$dir/s" "$dir/token"); then
		annotate error "consumer: $path" "the consumer did not reach the standalone sidecar"
		cat "$dir/stderr"
		rc=1
	fi
	kill "$pid" && wait "$pid" || rc=1
	[ "$rc" -eq 0 ] && echo "no sidecar binary in $output; the consumer reached the standalone one"
	return "$rc"
}

# A failure that leaves no per-form row gets one per RID it stands for: a JIT setup on a Linux CI host also stands for
# the Alpine RID of its architecture, whose forms then do not run; an AOT failure is its RID's. For nuget.org it adds
# restore's NU1101/NU1102 lines naming a Whatsmeow package, which say nuget.org did not serve it when restore ran (not why).
fail_rows() { # JIT|AOT step [log]
	local rids lines line r
	# Its own host detection: a failure can come before consumer_setup or job_aot found the host.
	if [ -z "${native_host_rid:-}" ]; then native_host >/dev/null 2>&1 || true; fi
	if [ "$1" = JIT ]; then
		rids="${native_host_rid:-host}"
		if linux_ci && [ -n "${native_host_rid:-}" ]; then rids="$rids linux-musl-${native_host_rid##*-}"; fi
	else
		rids="${aot_rid:-${native_host_rid:-host}}"
	fi
	lines=""
	if [ -n "${published:-}" ] && [ -n "${3:-}" ] && [ -f "$3" ]; then
		lines="$(grep -E 'NU110[12]' "$3" | grep -F 'Whatsmeow.' | sed 's/^.*\(NU110[12]\)/\1/' | sort -u | paste -sd';' -)"
		if [ -n "$lines" ]; then annotate error "nuget.org" "nuget.org did not serve a Whatsmeow package at $version when restore ran: $lines"; fi
	fi
	for r in $rids; do
		line="$r | $1 | $2 | FAIL | ${published:-feed} ${version:-?}"
		if [ "$1" = JIT ]; then line="$line | no consumer form ran"; fi
		if [ -n "$lines" ]; then line="$line; nuget.org did not serve, when restore ran: $lines"; fi
		echo "$line"
		if [ -n "${GITHUB_STEP_SUMMARY:-}" ]; then echo "* $line" >>"$GITHUB_STEP_SUMMARY"; fi
	done
	return 0
}

# Record a result for each consumer form only after its process has exited, which awaits sidecar disposal.
install_check() { # rid where path command...
	local consumer_rid="$1" where="$2" path="$3" result=PASS
	shift 3
	check "consumer: $consumer_rid $path" "$@"
	local rc=$?
	if [ "$rc" -ne 0 ]; then result=FAIL; where="execution unconfirmed ($where)"; fi
	local line="$consumer_rid | JIT | $path | $result | $where | ${published:-feed} $version"
	echo "$line"
	if [ -n "${GITHUB_STEP_SUMMARY:-}" ]; then echo "* $line" >>"$GITHUB_STEP_SUMMARY"; fi
	return "$rc"
}

job_install() { # feed
	check "install: a new consumer of $1" consumer_setup "$1" || { fail_rows JIT "consumer setup" "$work/restore.log"; return; }
	check "install: Whatsmeow.Client pins the native package at exactly its version" packaged_pins
	check "install: Whatsmeow.Extensions.Hosting pins Whatsmeow.Client at exactly its version" packaged_hosting_pin
	check "install: Whatsmeow.Testing pins Whatsmeow.Client and Whatsmeow.Extensions.Hosting at exactly its version" packaged_testing_pins
	check "install: Whatsmeow.Client pins Whatsmeow.Protocol, and Whatsmeow.Protocol pins Whatsmeow.Protocol.WhatsApp, at exactly its version" packaged_protocol_pins
	check "install: the native package holds exactly the binaries, its buildTransitive targets, its README and the GPL files" packaged_files
	check "install: the native package's README and GPL files are the repository's, and COPYING is the GPL-3.0 text (ADR 0016)" packaged_notices
	check "install: GPL-3.0-only on the native package, MPL-2.0 on the .NET packages (ADR 0016)" packaged_licences
	check "install: every package's description, tags, URLs, author and README (ADR 0054)" packaged_metadata
	check "install: every packaged binary is a CGO-free -trimpath release build of its target, none with -tags fakewhatsapp or livetest (ADR 0019, 0032)" packaged_binaries
	check "install: the packaged binary refuses --fake-whatsapp" packaged_refuses_fake
	install_check "$host_rid" "native $host_os_arch" "portable build and dotnet run" install_portable
	install_check "$host_rid" "native $host_os_arch" "portable publish" install_portable_publish
	install_check "$host_rid" "native $host_os_arch" "RID-specific framework-dependent publish" install_rid "RID-specific framework-dependent publish" fd -r "$host_rid" --self-contained false
	install_check "$host_rid" "native $host_os_arch" "RID-specific self-contained publish" install_rid "RID-specific self-contained publish" sc -r "$host_rid" --self-contained true
	install_check "$host_rid" "native $host_os_arch" "single-file publish" install_single_file "single-file publish" single
	install_check "$host_rid" "native $host_os_arch" "single-file publish with IncludeNativeLibrariesForSelfExtract" install_single_file "single-file publish with IncludeNativeLibrariesForSelfExtract" single-native -p:IncludeNativeLibrariesForSelfExtract=true
	check "consumer: win-x86, where no binary ships" install_win_x86
	check "consumer: External mode, the native assets excluded (ADR 0046)" install_external
	if linux_ci; then
		local musl_rid="linux-musl-${host_rid##*-}" where="native $host_os_arch, Alpine container"
		install_check "$musl_rid" "$where" "portable build and dotnet run" install_alpine "portable build on Alpine" \
			"dotnet build app -c Release -warnaserror && dotnet run --project app -c Release --no-build -- start \"\$VERSION\" \"\$RID\" portable"
		install_check "$musl_rid" "$where" "portable publish" install_alpine "portable publish on Alpine" \
			"dotnet publish app -c Release -warnaserror --self-contained false -o portable && dotnet portable/Consumer.dll start \"\$VERSION\" \"\$RID\" portable"
		install_check "$musl_rid" "$where" "RID-specific framework-dependent publish" install_alpine "RID-specific framework-dependent publish on Alpine" \
			"dotnet publish app -c Release -warnaserror -r \"\$RID\" --self-contained false -o fd && ./fd/Consumer start \"\$VERSION\" \"\$RID\" flat"
		install_check "$musl_rid" "$where" "RID-specific self-contained publish" install_alpine "RID-specific self-contained publish on Alpine" \
			"dotnet publish app -c Release -warnaserror -r \"\$RID\" --self-contained true -o sc && ./sc/Consumer start \"\$VERSION\" \"\$RID\" flat"
	fi
	check "noexec mount for the copy fallback consumer" noexec_mount
	check "consumer: build output on a noexec mount" install_noexec
	check "source: the archive holds the commit and the vendored modules (ADR 0016)" source_contents &&
		check "source: whatsmeow-net builds from the archive for every release target, without a network" source_build &&
		check "source: the archive's binaries link the packaged module versions, and GetInfo reports the release version" source_corresponds
	rm -rf "$work"
}

# Native AOT from the feed, on each target RID's own runner (ADR 0056): tests/AotSmoke, copied out of the repository
# with AotSmokeFromFeed, references the feed's packages instead of the projects, publishes for the RID with zero trim
# and AOT warnings, and runs there: the packaged sidecar beside the app, GetInfo at the feed's version, the hosting path,
# and the RID it runs as, natively. A linux-musl-* RID publishes and runs in the Alpine SDK container of its
# architecture, on a host of that architecture, never under emulation.

# The project and the versions that feed's packages and the repository pin, in a directory of its own outside the
# repository, so none of our MSBuild files, central versions or nuget.config apply (consumer_setup).
aot_setup() { # feed | nuget.org:<version>, rid
	local tfm hosting
	aot_rid="$2"
	package_origin "$1" || return
	tfm="$(sed -n 's|.*<TargetFramework>\(.*\)</TargetFramework>.*|\1|p' "$release_tree/dotnet/Directory.Build.props")"
	hosting="$(sed -n 's|.*<PackageVersion Include="Microsoft.Extensions.Hosting" Version="\([^"]*\)".*|\1|p' "$release_tree/dotnet/Directory.Packages.props")"
	if [ -z "$tfm" ] || [ -z "$hosting" ]; then
		annotate error "aot" "no TargetFramework in dotnet/Directory.Build.props or no Microsoft.Extensions.Hosting in dotnet/Directory.Packages.props"
		return 1
	fi
	packages="$work/packages"
	mkdir -p "$work/src/AotSmoke" || return
	cp "$release_tree/dotnet/tests/AotSmoke/AotSmoke.csproj" "$release_tree"/dotnet/tests/AotSmoke/*.cs "$work/src/AotSmoke/" && cp "$release_tree/global.json" "$work/src/" || return
	cat >"$work/src/Directory.Build.props" <<EOF || return
<Project>
	<PropertyGroup>
		<TargetFramework>$tfm</TargetFramework>
		<ImplicitUsings>enable</ImplicitUsings>
		<Nullable>enable</Nullable>
		<AotSmokeFromFeed>true</AotSmokeFromFeed>
	</PropertyGroup>
</Project>
EOF
	cat >"$work/src/Directory.Packages.props" <<EOF || return
<Project>
	<PropertyGroup>
		<ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally>
	</PropertyGroup>
	<ItemGroup>
		<PackageVersion Include="Microsoft.Extensions.Hosting" Version="$hosting" />
		<PackageVersion Include="Whatsmeow.Client" Version="$version" />
		<PackageVersion Include="Whatsmeow.Extensions.Hosting" Version="$version" />
		<PackageVersion Include="Whatsmeow.Testing" Version="$version" />
	</ItemGroup>
</Project>
EOF
	case "$published:$aot_rid" in
		nuget.org:*) consumer_config "$nuget_org" >"$work/src/nuget.config" ;;
		:linux-musl-*) consumer_config /feed >"$work/src/nuget.config" ;;
		*) consumer_config "$origin" >"$work/src/nuget.config" ;;
	esac || return
	aot_feed="$package_source"
	echo "AotSmoke from $origin at $version, for $aot_rid, in $work"
}

# The RID must be the host's OS and architecture: Native AOT never crosses operating systems, and an app of another
# architecture would run emulated (Rosetta, Prism, or docker's emulation, which the app cannot see). RUNNER_ARCH, when
# set, is the runner's own; Git Bash's uname -m may not be on windows-11-arm [inferred].
aot_native_host() {
	native_host || return
	if [ "$native_host_rid" != "${aot_rid/linux-musl-/linux-}" ]; then
		annotate error "aot $aot_rid" "$aot_rid runs on its native OS and architecture only, never emulated; this is $host_os_arch"
		return 1
	fi
	echo "$aot_rid on a native $host_os_arch host"
}

aot_publish_run() {
	local exe="" out="$work/src/out"
	case "$aot_rid" in linux-musl-*) aot_musl; return ;; win-*) exe=.exe ;; esac
	# Not on the shared compiler server, as in_consumer.
	if ! (unset WHATSMEOW_NET_SIDECAR_PATH && cd "$work/src" && NUGET_PACKAGES="$(native_path "$packages")" NUGET_HTTP_CACHE_PATH="$(native_path "$work/http-cache")" UseSharedCompilation=false \
		dotnet publish AotSmoke -c Release -warnaserror -r "$aot_rid" -o "$(native_path "$out")" 2>&1 | tee "$work/publish.log"); then
		annotate error "aot $aot_rid" "the Native AOT publish from ${published:-the feed} failed, or had a trim or AOT warning"
		fail_rows AOT "Native AOT publish" "$work/publish.log"
		return 1
	fi
	# A publish without Native AOT leaves the IL assembly beside the app; the app checks it too.
	if [ -f "$out/AotSmoke.dll" ]; then
		annotate error "aot $aot_rid" "not a Native AOT publish: $out holds AotSmoke.dll"
		fail_rows AOT "Native AOT layout (AotSmoke.dll present)"
		return 1
	fi
	if [ ! -f "$out/whatsmeow-net$exe" ]; then
		annotate error "aot $aot_rid" "the packaged whatsmeow-net$exe is not beside the app"
		fail_rows AOT "Native AOT layout (whatsmeow-net$exe missing)"
		ls -l "$out"
		return 1
	fi
	if ! (unset WHATSMEOW_NET_SIDECAR_PATH && "$out/AotSmoke$exe" "$version" "$aot_rid"); then
		annotate error "aot $aot_rid" "the Native AOT app did not start the packaged sidecar, run the hosting path, or run natively as $aot_rid"
		fail_rows AOT "AotSmoke run"
		return 1
	fi
	aot_summary "$host_os_arch"
}

# In the Alpine SDK container of the RID's architecture, which aot_native_host made sure is the host's.
aot_musl() {
	local arch="${aot_rid##*-}" image
	image="$(alpine_for_arch "$arch")" || return
	if ! docker run --rm -e VERSION="$version" -e RID="$aot_rid" -v "$aot_feed:/feed:ro" -v "$work/src:/src:ro" "$image" sh -euc '
		apk add --no-cache clang build-base zlib-dev >/dev/null
		cp -r /src /work && cd /work && export DOTNET_NOLOGO=true DOTNET_CLI_TELEMETRY_OPTOUT=true
		dotnet publish AotSmoke -c Release -warnaserror -r "$RID" -o out
		test ! -f out/AotSmoke.dll || { echo "not a Native AOT publish: out holds AotSmoke.dll" >&2; exit 1; }
		test -f out/whatsmeow-net || { echo "the packaged whatsmeow-net is not beside the app" >&2; ls -l out; exit 1; }
		./out/AotSmoke "$VERSION" "$RID"' 2>&1 | tee "$work/publish.log"; then
		annotate error "aot $aot_rid" "the Native AOT publish or run on Alpine failed"
		fail_rows AOT "Native AOT publish and run on Alpine" "$work/publish.log"
		return 1
	fi
	aot_summary "$host_os_arch (native, Alpine container)"
}

aot_summary() { # where
	local line="AotSmoke $aot_rid from ${published:-feed} $version: PASS, RID-specific Native AOT with 0 trim and AOT warnings, ran natively on $1; portable AOT is not applicable (Native AOT requires a concrete RID)"
	echo "$line"
	if [ -n "${GITHUB_STEP_SUMMARY:-}" ]; then echo "- $line" >>"$GITHUB_STEP_SUMMARY"; fi
}

aot_work() {
	work="$(mktemp -d)"
}

# One exit path, so the temporary directory goes whichever check fails.
job_aot() { # feed rid
	aot_rid="$2"
	check "aot: $2 on a native host" aot_native_host || return
	check "aot: a temporary directory" aot_work || return
	if check "aot: AotSmoke from $1 for $2" aot_setup "$1" "$2"; then
		check "aot: Native AOT publish for $2 with zero trim and AOT warnings, and run (ADR 0056)" aot_publish_run
	else
		fail_rows AOT "AotSmoke setup"
	fi
	rm -rf "$work"
}

job_package() {
	local before
	before="$(tree_state)"
	check "dotnet restore (locked)" dotnet restore dotnet/Whatsmeow.slnx --locked-mode &&
		check "one version for every package and binary, a pre-release when HEAD has no tag (ADR 0023)" package_version &&
		check "a stable release ships its public API (ADR 0055)" release_api_shipped &&
		check "the package validation baseline, the last stable release below this version (ADR 0055)" package_baseline &&
		check "restore the baseline packages from nuget.org (ADR 0055)" restore_baseline &&
		check "whatsmeow-net for every release target" build_sidecars &&
		check "dotnet pack into the local feed $package_feed" pack_feed &&
		check "the Corresponding Source archive into the feed (ADR 0016)" source_archive &&
		{
			# CI runs both on every target host in install.yml, win-x64 among them (ADR 0056, 0058).
			if [ "${GITHUB_ACTIONS:-}" != true ]; then
				job_install "$package_feed"
				job_aot "$package_feed" "$(rid "$(go env GOHOSTOS)/$(go env GOHOSTARCH)")"
			fi
		}
	check "the package job left the tree unchanged" tree_unchanged "$before" "the package job"
}

usage() {
	echo "usage: bash build/ci.sh setup|contract|go|dotnet|external|package|all, bash build/ci.sh install <feed>|nuget.org:<version>, bash build/ci.sh aot <feed>|nuget.org:<version> <rid>, or bash build/ci.sh release-pins v<version>" >&2
	exit 2
}

case "${1:-}" in
	install) [ $# -eq 2 ] || usage ;;
	aot) [ $# -eq 3 ] || usage ;;
	release-pins) [ $# -eq 2 ] || usage; release_pins "$2"; exit ;;
	*) [ $# -eq 1 ] || usage ;;
esac

# A published release is checked with its own toolchain (ADR 0059).
pins_at=""
case "$1:${2:-}" in
	install:nuget.org:* | aot:nuget.org:*)
		if ! release_commit_of "${2#nuget.org:}"; then
			published=nuget.org version="${2#nuget.org:}"
			if [ "$1" = install ]; then fail_rows JIT "release tag"; else aot_rid="$3" && fail_rows AOT "release tag"; fi
			exit 1
		fi
		pins_at="$release_commit"
		;;
esac
check "Go from mise equals the go.mod toolchain" go_toolchain ${pins_at:+"$pins_at"} || {
	echo "fix the toolchain first" >&2
	exit 1
}

case "$1" in
	setup) ;;
	contract) job_contract ;;
	go) job_go ;;
	dotnet) job_dotnet ;;
	external) job_external ;;
	package) job_package ;;
	install) job_install "$2" ;;
	aot) job_aot "$2" "$3" ;;
	all) job_contract; job_go; job_dotnet; job_external; job_package ;;
	*) usage ;;
esac

if [ "${#failures[@]}" -gt 0 ]; then
	echo
	echo "failed checks:"
	printf '  %s\n' "${failures[@]}"
	exit 1
fi
echo "all checks passed"
