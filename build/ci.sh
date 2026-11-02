#!/usr/bin/env bash
# The checks CI runs, one subcommand per CI job; .github/workflows/ci.yml calls this and nothing else.
# Run it before pushing: bash build/ci.sh all (or contract, go, dotnet, package; install <feed>). Works in Git Bash on
# Windows.
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
	fake="$(grep -E '/internal/fakewhatsapp(/|$)|/gen/whatsmeow/fake/' <<<"$deps")"
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
# dotnet/src, all of which ship, may contain a type in its namespace, which buf.gen.yaml gives every generated fake
# type, messages and service alike. The test assembly must, or this grep proves nothing.
shipped_have_no_fake() {
	local project name dll rc=0 namespace=Whatsmeow.IntegrationTests.Fake
	if ! grep -q -a -F "$namespace" dotnet/artifacts/bin/Whatsmeow.IntegrationTests/release/Whatsmeow.IntegrationTests.dll; then
		annotate error "fake in a shipped assembly" "Whatsmeow.IntegrationTests.dll has no $namespace type either, so this check cannot see one"
		return 1
	fi
	for project in dotnet/src/*/; do
		name="$(basename "$project")"
		dll="dotnet/artifacts/bin/$name/release/$name.dll"
		if [ ! -f "$dll" ]; then
			annotate error "fake in a shipped assembly" "$dll was not built"
			rc=1
		elif grep -q -a -F "$namespace" "$dll"; then
			annotate error "fake in a shipped assembly" "$dll contains the fake WhatsApp control"
			rc=1
		fi
	done
	[ "$rc" -eq 0 ] && echo "no assembly under dotnet/src contains the fake WhatsApp control"
	return "$rc"
}

# The live tests (docs/guidelines/tests.md Live WhatsApp) are contained: [LiveFact] appears only in
# Whatsmeow.LiveTests, Explicit, a declared exception to the banned list, only in its LiveFactAttribute.cs, every
# test there is a [LiveFact] (no [Fact], [FactAttribute], [Xunit.Fact] or [Theory]), and QRCoder, which it alone uses, is in no shipped project.
# Each grep has a positive control, or it could prove nothing.
live_tests_contained() {
	local live=dotnet/tests/Whatsmeow.LiveTests attribute=dotnet/tests/Whatsmeow.LiveTests/LiveFactAttribute.cs
	local explicit='(^|[^A-Za-z0-9_])Explicit[[:space:]]*=[^=]' found lock rc=0
	if ! grep -rqF --include='*.cs' '[LiveFact' "$live" || ! grep -qE "$explicit" "$attribute" ||
		! grep -qF '"QRCoder"' "$live/packages.lock.json"; then
		annotate error "live tests" "no [LiveFact] in $live, no Explicit in $attribute, or no QRCoder in its lock file, so this check cannot see them"
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
			annotate error "live tests" "$lock depends on QRCoder, which only $live may use"
			rc=1
		fi
	done
	[ "$rc" -eq 0 ] && echo "LiveFact, Explicit, the live tests and QRCoder stay in $live"
	return "$rc"
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
			check "no shipped assembly contains the fake WhatsApp control (ADR 0019)" shipped_have_no_fake
			# Never the live tests, even with their variables exported in the shell that runs this.
			rm -f dotnet/artifacts/TestResults/media-transfers.txt
			check "dotnet test" env -u WHATSMEOW_LIVE_DATABASE -u WHATSMEOW_LIVE_PHONE_NUMBER dotnet test --solution dotnet/Whatsmeow.slnx -c Release --no-build
			# The media transfer test's throughput and memory on each transport, for ARCHITECTURE §8.6 (ADR 0026).
			if [ -f dotnet/artifacts/TestResults/media-transfers.txt ]; then
				cat dotnet/artifacts/TestResults/media-transfers.txt
			fi
			check "AotSmoke publish (zero trim and AOT warnings)" dotnet publish dotnet/tests/AotSmoke/AotSmoke.csproj -c Release --no-restore -p:ContinuousIntegrationBuild=true &&
				check "AotSmoke run" aot_smoke_run
		}
	check "the dotnet job left the tree unchanged (a stale packages.lock.json shows here too)" tree_unchanged "$before" "the dotnet job"
}

# Native packages (ADR 0023). The package job builds whatsmeow-net for every release target at the client's version,
# packs every package into a local feed, and installs from it; CI packs on Windows, and the install job installs that
# feed on Linux and macOS, so packages built on Windows run there.
package_feed=dotnet/artifacts/package
sidecar_binaries=dotnet/artifacts/sidecar
alpine_image=mcr.microsoft.com/dotnet/sdk:10.0.403-alpine3.24-amd64@sha256:60a079c9d3ffc98b2968c3153b995ede679f7d6504e3ae7d2b68225a7fd84f5a

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
	go -C tools run ./check-licenses -targets | tr -d '\r'
}

# A path for dotnet: Git Bash on Windows hands POSIX paths to native programs only in arguments it recognizes.
native_path() {
	if command -v cygpath >/dev/null 2>&1; then cygpath -m "$1"; else printf '%s\n' "$1"; fi
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
	dotnet pack dotnet/Whatsmeow.slnx -c Release --no-restore -p:ContinuousIntegrationBuild=true -o "$package_feed" || return
	echo "$version" >"$package_feed/version"
	for nupkg in "$package_feed"/Whatsmeow.Sidecar.Native.*.nupkg; do
		line="$(basename "$nupkg"): $(wc -c <"$nupkg" | tr -d ' ') bytes"
		echo "$line"
		if [ -n "${GITHUB_STEP_SUMMARY:-}" ]; then echo "- $line" >>"$GITHUB_STEP_SUMMARY"; fi
	done
}

# A new console app in a directory of its own, outside the repository, so none of our MSBuild files, central versions
# or nuget.config apply. It restores Whatsmeow.* from the feed only (source mapping) into an empty packages folder, so it
# gets exactly what the feed holds, never a cached package of the same version.
consumer_setup() { # feed
	local feed
	feed="$(cd "$1" && pwd)" || return
	version="$(tr -d '\r' <"$feed/version")" || return
	work="$(mktemp -d)" || return
	packages="$work/packages"
	host="$(go env GOHOSTOS)/$(go env GOHOSTARCH)" || return
	host_rid="$(rid "$host")"
	exe="$(exe_of "$host")"
	cp global.json "$work/" || return
	consumer_config "$(native_path "$feed")" >"$work/nuget.config" || return
	in_consumer dotnet new console -o app -n Consumer || return
	cp build/package-smoke/Program.cs "$work/app/" || return
	in_consumer dotnet add app/Consumer.csproj package Whatsmeow.Client --version "$version" || return
	echo "a consumer of Whatsmeow.Client $version from $feed in $work"
}

consumer_config() { # feed
	cat <<EOF
<?xml version="1.0" encoding="utf-8"?>
<configuration>
	<packageSources>
		<clear />
		<add key="local" value="$1" />
		<add key="nuget.org" value="https://api.nuget.org/v3/index.json" protocolVersion="3" />
	</packageSources>
	<packageSourceMapping>
		<packageSource key="local"><package pattern="Whatsmeow.*" /></packageSource>
		<packageSource key="nuget.org"><package pattern="*" /></packageSource>
	</packageSourceMapping>
</configuration>
EOF
}

# Never with WHATSMEOW_NET_SIDECAR_PATH: an exported override would make every consumer path pass without the packages.
in_consumer() {
	(unset WHATSMEOW_NET_SIDECAR_PATH && cd "$work" && NUGET_PACKAGES="$(native_path "$packages")" "$@")
}

# The native package of a RID's OS (ADR 0023). A packages folder names it in lowercase.
native_id() { # linux|osx|win
	case "$1" in
		linux) echo Whatsmeow.Sidecar.Native.Linux ;;
		osx) echo Whatsmeow.Sidecar.Native.MacOS ;;
		win) echo Whatsmeow.Sidecar.Native.Windows ;;
	esac
}

lowercase() {
	tr '[:upper:]' '[:lower:]' <<<"$1"
}

native_package() { # linux|osx|win
	echo "$packages/$(lowercase "$(native_id "$1")")/$version"
}

packaged_pins() {
	local nuspec="$packages/whatsmeow.client/$version/whatsmeow.client.nuspec" os id rc=0
	if [ ! -f "$nuspec" ]; then
		annotate error "exact pins" "the consumer restored no $nuspec"
		return 1
	fi
	for os in linux osx win; do
		id="$(native_id "$os")"
		if ! grep -qF "<dependency id=\"$id\" version=\"[$version]\"" "$nuspec"; then
			annotate error "exact pins" "Whatsmeow.Client does not depend on $id at exactly [$version] (§13 invariant 12). _WhatsmeowExactProjectVersions in dotnet/Directory.Build.targets makes the pin by rewriting NuGet's internal _ProjectReferencesWithVersions item; after an SDK bump, check that NuGet.Build.Tasks.Pack.targets still fills it in _GetProjectReferenceVersions (ADR 0023)"
			rc=1
		fi
	done
	grep -F 'Whatsmeow.Sidecar.Native' "$nuspec"
	return "$rc"
}

# Each native package holds exactly its binaries, one per release target of its OS, and its buildTransitive targets,
# beside the files NuGet writes when it extracts a package: no build output, nothing else (ADR 0023).
packaged_files() {
	local targets target r os id name expected actual rc=0
	targets="$(release_targets)" || return
	for os in linux osx win; do
		id="$(native_id "$os")" name="$(lowercase "$(native_id "$os")")"
		expected="$({
			for target in $targets; do
				r="$(rid "$target")"
				if [ "${r%-*}" = "$os" ]; then echo "runtimes/$r/native/whatsmeow-net$(exe_of "$target")"; fi
			done
			printf '%s\n' "buildTransitive/$id.targets" .nupkg.metadata "$name.$version.nupkg" "$name.$version.nupkg.sha512" "$name.nuspec"
		} | LC_ALL=C sort)"
		actual="$(cd "$(native_package "$os")" && find . -type f | sed 's#^\./##' | LC_ALL=C sort)" || return
		if [ "$expected" != "$actual" ]; then
			annotate error "packaged files" "$id does not hold exactly its binaries for the release targets (go -C tools run ./check-licenses -targets) and buildTransitive/$id.targets"
			diff <(echo "$expected") <(echo "$actual")
			rc=1
		fi
	done
	[ "$rc" -eq 0 ] && echo "each native package holds exactly its binaries and its buildTransitive targets"
	return "$rc"
}

# Each packaged binary is a release build of the target its RID folder names: CGO-free, -trimpath, and without the
# fake (ADR 0019), as go version -m reads it from the binaries the consumer restored.
packaged_binaries() {
	local targets target r goos goarch binary info setting rc=0
	targets="$(release_targets)" || return
	for target in $targets; do
		goos="${target%/*}" goarch="${target#*/}" r="$(rid "$target")"
		binary="$(native_package "${r%-*}")/runtimes/$r/native/whatsmeow-net$(exe_of "$target")"
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
	done
	[ "$rc" -eq 0 ] && echo "every packaged binary is a CGO-free -trimpath build of its RID's target, none with -tags fakewhatsapp"
	return "$rc"
}

