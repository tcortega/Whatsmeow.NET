# macOS Gatekeeper on the packaged sidecar, 2026-11-06

Evidence for [#45](https://github.com/tcortega/Whatsmeow.NET/issues/45), its macOS half, measured on one daily-use Mac.
It does not complete #45: Windows, a clean Mac, native Intel and a restore of `whatsmeow-net` from nuget.org are unmeasured.
Every claim is scoped to this host and these packages; an [inferred] or [unknown] claim is an open question, not a result.
Times are the host's local clock, as `log show` printed them.

## Host

[observed: `sw_vers`, `uname`, `sysctl`, `csrutil`, `spctl --status`, `fdesetup`, `id`, `profiles` and `system_profiler`, in the lane's `plan.md`] macOS 26.5.2 (25F84) on a Mac16,8 with an Apple M4 Pro, arm64.
SIP enabled, Gatekeeper `assessments enabled`, FileVault on, an administrator account, no DEP or MDM enrollment, Rosetta installed, XProtect payload 5368 and configuration 5371.
It is a real desktop, not a CI runner.
It is the owner's daily-use Mac, not a clean one: syspolicyd's and Rosetta's caches were warm from earlier use and these runs added to them, so nothing here says what a fresh install does.
.NET SDK 10.0.403 arm64 with arm64 runtimes only; no x64 .NET runtime was present, and none was installed.

## Packages under test

[observed: the GitHub artifacts API, `shasum -a 256`, `go version -m`, `codesign -dvv`] One feed, the `native-packages` artifact 13524886472 of CI run [42257604502](https://github.com/tcortega/Whatsmeow.NET/actions/runs/42257604502), a push to main at `e5cf3b19c200822adb124bd6325683c59a14dbb1`, version `0.0.0-alpha.0.77`.
The downloaded zip's SHA-256, `67cea875828a3c769616c340ee1c78d58fab2cbf963cb921d4f93bfab5b2abcf`, equals the digest the API reports.

| File | SHA-256 | Signature |
| --- | --- | --- |
| `runtimes/osx-arm64/native/whatsmeow-net` | `2a7f380aabf9af1bfacc1db4e816d227a4ed449a09eab196c3ff2f427bda4fed` | `flags=0x20002(adhoc,linker-signed)`, `Identifier=a.out` |
| `runtimes/osx-x64/native/whatsmeow-net` | `5866f7e76b930288fec3a04eae38176acb0accb62bca2492f4317c5aff24fe3b` | `not signed at all` |
| `Whatsmeow.Sidecar.Native.MacOS.0.0.0-alpha.0.77.nupkg` | `1440cd5e2df8fd6d198eb46f46dc064dc7a8f3e6501c05aeb6f6abe1186ac5b9` | |
| `Whatsmeow.Client.0.0.0-alpha.0.77.nupkg` | `0d672955761bd692c2a41d130f2db93e8ad2384b84e0a6c3f67357ab00b30dda` | |
| `Whatsmeow.Protocol.0.0.0-alpha.0.77.nupkg` | `d188740ba66c48807c9f35cece6098b8be77bbfbd17e4155fe7901b0e3b215a5` | |
| `Whatsmeow.Protocol.WhatsApp.0.0.0-alpha.0.77.nupkg` | `879a60d857e4a3486a97cd25c3545500d60140719d767000d7119cfb2f8a433b` | |
| `Whatsmeow.Extensions.Hosting.0.0.0-alpha.0.77.nupkg` | `aaa3c2c98e18d8270122db4e5294cf43d1103470bc2b4bdbd8be26516c9e8a9e` | |
| `Whatsmeow.Testing.0.0.0-alpha.0.77.nupkg` | `42ff6f93d07f26da7ea0125159b9b5d9c72c816c37bc75fdc66bed3d29d0e47f` | |
| `Whatsmeow.Sidecar.Native.Linux.0.0.0-alpha.0.77.nupkg` | `1b33890fd79dd5da9880c7c2329c33755b3f60897453c2bcfff0e9838fc61ab5` | |
| `Whatsmeow.Sidecar.Native.Windows.0.0.0-alpha.0.77.nupkg` | `2fa648567c2f19353f61c5c660ceae246aba984888aa9ea945849b43197719a3` | |

Both binaries report Go 1.27.3, `CGO_ENABLED=0`, `vcs.revision=e5cf3b19c200822adb124bd6325683c59a14dbb1` and `vcs.modified=false`.
The release rehearsal run [42262463458](https://github.com/tcortega/Whatsmeow.NET/actions/runs/42262463458) at the same commit (artifact 13526341612, zip `775980dde5fe43912ff76d1c073a8135745829a97db7444fbfe9631376beba79`, version `0.0.0-rehearsal.2`) was downloaded and hashed only, never restored or launched.
Its osx-arm64 and osx-x64 binaries are `2bbbac27647129db9a09b2a078d50862c2c5b4eb9a23bacac3ad9b28c01b996d` and `60d204ba0074a0bbb7ee6908205e223014c2190cf14ad3480e5eec884ada2986`.

## Method

[observed: the lane's `scripts/`, hashed below]

* The consumer is `build/package-smoke/Program.cs` from `e5cf3b1`, byte for byte, in a `dotnet new console` app that references `Whatsmeow.Client`, `Whatsmeow.Extensions.Hosting` and `Whatsmeow.Testing` at the feed version.
  It uses the public API only: start, `GetInfoAsync`, dispose, with no `SidecarPath`, `WHATSMEOW_NET_SIDECAR_PATH` unset, and no pairing.
  An arm64 launch passes `osx-arm64` and the expected layout, so the program also checks the process architecture and where the sidecar was found; an x64 launch passes the version only.
* The feed is a static NuGet v3 tree of the eight nupkgs, served over plain HTTP on a loopback port by Python's `http.server` and mapped to `Whatsmeow.*` with `allowInsecureConnections`.
  It is a local HTTP feed, not nuget.org; every other package came from nuget.org [observed: the loopback server's request log and NuGet's `GET` lines in `logs/setup.log`].
* `HOME`, `DOTNET_CLI_HOME`, `NUGET_PACKAGES`, `NUGET_HTTP_CACHE_PATH` and `TMPDIR` pointed into the lane directory, with empty caches, and each launch had its own `HOME` and `TMPDIR`.
  That isolates the CLI and the app only: the OS wrote its logs, its caches and its Gatekeeper records outside it.
* Each launch had a 90 s cap, never reached; the process table was sampled every 0.3 s, then `log show` collected syspolicyd, XprotectService, amfid, the kernel's code signing lines and CoreServicesUIAgent over the launch window.
* `spctl -a -t exec` and `syspolicy_check distribution` ran only after the D4, D7 and S1 launches, so no assessment preceded a launch; `spctl` rejected all three sidecars and the D4 apphost [observed: `logs/assess-D-S.txt`].
* The harness has no UI step.
  No Gatekeeper, SIP or trust setting was changed.

## Metadata, M1 to M10

[observed: `logs/setup.log`, `logs/M10-copies.txt`, `plan.md`] No file at any stage carried `com.apple.quarantine`:

* M1, the zip and nupkgs downloaded with `gh api`, not a browser;
* M2, the nupkg NuGet fetched over the loopback feed into the empty packages folder;
* M3, the binaries NuGet extracted;
* M4, the portable build output;
* M5 to M9, the portable, framework-dependent, self-contained and single-file publishes for osx-arm64 and osx-x64;
* M10, `cp -R` and `ditto` copies of the two self-contained publishes.

Every file carried `com.apple.provenance` with the same 11-byte value; what the OS records in it is [unknown].
The binaries' hashes did not change at any stage.
NuGet extracted each binary as 0744 (`-rwxr--r--`); the M10 copies, made after D4 and D7 had run, are 0755 [observed: M10], which matches the client's `File.SetUnixFileMode` before a start (ADR 0007) [inferred].

## Launches

[observed: `cases/<id>/case.txt`, `stdout`, `stderr`] Each launch goes through the public client: start, `GetInfoAsync`, dispose.
Every completed launch printed `sidecar 0.0.0-alpha.0.77, protocol 1`.

| Case | Delivery path | Sidecar | Result |
| --- | --- | --- | --- |
| D1 | portable build, `dotnet run` | `runtimes/osx-arm64/native/`, arm64 | exit 0, 2.24 s, sidecar PID 13258 |
| D2 | portable publish, `dotnet Consumer.dll` | `runtimes/osx-arm64/native/`, arm64 | exit 0, 1.11 s, PID 13916 |
| D3 | framework-dependent `-r osx-arm64` | next to the app, arm64 | exit 0, 1.10 s, PID 14013 |
| D4 | self-contained `-r osx-arm64` | next to the app, arm64 | exit 0, 2.20 s, PID 14163 |
| D5 | single-file osx-arm64 | next to the app, arm64 | exit 0, 2.63 s, PID 14434 |
| D6 | single-file osx-arm64 with `IncludeNativeLibrariesForSelfExtract` | next to the app, arm64 | exit 0, 2.25 s, PID 14672 |
| D7 | self-contained `-r osx-x64` | next to the app, x64 under Rosetta | exit 0, 10.29 s, PID 15168 |
| D8 | single-file osx-x64 | next to the app, x64 under Rosetta | exit 0, 5.63 s, PID 15518 |
| D9 | `cp -R` and `ditto` copies of D4 and D7, each run from the copy | as its source | exit 0 for all four: PIDs 16306 (`cp`, arm64), 16422 (`ditto`, arm64), 16836 (`cp`, x64), 17370 (`ditto`, x64) |
| S1 | copy of D4, the sidecar's ad-hoc signature removed (`fb0f565fade6509df60d59e813f10f438d503aade147b7d0772255c72782e191`, `not signed at all`), no quarantine | arm64 | refused at once, no prompt: [S1](#s1-unsigned-arm64-sidecar) |
| Q1a | copy of D4, `com.apple.quarantine` on `whatsmeow-net` only | arm64 | refused after a prompt: [Q1a](#q1a-quarantined-arm64-sidecar) |
| Q1b, Q2a, Q2b, Q2c | quarantined x64 sidecar (Q1b); quarantined whole directory, portable arm64 (Q2a), self-contained arm64 (Q2b), self-contained x64 (Q2c) | | not run: prepared, then the coordinator stopped the synthetic quarantine variants after Q1a, and no further launch was made |
| | osx-x64 framework-dependent | | not run: no x64 .NET runtime |

Every arm64 launch printed `process Arm64, OS Arm64`.
The x64 launches ran under Rosetta [inferred: an x86_64-only Mach-O on Apple silicon; D7's and D8's logs name `/private/var/db/oah/` in kernel sandbox lines], so they say nothing about native Intel.
No launch used the client's copy fallback (its isolated directory stayed empty), and no sidecar process was left after any case.

## What the OS logged

[observed: `cases/<id>/oslog-policy.txt`, `oslog.txt`] syspolicyd logged `GK performScan` and `GK evaluateScanResult` lines in every launch, quarantined or not.
Each launch of the ad-hoc arm64 sidecar has one `evaluateScanResult` with `id: a.out`, its signing identifier: `GK evaluateScanResult: 2` in D1 to D6 and D9, which ran, and `GK evaluateScanResult: 1` in Q1a.
Q1a's is the only result other than 2 in any case; every scan in S1, which was refused, reads 2.
The x64 launches' and S1's scans include lines with `id: (null)`; the log hashes paths, so which file each one covers is [unknown].
The numbers are quoted as logged; their meaning is [unknown].
In every arm64 launch with the ad-hoc sidecar, Q1a included, the kernel logged `AMFI: '.../whatsmeow-net' has no CMS blob?` and `Unrecoverable CT signature issue, bailing out.`; the D launches still started it.

### S1, unsigned arm64 sidecar

| Time | Process | Logged |
| --- | --- | --- |
| 15:06:34.115 | harness | launch |
| 15:06:34.792 | kernel | `(AppleSystemPolicy) ASP: Security policy would not allow process: 17957, .../copies/S1/whatsmeow-net` |

The client threw `SidecarUnavailableException: The sidecar did not start: it exited or wrote garbage before its ready line (exit code 137). It wrote nothing to stderr.`, over an inner `IOException: Broken pipe`, and the program ended after 0.749 s.
There is no syspolicyd `Prompt shown` line, and no CoreServicesUIAgent line, in the launch window.
On this Mac an arm64 sidecar with no signature is refused without a prompt, and the same build with the linker's ad-hoc signature runs (D4).

### Q1a, quarantined arm64 sidecar

[observed: `logs/Q-pre.txt`] The harness wrote `0081;6aee1751;Safari;926521E3-14D2-4DFA-AD6D-97B6B74B16D1` to the sidecar's `com.apple.quarantine`; the value is synthetic, and no browser downloaded anything.
The apphost `Consumer` had no quarantine.

| Time | Process | Logged |
| --- | --- | --- |
| 15:07:41.040 | harness | launch |
| 15:07:42.848 | syspolicyd | `GK evaluateScanResult: 1, PST: (path: 76c209b2b2531700), (team: (null)), (id: a.out), ...` |
| 15:07:42.848 | syspolicyd | `Prompt shown (6, 0), waiting for response: PST: (path: 76c209b2b2531700), (team: (null)), (id: a.out), ...` |
| 15:07:42.855 | CoreServicesUIAgent | `present code-evaluation prompt` |
| 15:08:10.111 | CoreServicesUIAgent | `(AppKit) trackMouse send action on mouseUp` |
| 15:08:10.117 | syspolicyd | `Code evaluation completed: 2` |
| 15:08:10.118 | syspolicyd | `Adding Gatekeeper denial breadcrumb (direct): PST: (path: 76c209b2b2531700), (team: (null)), (id: a.out), ...` |
| 15:08:10.170 | kernel | `(AppleSystemPolicy) ASP: Security policy would not allow process: 20384, .../copies/Q1a/whatsmeow-net` |

The client threw `SidecarUnavailableException: The sidecar did not start: it exited before its ready line (exit code 137). It wrote nothing to stderr.`, and the program ended after 29.363 s.
A mouse action in CoreServicesUIAgent answered the prompt 27 s after it appeared.
Who acted and which button was pressed are [unknown]: the harness has no UI step, nobody recorded the dialog, and nothing is inferred from the outcome.
The copy's sidecar still has the same SHA-256 and the same quarantine value [observed: `shasum` and `xattr -l` on it while writing this record].
On this Mac a quarantined, ad-hoc signed arm64 sidecar, started by an unquarantined app, raised a Gatekeeper prompt, the start waited until it was answered, and the sidecar was then refused.

## What this settles, and what it does not

* "Apple Silicon needs arm64 code to be signed at all" is observed on this Mac (S1), and the linker's ad-hoc signature is enough without quarantine (D1 to D6, D9).
* "Gatekeeper assesses only quarantined launches" does not hold as worded: syspolicyd scanned every launch.
  What differed was the outcome: only the quarantined sidecar raised a prompt and was refused (Q1a against D).
* Restore from a loopback HTTP feed, portable build and `dotnet run`, the framework-dependent, self-contained and single-file publishes, and `cp -R` and `ditto` copies put no quarantine on the sidecar, and each started it, arm64 natively and x64 under Rosetta.
* No measured delivery path was blocked, so ADR 0007's decision stands and no signing ADR is triggered.

Unmeasured, so no claim covers them:

* a restore from nuget.org, since nothing is published yet;
* a clean Mac, native Intel, and other macOS versions;
* osx-x64 framework-dependent;
* Q1b and Q2a to Q2c: a quarantined x64 sidecar, a quarantined app directory, and whether the apphost is refused first;
* which tools, transfers or user actions set quarantine on a consumer's machine, beyond the paths measured here;
* what the prompt offers, and what each answer does;
* Windows: SmartScreen, Mark-of-the-Web, Smart App Control, and §10's Temp ACL for a non-administrator;
* the notarized `.app` claim, which stays [inferred] (#51).

## Evidence provenance

The raw records stay private: their OS logs include unrelated activity of a daily-use machine, and their scripts and paths name the host.
They are in `/tmp/whatsmeow-issues/45-mac-current/` on the measuring Mac, left as recorded; these SHA-256 hashes identify them.

| Artifact | SHA-256 |
| --- | --- |
| `RESULTS.md`, the lane's results | `9e39db6a5fcca1baf5d7af52874a4917e3d7a70b3e7e276ef47959d7eca6c4d5` |
| `plan.md`, host record and matrix | `13dd21708ad41debd0b207db8cedff948f0bb1d25868f4c2ce490ed520f73660` |
| `artifacts/checksums.sha256`, both feeds' files | `5dc56e33c888b98c8d6da77fff3c12e49bee9f89b2411c9d40b2785a5a84be42` |
| `scripts/env.sh` | `796f3f7edf9ff327e4a53c71894128761671e342d5f9ba258dd9fa22e37afb8b` |
| `scripts/make-feed.sh` | `e9407b85b7fb29f4558bd2d8343b288167de6da09bc08385af4455c85c1647f6` |
| `scripts/setup.sh` | `1a2627b463e96c3d00d4e38229f941fbd5f853539e8acd98537b79380100ff20` |
| `scripts/case.sh` | `eea22e2323781e870f7e7c7deabac85f9c6a427860d9e159b90bb5a0a89b096e` |
| `scripts/Program.cs`, `build/package-smoke/Program.cs` at `e5cf3b1` | `82432ad4b9875280c6862576880d921f9fbfe8769d266814bf38f06e9d9442b3` |
| `logs/setup.log`, restore, build, publish, M2 to M9 | `9e86734271a71b0bd0f960dcee86a134073cf33b0b07699e62f8e03132834be9` |
| `logs/http-server.log`, the loopback feed's requests | `982e27c724739f4b8c30c3d3d0ee0278c235af67af2fa9f60356b41476d4413e` |
| `logs/M10-copies.txt` | `28c71b4927324f47e4917793dd0c2213fad26c228c4311e88ace3800167e65dd` |
| `logs/S1-pre.txt` | `ab5f012e0d14a2e606180b37d8cce002d08efc2a52c763fc3bcf0edd9afa4ef0` |
| `logs/Q-pre.txt` | `bd960d5496695357350712b36bae373e1c0ff9e38848cc0fa9d0c3029ec9190a` |
| `logs/assess-D-S.txt` | `e81a1d31e0a65d0100eda80a9815707efe05a4f2aabf53ead11486d79c8e779e` |
| `cases/S1-unsigned-arm64/oslog-policy.txt` | `99e0054029afa9b1a33341453708aa4e4b4b7659d94b93a461d286e9e8178a49` |
| `cases/S1-unsigned-arm64/oslog.txt` | `6c3b9ef1a68c4f91be68b3847911fb50952b05373e3d26f94b7c194cb4b8749b` |
| `cases/Q1a-sidecar-quarantined-arm64/oslog-policy.txt` | `101485ad80c7b912b98dda952f4b2cfef9811d24af73d4afa6286d67a53a2946` |
| `cases/Q1a-sidecar-quarantined-arm64/oslog.txt` | `ea524f7f2f1e86844190ee461f39ffa28f438755b037061a0bae4b341d2224cb` |
| `cases/`, all 126 files of the 14 cases | `d4a7dd12889f8b4e6811183ac94c257693936d3b02b8c11b487f0ec9a687bc93` |

The `cases/` digest is `find cases -type f | LC_ALL=C sort | xargs shasum -a 256 | shasum -a 256`, run in the lane directory.
To repeat the measurement: download the run's `native-packages` artifact, then run `setup.sh` and `case.sh <id> <dir> <command>` per case; the scripts hard-code the lane directory and the host's `DOTNET_ROOT`.
