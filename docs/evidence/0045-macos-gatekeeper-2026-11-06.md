# macOS Gatekeeper on the packaged sidecar, 2026-11-06

Evidence for [#45](https://github.com/tcortega/Whatsmeow.NET/issues/45), its macOS half, measured on one daily-use Mac.
It does not complete #45: Windows and a restore of `whatsmeow-net` from nuget.org are unmeasured here; Windows has [its own record](0045-windows-smartscreen-2026-11-10.md), measured 2026-11-10.
The owner authorized this Mac in place of the clean Mac #45 names (#11), so this record stands in for that run without being evidence about a clean Mac; a clean Mac, a native Intel desktop, other macOS versions and Q2a to Q2c are unmeasured extras, not #45 gates.
Every claim is scoped to this host and these packages; an [inferred] or [unknown] claim is an open question, not a result.
This record is authoritative over the raw results it draws on; the raw claims it corrects are listed under [Superseded raw claims](#superseded-raw-claims).
Times are the host's local clock, UTC-3, as `log show` printed them.
Q1b ran after the rest, at 23:54 on the same local day, under its own driver; [its section](#q1b-quarantined-x64-sidecar) says what differs.

## Host

[observed: the measurer's pre-run host record in `plan.md`, transcribed from `sw_vers`, `sysctl`, `csrutil` and `spctl --status`; the raw command output was not kept] macOS 26.5.2 (25F84) on a Mac16,8 with an Apple M4 Pro, arm64, with SIP enabled, Gatekeeper `assessments enabled` and Rosetta installed.
It is a real desktop in daily use, not a CI runner and not a clean install, and it was in use during the runs.
[inferred] syspolicyd and Rosetta held state from earlier use, and these runs may have added to it, so nothing here says what a fresh install does.
Whether the launching terminal had the Developer Tools privacy exemption is [unknown].
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

[observed: the measurement directory's `scripts/`, hashed below]

* The consumer is `build/package-smoke/Program.cs` from `e5cf3b1`, byte for byte, in a `dotnet new console` app that references `Whatsmeow.Client`, `Whatsmeow.Extensions.Hosting` and `Whatsmeow.Testing` at the feed version.
  It uses the public API only: start, `GetInfoAsync`, dispose, with no `SidecarPath`, `WHATSMEOW_NET_SIDECAR_PATH` unset, and no pairing.
  It sets only a database path, so `StartupTimeout` is its 30 s default [observed: `WhatsmeowSidecarOptions.cs:21` at `e5cf3b1`].
  An arm64 launch passes `osx-arm64` and a layout, and the program first checks that the process is native and that the binary exists at that layout's path; an x64 launch passes the version only, so it prints no architecture line.
* The feed is a static NuGet v3 tree of the eight nupkgs, served over plain HTTP on a loopback port by Python's `http.server` and mapped to `Whatsmeow.*` with `allowInsecureConnections`.
  It is a local HTTP feed, not nuget.org; every other package came from nuget.org [observed: the loopback server's request log and NuGet's `GET` lines in `logs/setup.log`].
* `HOME`, `DOTNET_CLI_HOME`, `NUGET_PACKAGES`, `NUGET_HTTP_CACHE_PATH` and `TMPDIR` pointed into the measurement directory, with empty caches, and each launch had its own `HOME` and `TMPDIR`.
  That isolates the CLI and the app only: the OS wrote its logs, its caches and its Gatekeeper records outside it.
* Each launch had a 90 s cap, never reached, and the process table was sampled every 0.3 s into `ps.log`, which names the path each sidecar ran from.
  Then `log show` captured the launch window twice: lines naming `whatsmeow` or the measurement directory plus every CoreServicesUIAgent line, and every syspolicyd, XprotectService and amfid line plus the kernel's code signing lines.
* The captures are time windows, not filtered to the case's processes, and the desktop was in concurrent use, so they hold unrelated activity, which this record does not publish.
  Only lines that name the case's path, its PID or the sidecar's signing identifier are attributed to a case.
* `spctl -a -t exec` and `syspolicy_check distribution` ran at 15:06:54 to 15:06:56, after D1 to D9 and S1 and before Q1a, on D4's sidecar and apphost, D7's sidecar and S1's sidecar; `spctl` rejected all four [observed: `logs/assess-D-S.txt`; the command lines are from `plan.md`, inferred].
  D4's sidecar and apphost are byte-identical to Q1a's (`2a7f380a…`, `8692339a…`) [observed: `logs/setup.log`, `logs/Q-pre.txt`], so Q1a ran after an assessment of the same code; whether that changed Q1a is [unknown].
* The harness has no UI step; Q1b's dialog was answered from outside it ([Q1b](#q1b-quarantined-x64-sidecar)).
  No Gatekeeper, SIP or trust setting was changed.

### Commands per case

`V` is a placeholder: substitute the feed version, `0.0.0-alpha.0.77`.
Commands are normalized from `case.txt` [observed]: paths are relative to the measurement directory, or to the stated directory, and `dotnet` stands for the SDK's path.
Publish preparation is `setup.sh` [observed], shortened here: each publish also passes `app -c Release -warnaserror -o out/<name>`; copy, signature and quarantine preparation is reconstructed from `plan.md` [inferred], and its result is [observed] in the named log.

| Case | Command | Preparation |
| --- | --- | --- |
| D1 | in `consumer`: `dotnet run --project app -c Release --no-build -- start V osx-arm64 portable` | `dotnet build app -c Release -warnaserror` |
| D2 | in `out/portable`: `dotnet Consumer.dll start V osx-arm64 portable` | `dotnet publish --self-contained false` |
| D3 | `out/fd-arm64/Consumer start V osx-arm64 flat` | `-r osx-arm64 --self-contained false` |
| D4 | `out/sc-arm64/Consumer start V osx-arm64 flat` | `-r osx-arm64 --self-contained true` |
| D5 | `out/single-arm64/Consumer start V osx-arm64 flat` | D4's, plus `-p:PublishSingleFile=true` |
| D6 | `out/single-native-arm64/Consumer start V osx-arm64 flat` | D5's, plus `-p:IncludeNativeLibrariesForSelfExtract=true` |
| D7 | `out/sc-x64/Consumer start V` | `-r osx-x64 --self-contained true` |
| D8 | `out/single-x64/Consumer start V` | D7's, plus `-p:PublishSingleFile=true` |
| D9 | `copies/{cp,ditto}-arm64/Consumer start V osx-arm64 flat`, `copies/{cp,ditto}-x64/Consumer start V` | `cp -R` and `ditto` of `out/sc-arm64` and `out/sc-x64` [inferred]; `logs/M10-copies.txt` |
| S1 | `copies/S1/Consumer start V osx-arm64 flat` | a copy of `out/sc-arm64`, then `codesign --remove-signature` on its `whatsmeow-net` [inferred]; `logs/S1-pre.txt` |
| Q1a | `copies/Q1a/Consumer start V osx-arm64 flat` | a copy of `out/sc-arm64`, then `xattr -w com.apple.quarantine` on its `whatsmeow-net` [inferred]; `logs/Q-pre.txt` |
| Q1b | `copies/Q1b/Consumer start V` | a copy of `out/sc-x64`, then `xattr -w com.apple.quarantine` on its `whatsmeow-net` [inferred]; `logs/Q-pre.txt` |

## Metadata, M1 to M10

[observed: `logs/setup.log` and `logs/M10-copies.txt`; M1 from the measurer's record in `plan.md`] No file at any stage carried `com.apple.quarantine`:

* M1, the zip and nupkgs downloaded with `gh api`, not a browser;
* M2, the nupkg NuGet fetched over the loopback feed into the empty packages folder;
* M3, the binaries NuGet extracted;
* M4, the portable build output;
* M5 to M9, the portable publish (its osx-arm64 binary only), the framework-dependent osx-arm64 publish, and the self-contained and single-file publishes for osx-arm64 and osx-x64;
* M10, `cp -R` and `ditto` copies of the two self-contained publishes.

Every file carried `com.apple.provenance` with the same 11-byte value; what the OS records in it is [unknown].
The binaries' hashes did not change at any stage.
NuGet extracted each binary as 0744 (`-rwxr--r--`); the M10 copies, made after D4 and D7 had run, are 0755 [observed: M10], which matches the client's `File.SetUnixFileMode` before a start (ADR 0007) [inferred].

## Launches

[observed: `cases/<id>/case.txt`, `stdout`, `stderr`; the Sidecar column from `ps.log`] Each launch goes through the public client: start, `GetInfoAsync`, dispose.
Every completed launch printed `sidecar 0.0.0-alpha.0.77, protocol 1`.

| Case | Delivery path | Sidecar | Result |
| --- | --- | --- | --- |
| D1 | portable build, `dotnet run` | `bin/Release/net10.0/runtimes/osx-arm64/native/`, arm64 | exit 0, 2.24 s, sidecar PID 13258 |
| D2 | portable publish, `dotnet Consumer.dll` | `runtimes/osx-arm64/native/`, arm64 | exit 0, 1.11 s, PID 13916 |
| D3 | framework-dependent `-r osx-arm64` | next to the app, arm64 | exit 0, 1.10 s, PID 14013 |
| D4 | self-contained `-r osx-arm64` | next to the app, arm64 | exit 0, 2.20 s, PID 14163 |
| D5 | single-file osx-arm64 | next to the app, arm64 | exit 0, 2.63 s, PID 14434 |
| D6 | single-file osx-arm64 with `IncludeNativeLibrariesForSelfExtract` | next to the app, arm64 | exit 0, 2.25 s, PID 14672 |
| D7 | self-contained `-r osx-x64` | next to the app, x64 under Rosetta [inferred] | exit 0, 10.29 s, PID 15168 |
| D8 | single-file osx-x64 | next to the app, x64 under Rosetta [inferred] | exit 0, 5.63 s, PID 15518 |
| D9 | `cp -R` and `ditto` copies of D4 and D7, each run from the copy | as its source | exit 0 for all four: PIDs 16306 (`cp`, arm64), 16422 (`ditto`, arm64), 16836 (`cp`, x64), 17370 (`ditto`, x64) |
| S1 | synthetic control: copy of D4, the sidecar's ad-hoc signature removed (`fb0f565fade6509df60d59e813f10f438d503aade147b7d0772255c72782e191`, `not signed at all`), no quarantine | arm64; never in `ps.log` | refused at exec, no prompt: [S1](#s1-unsigned-arm64-sidecar) |
| Q1a | synthetic control: copy of D4, `com.apple.quarantine` on `whatsmeow-net` only | arm64 | prompt, then refused: [Q1a](#q1a-quarantined-arm64-sidecar) |
| Q1b | synthetic control: copy of D7, `com.apple.quarantine` on `whatsmeow-net` only | x64 under Rosetta [inferred] | prompt, client timed out: [Q1b](#q1b-quarantined-x64-sidecar) |
| Q2a, Q2b, Q2c | quarantined whole directory, portable arm64 (Q2a), self-contained arm64 (Q2b), self-contained x64 (Q2c) | | not run: prepared, then the measurer stopped the synthetic quarantine variants after Q1a; later only Q1b was run |
| | osx-x64 framework-dependent | | not run on this Mac: no x64 .NET runtime |

Every arm64 launch printed `process Arm64, OS Arm64`.
The x64 launches D7, D8 and D9's x64 copies ran under Rosetta [inferred: x86_64-only Mach-O files on Apple silicon, and kernel sandbox lines naming `/private/var/db/oah/` in each of their windows], so they say nothing about native Intel; Q1b's basis is narrower ([Q1b](#q1b-quarantined-x64-sidecar)).
[observed: `gh run view 42257604502`] The run that built these packages passed `install (osx-x64)` and `aot (osx-x64)` on `macos-26-intel` runners (§11), so osx-x64 consumers, framework-dependent included, started natively on a CI runner; that run recorded no quarantine metadata, so whether the runner's copies carried quarantine, and its Gatekeeper state, are [unknown], as are a native Intel desktop and quarantine on Intel.
No launch used the client's copy fallback (its isolated directory stayed empty), no sidecar process was left after any case, and the watchdog never fired.

## What the OS logged

[observed: `cases/<id>/oslog-policy.txt`, `oslog.txt`]

* syspolicyd `GK performScan` and `GK evaluateScanResult` lines appear in every launch window, quarantined or not.
* Each launch of the ad-hoc arm64 sidecar (D1 to D6, D9's arm64 copies, Q1a) has exactly one `evaluateScanResult` with `id: a.out`, logged 0.3 to 0.8 s after the sidecar's AMFI lines.
  That it is the sidecar's scan is [inferred]: `a.out` is the sidecar's signing identifier, and the timing fits, but `go build` gives other darwin/arm64 binaries the same identifier.
  It reads `GK evaluateScanResult: 2` where the sidecar ran, and `GK evaluateScanResult: 1` in Q1a.
* The x64 launches' and S1's candidate lines have `id: (null)`, and the log hashes paths, so which one, if any, covers the sidecar is [unknown]; the two D9 x64 windows also hold scans of unrelated third-party code.
  Q1b's line with result 1 is the exception: it carries the same path hash as the prompt and the denial breadcrumb that follow it ([Q1b](#q1b-quarantined-x64-sidecar)).
* Q1a's and Q1b's are the only `evaluateScanResult` lines other than 2 in any case, and both read 1.
  The numbers are quoted as logged; their meaning is [unknown].
* Every case was the first launch of its file from its own path; whether a repeat launch from the same path is scanned is [unknown].
* In every launch of the ad-hoc arm64 sidecar, Q1a included, the kernel logged `AMFI: '.../whatsmeow-net' has no CMS blob?` and `Unrecoverable CT signature issue, bailing out.`; the D launches still started it.

### S1, unsigned arm64 sidecar

| Time | Process | Logged |
| --- | --- | --- |
| 15:06:34.115 | harness | launch |
| 15:06:34.792 | kernel | `(AppleMobileFileIntegrity) AMFI: hook..execve() killing Consumer (pid 17957): Attempt to execute completely unsigned code (must be at least ad-hoc signed).` |
| 15:06:34.792 | kernel | `(AppleSystemPolicy) ASP: Security policy would not allow process: 17957, .../copies/S1/whatsmeow-net` |

The client threw `SidecarUnavailableException: The sidecar did not start: it exited or wrote garbage before its ready line (exit code 137). It wrote nothing to stderr.`, over an inner `IOException: Broken pipe`, and the program ended after 0.749 s.
There is no syspolicyd `Prompt shown` line, and no CoreServicesUIAgent line, in the launch window.
The window's only scan that is neither the apphost nor a Microsoft library is an `id: (null)` line at 15:06:35.227, 0.4 s after the kill, which is not attributed to S1; the refusal is the kernel's, in the AMFI and ASP lines.
On this Mac this sidecar, with no signature and no quarantine, was refused at exec with no prompt, and the same build with the linker's ad-hoc signature ran (D4).
The AMFI line states the rule the kernel applied to this binary here; that it holds on other Macs is [inferred].

### Q1a, quarantined arm64 sidecar

[observed: `logs/Q-pre.txt`] The sidecar's `com.apple.quarantine` was `0081;6aee1751;Safari;926521E3-14D2-4DFA-AD6D-97B6B74B16D1`; the value is synthetic, written by the measurer, and no browser downloaded anything.
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
CoreServicesUIAgent logged AppKit `sendAction` lines from 15:08:10.042 and the mouse-up at .111, 6 ms before `Code evaluation completed`; that this mouse action answered the prompt is [inferred: timing], not an observation of the dialog.
Who acted and which button was pressed are [unknown]: the harness has no UI step, nobody recorded the dialog, and nothing is inferred from the outcome.
The refusal came 29.13 s after launch, inside the client's 30 s `StartupTimeout`; what the client does when a prompt is answered later, or never, is [unknown].
The copy's sidecar still has the same SHA-256 and the same quarantine value [observed: `shasum` and `xattr -l` on it after the run].
In this one synthetic case, a quarantined, ad-hoc signed arm64 sidecar, started by an unquarantined app about 45 s after an assessment of the same code, raised a Gatekeeper prompt, and was refused after the prompt was answered.
Whether quarantine alone explains the difference from D4 is [unknown].

### Q1b, quarantined x64 sidecar

[observed: `cases/Q1b-sidecar-quarantined-x64-rosetta/` `pre.txt`, `post.txt`, `post-dialog.txt`] The sidecar is the packaged osx-x64 binary (`5866f7e7…`, `not signed at all`), with `com.apple.quarantine` `0081;6aee1751;Safari;F8629C5B-4C12-4A4E-97EB-9C642DA05A44`, synthetic like Q1a's; the apphost `Consumer` is D7's (`6eca4f33…`), ad-hoc signed, with no quarantine.
Both files had the same SHA-256 and attributes before the launch, after it, and after the dialog closed.
The copy was prepared with the others and never launched before Q1b, and no assessment ran before or after Q1b.
The same sidecar bytes had run earlier that day, in D7 (15:05:11), D8 (15:05:25) and D9's x64 copies (15:06:02 and 15:06:14), and `spctl` had assessed D7's at 15:06 [observed: their `case.txt`, `logs/setup.log`, `logs/M10-copies.txt`, `logs/assess-D-S.txt`]; whether that changed Q1b is [unknown].
[observed: `host.txt`] The host was unchanged: macOS 26.5.2 (25F84), SIP enabled, `assessments enabled`, XProtect 5371.
The sidecar is an x86_64-only Mach-O on Apple silicon, so Rosetta is [inferred]; unlike D7, D8 and D9's x64 copies, Q1b's window has no `/private/var/db/oah/` line, and whether any sidecar code ran or was translated is [unknown].

Q1b ran under `case-q1b.sh`, which keeps `case.sh`'s launch, isolation and 90 s cap, and differs in four ways [observed: the script, hashed below]:

* it refuses to launch unless both files' hashes and attributes are the ones above;
* it samples only the consumer's PID and its children, and on every exit path kills only its own PIDs: its consumer (at the cap or in cleanup), a live child whose path is the Q1b sidecar, and its own helpers;
* it lists CoreServicesUIAgent's on-screen windows, as metadata with no image, before the launch, at 5 s and 20 s, and after the exit;
* its policy capture adds `process == "oahd"` and kernel `ASP` messages to `case.sh`'s, so no earlier case can be compared on those lines.

`oslog-after.txt`, `oslog-after-2.txt`, `post-dialog.txt`, `pids-after.txt` and `root-report.txt` were made by hand after the driver exited, and the `log show` predicate of the two `oslog-after` files is not recorded with them.
The coordinating agent answered the dialog with a computer-use tool; the owner did not take part [observed: the coordinator's report, `root-report.txt`].

| Time | Source | Logged or observed |
| --- | --- | --- |
| 23:54:12.584 | harness | launch, consumer PID 86579 |
| 23:54:12.931 | syspolicyd | `GK evaluateScanResult: 2, PST: (path: 1ddeb996444af9f5), (team: (null)), (id: apphost), ...` |
| 23:54:23.254 | `ps.log` | sidecar PID 86972 first seen, the consumer's child, running from `copies/Q1b/whatsmeow-net` |
| 23:54:23.499 | syspolicyd | `temporarySigning type=1 matchFlags=0x0 path=.../copies/Q1b/whatsmeow-net` |
| 23:54:23.729 | syspolicyd | `GK evaluateScanResult: 1, PST: (path: 6f89235a3eae2501), (team: (null)), (id: (null)), ...` |
| 23:54:23.730 | syspolicyd | `Prompt shown (8, 0), waiting for response: PST: (path: 6f89235a3eae2501), ...` |
| 23:54:23.734 | CoreServicesUIAgent | `present code-evaluation prompt` |
| 23:54:32 | window list (`windows.t20`) | one CoreServicesUIAgent window, none at 23:54:17 |
| 23:54:51.246 | `ps.log` | sidecar last seen |
| 23:54:51.336 | kernel | `(AppleSystemPolicy) ASP: Sleep interrupted: ref 77352, signal 0x100, pid: 86972` |
| 23:54:51.336 | kernel | `(AppleSystemPolicy) ASP: Security policy would not allow process: 86972, .../copies/Q1b/whatsmeow-net` |
| 23:54:51.983 | harness | consumer exit 134, after 39.399 s |
| 23:54:52 | window list (`windows.after`) | the same window still on screen |
| 23:55:44.376 | coordinator | read the dialog through accessibility, then invoked its Done button; the tool call timed out after 5.8 s, and a follow-up accessibility read timed out |
| 23:55:45.123 | CoreServicesUIAgent | `(AppKit) performClick:` |
| 23:55:45.138 | syspolicyd | `Code evaluation completed: 2` and `Adding Gatekeeper denial breadcrumb (direct): PST: (path: 6f89235a3eae2501), ...` |
| 23:55:45.171 to .172 | syspolicyd | `Found provenance data on target` and `Created provenance data for target: TA(7dd874d1846aa5ed, 2), PST: (path: 6f89235a3eae2501), ...` |
| 23:56:02.837 | coordinator | the app inventory lists no CoreServicesUIAgent |

The client threw `SidecarUnavailableException: The sidecar did not start: it printed no ready line within 00:00:30 (exit code 137). It wrote nothing to stderr.`; no process was left after the case, and the client's copy fallback wrote nothing.
The dialog read "whatsmeow-net" Not Opened, said Apple could not verify that it is free of malware, and offered Help, Move to Trash and Done [observed: the coordinator's accessibility read].
The prompt was the sidecar's [inferred: the dialog names it, and the prompt and the breadcrumb carry the same path hash].
The exception's text comes only from the timeout branch of `SidecarProcess.StartAsync`, which kills the process tree before it reads the exit code, and the timer starts before the spawn [observed: `SidecarProcess.cs:121-131` and `:333`, `WhatsmeowSidecar.cs:435`, at `e5cf3b1`].
ps saw the sidecar for 28.0 s of those 30 s; what took the rest, between the timer's start and the first sample, is [unknown].
The two ASP lines came when that kill hit the sidecar while the prompt was still waiting [inferred: the code path, exit code 137, and the interrupted ASP sleep in the same millisecond]; what `signal 0x100` means is [unknown].
The coordinator's Done came 53.0 s after the ASP lines and 52.4 s after the consumer exited, too late to allow or refuse that launch [inferred: both processes had exited].
The `performClick:` 0.75 s after it is that click [inferred: timing; the tool call itself timed out], and the app inventory at 23:56:02.837 no longer listed CoreServicesUIAgent, which does not mean the process exited.
What `Code evaluation completed: 2`, the breadcrumb and the provenance lines record about this file beyond their text, and whether they leave state for a later launch of this file or path, are [unknown]; `copies/Q1b` is no longer a first-launch control.
The `temporarySigning` line, in no other case's log, is quoted with its meaning [unknown].
No AMFI line names this sidecar, unlike every ad-hoc arm64 launch.
In this one synthetic case, a quarantined, unsigned x64 sidecar, spawned by an unquarantined app after launches and an assessment of the same code earlier that day, raised a Gatekeeper prompt and never printed its ready line, and the client gave up at its 30 s `StartupTimeout` with the prompt still on screen.
Whether it would have run if the prompt had been allowed in time, and whether quarantine alone explains the difference from D7, are [unknown].

## What this settles, and what it does not

* "Apple Silicon needs arm64 code to be signed at all" is observed for this sidecar on this Mac (S1, its AMFI line), and the linker's ad-hoc signature is enough there without quarantine (D1 to D6, D9's arm64 copies).
* "Gatekeeper assesses only quarantined launches" does not hold as worded: a scan with the sidecar's signing identifier appears in every ad-hoc arm64 launch, quarantined or not (attribution [inferred], above).
  Of the ad-hoc sidecars, only the quarantined one raised a prompt, and it was refused after the prompt was answered; the unsigned S1 sidecar was refused without a prompt.
  The quarantined, unsigned x64 sidecar under Rosetta [inferred] raised a prompt too, and the client gave up at its `StartupTimeout` before the prompt was answered (Q1b).
* These supported delivery paths put no quarantine on the sidecar, and each started it: restore from a loopback HTTP feed; portable build and `dotnet run`, portable publish and framework-dependent publish, arm64; self-contained and single-file publishes and their `cp -R` and `ditto` copies, arm64 natively and x64 under Rosetta [inferred].
* S1, Q1a and Q1b are synthetic controls, not delivery paths.
  No measured supported delivery path was blocked, so ADR 0007's decision stands and no signing ADR is triggered.
  Per ADR 0007's Consequences, a supported delivery path that sets quarantine or Mark-of-the-Web reopens signing by a new ADR; that an app which embeds us and ships signed covers the sidecar stays [inferred] (#51).

Unmeasured, so no claim covers them:

* a restore from nuget.org, since nothing is published yet;
* a clean Mac, a native Intel desktop, and other macOS versions (extras, not #45 gates);
* osx-x64 framework-dependent on this Mac;
* Q1a and Q1b without a prior assessment or launch of the same code, and a repeat launch of one file from one path, such as Q1b's copy, which now has a denial breadcrumb;
* Q2a to Q2c (extras, not #45 gates): a quarantined app directory, and whether the apphost is refused first;
* a prompt allowed within `StartupTimeout`, and a prompt nobody answers (Q1b's stayed on screen after its client exited, until it was answered);
* which tools, transfers or user actions set quarantine on a consumer's machine, beyond the paths measured here;
* what Q1a's prompt offered, and what each answer does (Q1b's offered Help, Move to Trash and Done; Done came after its process had exited);
* the launching terminal's Developer Tools exemption;
* Windows: SmartScreen, Mark-of-the-Web, Smart App Control, and §10's Temp ACL for a non-administrator, since measured in [the Windows record](0045-windows-smartscreen-2026-11-10.md), except a desktop session and Smart App Control in enforcement;
* the notarized `.app` claim, which stays [inferred] (#51).

## Superseded raw claims

The measurer's `RESULTS.md` stays as recorded (hash below), and this record wins where they differ.
A dated private addendum, kept beside the archive below, lists the same corrections.
Q1b's private addendum is corrected the same way by a later private corrections file (below), and this record wins over both.

* "Ordering: every launch first": the assessments preceded Q1a, on byte-identical code (Method).
* Every launch "got a syspolicyd `GK performScan` of the apphost, the .NET native libraries and the sidecar": scans appear in each window, the .NET libraries' only in some; only the arm64 `a.out` scan is tied to the sidecar, and that is [inferred].
* The unsigned x64 sidecar "scans with result 2": which line covers it is [unknown].
* "Only the quarantined sidecar raised a prompt and was then refused": S1 was refused too, without a prompt.
* S1's cause: the AMFI line above, not quoted there.
* Rosetta "observed" from syspolicyd reading `/private/var/db/oah/`: the lines are kernel sandbox denials, and Rosetta is [inferred].
* Warm caches, and `com.apple.provenance` written by the terminal's process tree: [inferred] and [unknown].
* The host record as observed command output: it is a transcription in `plan.md`.
* M5 to M9 as "portable/fd/sc/single-file publishes (arm64, x64)": scoped as above.
* Native Intel as pending: CI's native osx-x64 install and AOT runs passed, with no quarantine metadata recorded; the desktop and quarantine are open.
* Apple not documenting the scan numbers: not checked; their meaning is [unknown].

## Evidence provenance

The raw records stay private: their OS logs include unrelated activity of a daily-use machine, and their scripts and paths name the host.
They were recorded in `/tmp/whatsmeow-issues/45-mac-current/` on the measuring Mac, which still holds them unchanged; Q1b later added 29 files there, its review corrections one more, and neither changed any.
Since `/tmp` may not last, a private archive of that whole directory, every file with its extended attributes, is kept outside git in `~/Library/Application Support/Whatsmeow.NET/evidence/` (directory 0700, files 0600).

| Private artifact | SHA-256 |
| --- | --- |
| `45-mac-current-2026-11-06.tar.gz`, the archive, 1,890,222,583 bytes | `8ea3ba3d682d2770c32dcdd5ec172af3e33197f5caf7d92674d4895c18da482b` |
| Its 4,901 files, checked against the originals | `0d5ed358712017bbafe67d83bb363a008d75c6d213035788fb7d5f9328736ab1` |
| `45-mac-current-addendum-2026-11-06.md`, the dated addendum | `94a2b98a3af0f8fcf313bef9f7ab8f47c701e91bf3e8b97d65977b2cd78c0daa` |

The file digest is `find 45-mac-current -type f | LC_ALL=C sort | while read -r f; do printf '%s  %s\n' "$(shasum -a 256 "$f" | cut -d' ' -f1)" "$f"; done | shasum -a 256`, run in `/tmp/whatsmeow-issues`; the archive's members, hashed while streamed, give the same list.
With those 30 files left out, the directory still gives the 4,901 files' digest, which checks their content; their extended attributes were not re-checked, beyond the two Q1b files'.

| Q1b private artifact | SHA-256 |
| --- | --- |
| `45-mac-q1b-2026-11-07.tar.gz`, Q1b's archive, 49,023 bytes | `98ebd47f5e1dd911ea980b08e501dca5cbc9cf701c1e1bf3776d5e756d537fbc` |
| Its 29 files, by the same command over their list | `0cfd482d6735c73f13591a6c82de38ab8266df4acf880385dab3c5d7d75b289f` |
| `45-mac-q1b-addendum-2026-11-07.md`, Q1b's dated addendum | `2f00ac0cf7f3a7ac4dcefcddfd83b206957e8922887807ed1669e784a800bbe1` |
| `45-mac-q1b-corrections-2026-11-07.md`, corrections to that addendum, not in the archive | `df72ecbc95e2b0d943e15e60fe82c417bb6a24805370bcfe00e7f2fb96529cce` |

Q1b's 29 files are `q1b-plan.md`, `RESULTS-Q1b-addendum-2026-11-07.md`, `scripts/case-q1b.sh`, `scripts/uiagent-windows.swift` and its build `scripts/uiagent-windows`, `logs/Q1b-run.txt`, and the 23 files of `cases/Q1b-sidecar-quarantined-x64-rosetta/`.
The Q1b archive also holds an AppleDouble `._*` member per file, carrying its extended attributes: a generic tar reader sees 58 members, macOS `tar -t` shows 29, and the list matches once the `._*` members are left out.
The corrections file is also in the measurement directory, as `RESULTS-Q1b-corrections-2026-11-07.md`.

| Artifact | SHA-256 |
| --- | --- |
| `RESULTS.md`, the measurer's results | `9e39db6a5fcca1baf5d7af52874a4917e3d7a70b3e7e276ef47959d7eca6c4d5` |
| `plan.md`, host record and matrix | `13dd21708ad41debd0b207db8cedff948f0bb1d25868f4c2ce490ed520f73660` |
| `artifacts/checksums.sha256`, both feeds' files | `5dc56e33c888b98c8d6da77fff3c12e49bee9f89b2411c9d40b2785a5a84be42` |
| `scripts/env.sh` | `796f3f7edf9ff327e4a53c71894128761671e342d5f9ba258dd9fa22e37afb8b` |
| `scripts/global.json`, the consumer's SDK pin | `974a1476882b8cf92fd9e2226347dc467b70c24d1016a9122fa6dd04c983fde5` |
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
| `scripts/case-q1b.sh` | `dbf2e43868f7481a28f934858c348c63847bec400675c46b49b5bb2634728c4a` |
| `scripts/uiagent-windows.swift` | `bc39ae20d30033f164ed2fe31ddd876c83fa96756cb3ae7c7f7f7a96253dd9e9` |
| `logs/Q1b-run.txt`, the driver's output | `74f3b51766b713b170373b177cfc1b033c4fc1c50cd10636fd453444ffac60ea` |
| `cases/Q1b-sidecar-quarantined-x64-rosetta/oslog-policy.txt` | `9c52800b35c59f5ad9968d5a66b7fdcb23e74a894b314115d8309f7da27f8f03` |
| `cases/Q1b-sidecar-quarantined-x64-rosetta/oslog.txt` | `f360f967c75d569aa1fc6dd5dea3796e5e532bd7887df7cd0d24083533576bbf` |
| `cases/Q1b-sidecar-quarantined-x64-rosetta/oslog-after-2.txt`, 23:54:52 to 23:56:15 | `628b270e8296fb68eb324886c31c02f7c9a701a5345aa6357b7b94b0abf0ff40` |
| `cases/Q1b-sidecar-quarantined-x64-rosetta/root-report.txt`, the coordinator's report | `ef3de7368054517b47796dec8c7b52df6ce363d0090653017f089c471366b12e` |
| `cases/Q1b-sidecar-quarantined-x64-rosetta/`, its 23 files | `cbc3e681bc5d754b458ee45a3303f1701074d51a98550bb7d0702d43f6ce9a8b` |
| `cases/`, all 126 files of the first 14 cases | `d4a7dd12889f8b4e6811183ac94c257693936d3b02b8c11b487f0ec9a687bc93` |

The `cases/` digest is `find cases -type f | LC_ALL=C sort | xargs shasum -a 256 | shasum -a 256`, run in the measurement directory before Q1b; Q1b's directory digest is the same command over it.
To repeat the measurement: download the run's `native-packages` artifact, then run `setup.sh`, the preparation above, and `case.sh <id> <dir> <command>` per case; the scripts hard-code the measurement directory and the host's `DOTNET_ROOT`.
`copies/Q1b` is no longer a first-launch control: a repeat of Q1b needs a fresh copy.
Do not reuse `case.sh`'s cleanup as it is: it kills leftover processes it selects by path pattern, and a rerun should kill only the exact PIDs it started.
It killed nothing in these runs; `case-q1b.sh` kills only its own PIDs, and killed nothing either.