packaged_refuses_fake() {
	local binary stderr rc=0
	binary="$(native_package "${host_rid%-*}")/runtimes/$host_rid/native/whatsmeow-net$exe"
	stderr="$("$binary" --nonce n --fake-whatsapp --database "$work/refuse.db" </dev/null 2>&1 >/dev/null)" || rc=$?
	if [ "$rc" -ne 2 ] || [[ "$stderr" != *"never fakes WhatsApp"* ]]; then
		annotate error "fake in a packaged binary" "the packaged $host_rid binary did not refuse --fake-whatsapp with exit code 2 (exit $rc)"
		echo "$stderr"
		return 1
	fi
	echo "the packaged $host_rid binary refuses --fake-whatsapp"
}

# consumer_start PATH COMMAND...: COMMAND, given start <version>, must start the packaged sidecar; a failure names PATH.
consumer_start() {
	local path="$1"
	shift
	if ! (unset WHATSMEOW_NET_SIDECAR_PATH && "$@" start "$version"); then
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
	consumer_start "$path" in_consumer dotnet run --project app -c Release --no-build --
}

install_rid() { # path output publish-arguments...
	local path="$1" output="$work/$2"
	shift 2
	consumer_publish "$path" "$output" "$@" && consumer_start "$path" "$output/Consumer$exe"
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
	consumer_start "$path" "$output/Consumer$exe"
}

