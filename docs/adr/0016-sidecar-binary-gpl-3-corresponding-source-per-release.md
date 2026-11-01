# 0016. The sidecar binary is conveyed under GPL-3.0-only because it links libsignal; Corresponding Source ships with every release

- Status: Accepted (owner, 2026-10-31)
- Date: 2026-10-31
- Evidence: [ARCHITECTURE §14](../ARCHITECTURE.md#licence-audit-2026-10-31) question 9, issue #12

## Context

The sidecar binary ships inside our NuGet packages ([ADR 0007](0007-per-os-native-nuget-packages-exact-pins.md)), so every licence linked into it decides what we and our consumers owe.
RESEARCH "What still needs proof" item 1 left the licences of `go.mau.fi/libsignal` and `go.mau.fi/util` open.

The audit, run 2026-10-31 with Go 1.27.3 outside the repo, because `sidecar/` has no package until #4 [observed]:

- A throwaway module required exactly the planned graph: whatsmeow at the pin `v0.0.0-20261030195514-56e5ad0581ce`, grpc 1.85.0, protobuf 1.36.12, modernc.org/sqlite 1.61.1 and zerolog 1.35.1 (ARCHITECTURE §5).
- It imported and called whatsmeow, `store/sqlstore`, `types`, `types/events`, `util/log`, `proto/waE2E`, grpc, grpc `health`, `status`, `codes`, `errdetails`, `proto`, `timestamppb` and the modernc driver.
- `CGO_ENABLED=0 -trimpath -ldflags "-s -w"` builds for darwin, linux and windows on amd64 and arm64 link 28 modules on linux and 29 on darwin and windows; the extra one is `github.com/ncruces/go-strftime` [observed: `go version -m` on each binary].
- `CGO_ENABLED=0 GOOS GOARCH go list -deps` names exactly the same modules on all six targets [observed: empty diff], so CI can use it instead of building six binaries.
- Each licence file was read from the module cache; the table below and [`sidecar/licenses.allow`](../../sidecar/licenses.allow) record the verdicts with the sha256 of each file.

Linked modules [observed: the named file under `$GOMODCACHE/<module>@<version>/`, or under GOROOT for std]:

| Module | Version | Licence files | Licence |
|---|---|---|---|
| Go standard library | go1.27.3 | `LICENSE`, `PATENTS`, `src/vendor/golang.org/x/{crypto,net,sys,text}/{LICENSE,PATENTS}` | BSD-3-Clause, LicenseRef-Google-Patents |
| filippo.io/edwards25519 | v1.2.0 | `LICENSE` | BSD-3-Clause |
| github.com/beeper/argo-go | v1.1.2 | `LICENSE` | MIT |
| github.com/coder/websocket | v1.8.15 | `LICENSE.txt` | ISC |
| github.com/dustin/go-humanize | v1.1.0 | `LICENSE` | MIT |
| github.com/elliotchance/orderedmap/v3 | v3.1.0 | `LICENSE` | MIT |
| github.com/google/uuid | v1.6.0 | `LICENSE` | BSD-3-Clause |
| github.com/mattn/go-colorable | v0.1.14 | `LICENSE` | MIT |
| github.com/mattn/go-isatty | v0.0.24 | `LICENSE` | MIT |
| github.com/ncruces/go-strftime (darwin, windows) | v1.1.0 | `LICENSE` | MIT |
| github.com/petermattis/goid | v0.0.0-20260918085751-abfca077860b | `LICENSE` | Apache-2.0 |
| github.com/remyoudompheng/bigfft | v0.0.0-20230129092748-24d4a6f8daec | `LICENSE` | BSD-3-Clause |
| github.com/rs/zerolog | v1.35.1 | `LICENSE`, `hlog/internal/mutil/LICENSE` (package not linked) | MIT |
| github.com/vektah/gqlparser/v2 | v2.5.27 | `LICENSE`, `validator/imported/LICENSE` (not linked; only `ast` links) | MIT, BSD-3-Clause |
| **go.mau.fi/libsignal** | v0.2.2 | `LICENSE` | **GPL-3.0-or-later** |
| go.mau.fi/util | v0.11.0 | `LICENSE` | MPL-2.0 |
| go.mau.fi/whatsmeow | the pin | `LICENSE` (byte identical to `third_party/whatsmeow/LICENSE`) | MPL-2.0 |
| golang.org/x/{crypto, exp, net, sync, sys, text} | v0.58.0, v0.0.0-20261014143641-f0251eb4aa3e, v0.61.0, v0.24.0, v0.49.0, v0.43.0 | `LICENSE`, `PATENTS` | BSD-3-Clause, LicenseRef-Google-Patents |
| google.golang.org/genproto/googleapis/rpc | v0.0.0-20260817212433-ac3dfec99bb1 | `LICENSE` | Apache-2.0 |
| google.golang.org/grpc | v1.85.0 | `LICENSE`, `NOTICE.txt` | Apache-2.0 |
| google.golang.org/protobuf | v1.36.12 | `LICENSE`, `PATENTS` | BSD-3-Clause, LicenseRef-Google-Patents |
| modernc.org/libc | v1.79.0 | `LICENSE`, `LICENSE-3RD-PARTY.md` | BSD-3-Clause, LicenseRef-modernc-libc-third-party |
| modernc.org/mathutil | v1.7.1 | `LICENSE`, `mersenne/LICENSE` | BSD-3-Clause |
| modernc.org/memory | v1.12.1 | `LICENSE`, `LICENSE-GO`, `LICENSE-MMAP-GO`, `LICENSE-LOGO` | BSD-3-Clause; `LICENSE-LOGO` is only a Wikimedia URL (NONE) |
| modernc.org/sqlite | v1.61.1 | `LICENSE`, `LICENSE-3RD-PARTY.md`, `LICENSE-SQLITE`, `LICENSE-SQLITE_VEC` (sqlite-vec not linked) | BSD-3-Clause, LicenseRef-modernc-sqlite-third-party, LicenseRef-SQLite-public-domain, MIT |

LicenseRef ids, for texts with no SPDX id (findings, not guesses):

- `LicenseRef-Google-Patents`: Google's additional patent grant in the Go `PATENTS` files [observed].
- `LicenseRef-SQLite-public-domain`: SQLite's public-domain dedication in `LICENSE-SQLITE`; SPDX lists only the header "blessing" [observed: the file; SPDX license list].
- `LicenseRef-modernc-libc-third-party`: notices for code inside modernc.org/libc: Go (BSD-3-Clause), musl (MIT, with BSD-2-Clause, BSD and public-domain portions it lists), go-netdb (MIT) and nixpkgs (MIT) [observed: the file].
- `LicenseRef-modernc-sqlite-third-party`: modernc.org/sqlite's account of its whole module graph; its §1 "Components linked into your program" holds only MIT, BSD-3-Clause and public-domain components [observed: the file].

In the module graph but never linked, 56 modules; none is GPL, AGPL, LGPL or without a licence [observed: `go list -m all` minus the linked set, each root licence file read]:

- Apache-2.0: `cel.dev/expr`, `cloud.google.com/go/compute/metadata`, `github.com/cncf/xds/go`, `github.com/coreos/go-systemd/v22`, `github.com/envoyproxy/go-control-plane` (and `/envoy`, `/ratelimit`), `github.com/envoyproxy/protoc-gen-validate`, `github.com/go-jose/go-jose/v4`, `github.com/go-logr/logr`, `github.com/go-logr/stdr`, `github.com/golang/glog`, `github.com/google/pprof`, `github.com/GoogleCloudPlatform/opentelemetry-operations-go/detectors/gcp`, `github.com/spiffe/go-spiffe/v2`, `go.opentelemetry.io/auto/sdk`, `google.golang.org/genproto/googleapis/api`.
- Apache-2.0 and BSD-3-Clause: `go.opentelemetry.io/otel` (and `/metric`, `/sdk`, `/sdk/metric`, `/trace`), `go.opentelemetry.io/contrib/detectors/gcp`.
- BSD-3-Clause: `github.com/DATA-DOG/go-sqlmock`, `github.com/golang/protobuf`, `github.com/google/go-cmp`, `github.com/planetscale/vtprotobuf`, `github.com/pmezard/go-difflib`, `golang.org/x/mod`, `golang.org/x/oauth2`, `golang.org/x/term`, `golang.org/x/tools` (and `/go/expect`, `/go/packages/packagestest`), `gonum.org/v1/gonum`, `modernc.org/cc/v4`, `modernc.org/ccgo/v4`, `modernc.org/fileutil`, `modernc.org/gc/v2`, `modernc.org/gc/v3`, `modernc.org/goabi0`, `modernc.org/opt`, `modernc.org/sortutil`, `modernc.org/strutil`, `modernc.org/token`.
- BSD-2-Clause: `github.com/pkg/errors`.
- ISC: `github.com/davecgh/go-spew`.
- MIT: `github.com/agnivade/levenshtein`, `github.com/andreyvit/diff`, `github.com/cespare/xxhash/v2`, `github.com/mattn/go-sqlite3`, `github.com/rs/xid`, `github.com/sergi/go-diff`, `github.com/stretchr/testify`.
- MIT and Apache-2.0: `gopkg.in/yaml.v3`.
- MPL-2.0: `github.com/hashicorp/golang-lru/v2`.

The sidecar has no tests yet, so it has no test-only modules of its own [observed].

libsignal:

- Its `LICENSE` is the full GPLv3 text, and its appendix carries the filled-in notice "libsignal-protocol-go Copyright (C) 2017 RadicalApp LLC" with "either version 3 of the License, or (at your option) any later version" [observed: `LICENSE` lines 634-640].
- No `.go` file carries a licence header [observed: grep].
- So libsignal is `GPL-3.0-or-later` [observed: the notice above].
- GitHub reports GPL-3.0 for tulir/libsignal-protocol-go and for its origin RadicalApp/libsignal-protocol-go [observed: `gh api repos/<repo>`, 2026-10-31].
- GPLv3 §14: when "or any later version" applies, "you have the option of following the terms and conditions either of that numbered version or of any later version" [observed: `LICENSE`].

What the sources say:

- GPLv3 §5(c): "You must license the entire work, as a whole, under this License" [observed: libsignal `LICENSE`].
- FSF FAQ #GPLStaticVsDynamic: linking statically or dynamically "is making a combined work based on the GPL covered work" [observed: gnu.org/licenses/gpl-faq].
- GPLv3 §6(d) lets object code on a network server point to Corresponding Source "on a different server", given "clear directions next to the object code", and "you remain obligated to ensure that it is available for as long as needed" [observed: `LICENSE`; FAQ #SourceAndBinaryOnDifferentSites].
- GPLv3 §1: Corresponding Source is "all the source code needed to generate, install, and (for an executable work) run the object code and to modify the work, including scripts to control those activities" [observed].
- GPLv3 §0: "Mere interaction with a user through a computer network, with no transfer of a copy, is not conveying" [observed].
- whatsmeow and util attach no MPL Exhibit B [observed: grep; MPL §1.5], and the FSF licence list says of MPL-2.0: "Section 3.3 provides indirect compatibility between this license and the GNU GPL version 2.0, the GNU LGPL version 2.1, the GNU AGPL version 3, and all later versions of those licenses" [observed: gnu.org/licenses/license-list #MPL-2.0].
- The FSF licence list calls Apache-2.0 "compatible with version 3 of the GNU GPL", and BSD-3-Clause, Expat (MIT) and ISC compatible with the GPL [observed: gnu.org/licenses/license-list].
- FSF FAQ #MereAggregation: "pipes, sockets and command-line arguments are communication mechanisms normally used between two separate programs", and "But if the semantics of the communication are intimate enough, exchanging complex internal data structures, that too could be a basis to consider the two parts as combined into a larger program" [observed].

## Decision

- Each released `whatsmeow-net` binary is one combined work that the GPL covers as a whole [inferred: GPLv3 §5(c) and FAQ #GPLStaticVsDynamic, quoted above].
- We convey it under GPL-3.0-only, which libsignal's "or any later version" allows [inferred: GPLv3 §14, quoted above].
- We comply through GPLv3 §6(d): the object code is on nuget.org, and the Corresponding Source is on the GitHub release of the same tag, with directions in `NOTICE` and in each native package.
- The .NET assemblies are separate programs that talk to the sidecar only through gRPC over a local socket, so the GPL does not reach them [inferred: FAQ #MereAggregation; the contract carries whatsmeow message types and no libsignal structures, and the contract does not exist yet (#3)].
- Files generated from `third_party/whatsmeow` protos are MPL-2.0 [inferred: MPL §1.10(b) makes "any new file in Source Code Form that contains any Covered Software" a Modification, and each generated file embeds its whole proto descriptor (observed in `Generated/*.cs`)].
- The protos carry no Exhibit A header; the module's `LICENSE`, copied to `third_party/whatsmeow/LICENSE`, is the location Exhibit A allows [observed: zero protos with a header; MPL Exhibit A].
- The repository's own licence is MPL-2.0 (option A, chosen by the owner on 2026-10-31), in the root `LICENSE`, the mozilla.org text verbatim:
  - A, chosen: MPL-2.0 matches whatsmeow and util, covers the generated C# with the same licence, is GPL-compatible through §3.3 as long as we never attach Exhibit B, is file-level so proprietary apps can use the .NET packages, and grants patents (§2.1(b)).
  - B, considered: MIT or Apache-2.0 for our code, with MPL-2.0 for the generated C# and GPL-3.0 for the binary: three licences to explain for little gain.
  - C, considered: GPL-3.0 everywhere: an app that loads the .NET assemblies in-process would likely form a combined work with them [inferred], which would block most consumers.
- `sidecar/licenses.allow` records every licence file of every linked module and of std, with its sha256 and a reviewed licence.
- `tools/check-licenses`, run by `build/ci.sh go`, compares it with the `CGO_ENABLED=0 go list -deps` graph of `sidecar/cmd/whatsmeow-net` on the six targets of ADR 0007.
- It fails on a new module, an added, removed or changed licence file, a module without a licence file, or a module that no longer links.
- Until #4 adds the main package, CI compares only the std lines; the module lines come from the audit's throwaway build and are not checked in CI until then.
- Legal review is [unknown] and stays open; RESEARCH item 1 asked for it, and nothing above replaces it.

## Consequences

- Phase 4 obligations, before the first public release:
  - Every GitHub release attaches `whatsmeow-net_<version>_source.tar.gz`: the whole tagged repository plus the output of `go mod vendor` for `sidecar/`.
  - The audit's vendored tree was 30 MB gzipped [observed].
  - Releases are never deleted while their packages are on nuget.org.
  - Each `Whatsmeow.Sidecar.Native.*` package carries `COPYING` (the GPL-3.0 text), `NOTICE`, and `THIRD-PARTY-NOTICES.txt` holding every file listed in `sidecar/licenses.allow` verbatim, which covers the BSD, MIT, ISC and Apache notice duties and grpc's `NOTICE.txt` (Apache-2.0 §4(d)).
  - `PackageLicenseExpression` is `GPL-3.0-only` on the native packages and `MPL-2.0` on the .NET packages.
  - The package READMEs say that the native packages contain GPL-3.0 object code and what redistributing them owes.
  - `.goreleaser.yaml` builds exactly the targets in `tools/check-licenses`, and CI checks that.
- Consumers who only run the binary on their own machines or servers owe nothing under the GPL [inferred: GPLv3 §0].
- A consumer who redistributes it (an installer, a desktop app, a container image) conveys it and takes on GPLv3 §6 for `whatsmeow-net` [inferred].
- Shipping it in a consumer device also owes Installation Information [inferred: GPLv3 §6 "User Product"].
- Whether a consumer can leave the native assets out (for example NuGet `ExcludeAssets`) and run an external sidecar is [unknown]; measure it in Phase 4 if asked.
- Our own sidecar files stay MPL-2.0 inside the GPL combined work; never attach MPL Exhibit B to any file.
- [ADR 0007](0007-per-os-native-nuget-packages-exact-pins.md)'s packaging decision is unchanged; this record answers its last two Consequences.
- A whatsmeow bump that adds a module or changes a licence file fails CI until a reviewer reads the licence and edits `sidecar/licenses.allow`.