# 32-bit Windows ships no binary: start fails naming both bundled paths (#4). Runs under WOW64 on the x64 runner.
install_win_x86() {
	local path="win-x86 self-contained publish, no binary ships" output="$work/win-x86"
	if [ "$host_rid" != win-x64 ]; then
		if [ "${GITHUB_ACTIONS:-}" = true ] && [ "${host%/*}" = windows ]; then
			annotate error "consumer: $path" "Windows CI must run on win-x64, where win-x86 runs under WOW64; this is $host_rid"
			return 1
		fi
		echo "not win-x64: the win-x86 consumer is skipped"
		return 0
	fi
	consumer_publish "$path" "$output" -r win-x86 --self-contained true || return
	if ! (unset WHATSMEOW_NET_SIDECAR_PATH && "$output/Consumer.exe" missing win-x86); then
		annotate error "consumer: $path" "start did not fail with SidecarUnavailableException naming both bundled paths"
		return 1
	fi
}

# On Alpine (musl), in the SDK container: the portable build finds the linux-x64 binary (ARCHITECTURE §8.5, RESEARCH
# item 9), and a linux-musl-x64 publish gets it through the RID graph. CI runs it on linux-x64; elsewhere it is skipped.
install_alpine() { # path script
	local path="$1" src="$work/alpine"
	if ! linux_ci; then
		echo "not Linux CI: the Alpine consumer is skipped"
		return 0
	fi
	rm -rf "$src" && mkdir -p "$src" || return
	cp global.json build/package-smoke/Program.cs "$src/" || return
	consumer_config /feed >"$src/nuget.config" || return
	if ! docker run --rm -e VERSION="$version" -v "$(cd "$package_source" && pwd):/feed:ro" -v "$src:/src:ro" "$alpine_image" sh -euc "
		cp -r /src /work && cd /work && export DOTNET_NOLOGO=true DOTNET_CLI_TELEMETRY_OPTOUT=true
		dotnet new console -o app -n Consumer >/dev/null && cp Program.cs app/
		dotnet add app/Consumer.csproj package Whatsmeow.Client --version \"\$VERSION\" >/dev/null
		$2"; then
		annotate error "consumer: $path" "the consumer did not find or start the packaged sidecar on Alpine"
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
	XDG_DATA_HOME="$work/xdg" consumer_start "$path" dotnet "$output/Consumer.dll" || return
	if [ ! -f "$copy" ]; then
		annotate error "consumer: $path" "the sidecar started, but not from the copy $copy"
		return 1
	fi
	echo "the sidecar ran from $copy"
}

job_install() { # feed
	package_source="$1"
	check "install: a new consumer of the feed $1" consumer_setup "$1" || return
	check "install: Whatsmeow.Client pins each native package at exactly its version" packaged_pins
	check "install: each native package holds exactly its binaries and its buildTransitive targets" packaged_files
	check "install: every packaged binary is a CGO-free -trimpath release build of its target, none with -tags fakewhatsapp (ADR 0019)" packaged_binaries
	check "install: the packaged binary refuses --fake-whatsapp" packaged_refuses_fake
	check "consumer: portable build and dotnet run" install_portable
	check "consumer: RID-specific framework-dependent publish" install_rid "RID-specific framework-dependent publish" fd -r "$host_rid" --self-contained false
	check "consumer: RID-specific self-contained publish" install_rid "RID-specific self-contained publish" sc -r "$host_rid" --self-contained true
	check "consumer: single-file publish" install_single_file "single-file publish" single
	check "consumer: single-file publish with IncludeNativeLibrariesForSelfExtract" install_single_file "single-file publish with IncludeNativeLibrariesForSelfExtract" single-native -p:IncludeNativeLibrariesForSelfExtract=true
	check "consumer: win-x86, where no binary ships" install_win_x86
	check "consumer: portable build on Alpine" install_alpine "portable build on Alpine" \
		"dotnet build app -c Release -warnaserror && dotnet run --project app -c Release --no-build -- start \"\$VERSION\""
	check "consumer: linux-musl-x64 self-contained publish on Alpine" install_alpine "linux-musl-x64 self-contained publish on Alpine" \
		"dotnet publish app -c Release -warnaserror -r linux-musl-x64 --self-contained true -o musl && ./musl/Consumer start \"\$VERSION\""
	check "noexec mount for the copy fallback consumer" noexec_mount
	check "consumer: build output on a noexec mount" install_noexec
	rm -rf "$work"
}

job_package() {
	local before
	before="$(tree_state)"
	check "dotnet restore (locked)" dotnet restore dotnet/Whatsmeow.slnx --locked-mode &&
		check "one version for every package and binary, a pre-release when HEAD has no tag (ADR 0023)" package_version &&
		check "whatsmeow-net for every release target" build_sidecars &&
		check "dotnet pack into the local feed $package_feed" pack_feed &&
		job_install "$package_feed"
	check "the package job left the tree unchanged" tree_unchanged "$before" "the package job"
}

usage() {
	echo "usage: bash build/ci.sh setup|contract|go|dotnet|package|all, or bash build/ci.sh install <feed>" >&2
	exit 2
}

if [ "${1:-}" = install ]; then [ $# -eq 2 ] || usage; else [ $# -eq 1 ] || usage; fi

check "Go from mise equals the go.mod toolchain" go_toolchain || {
	echo "fix the toolchain first" >&2
	exit 1
}

case "$1" in
	setup) ;;
	contract) job_contract ;;
	go) job_go ;;
	dotnet) job_dotnet ;;
	package) job_package ;;
	install) job_install "$2" ;;
	all) job_contract; job_go; job_dotnet; job_package ;;
	*) usage ;;
esac

if [ "${#failures[@]}" -gt 0 ]; then
	echo
	echo "failed checks:"
	printf '  %s\n' "${failures[@]}"
	exit 1
fi
echo "all checks passed"
