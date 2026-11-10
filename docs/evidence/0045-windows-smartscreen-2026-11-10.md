# Windows SmartScreen on the packaged sidecar, 2026-11-10

Evidence for [#45](https://github.com/tcortega/Whatsmeow.NET/issues/45), its Windows half, measured on one real laptop.
Its macOS half is [evidence 0045, macOS](0045-macos-gatekeeper-2026-11-06.md); a restore of `whatsmeow-net` from nuget.org is still unmeasured, and waits for the first publish (#52).
The owner authorized his own laptop, driven over SSH with nobody at it, in place of the desktop run #45 planned, so this record has no desktop session: everything below ran in session 0, and what an interactive desktop does is [unknown].
Every claim is scoped to this host, this session type and these packages; an [inferred] or [unknown] claim is an open question, not a result.
This record is authoritative over the raw results it draws on; the raw claims it corrects are listed under [Corrections to the raw record](#corrections-to-the-raw-record).
Times are UTC, as the harness recorded them; the host's clock was UTC-3.
Excerpts are from the redacted copy of the records, reviewed line by line before publishing ([Redaction review](#redaction-review)); `<HOME>`, `<USER>`, `<MACHINE>` and `<USER-SID>` are its placeholders.

## Host

[observed: `logs/measured/host-before.txt` and `host-after.txt`, written by `host.ps1` at 17:30:01 and 17:37:41]

* Windows 11 Home, version 25H2, build 26200.9711 (`EditionID` `Core`, `InstallationType` `Client`), x64.
  The registry's `ProductName` reads `Windows 10 Home`; the caption reads `Microsoft Windows 11 Home`.
* A real laptop with a 12th-gen Intel Core CPU: `Win32_ComputerSystem` reports `PCSystemType` 2 (mobile) and `HypervisorPresent` `False`, and the owner states it is not a VM or a CI runner.
  That `PCSystemType` 2 means a mobile machine is [inferred: the class's documentation]; `HypervisorPresent` would be `True` on bare metal with virtualization-based security, so `False` rules out a guest only together with the owner's statement [inferred].
  Its vendor and model are recorded privately and left out here.
* Smart App Control: `Get-MpComputerStatus` reports `SmartAppControlState` `Off`, and `CI\Policy\VerifiedAndReputablePolicyState` is 0.
* SmartScreen settings: `Explorer\SmartScreenEnabled`, and the `EnableSmartScreen` and `ShellSmartScreenLevel` policies, are absent or unreadable for this account, so the machine runs Windows' default SmartScreen configuration [inferred: no setting or policy found under those names; that they are the names that matter is also inferred].
  No screenshot of Windows Security was possible without a desktop.
* Microsoft Defender: antimalware client 4.18.26100.4, signatures 1.463.223.0, real-time protection on, tamper protection off.
* Event channels: `Microsoft-Windows-CodeIntegrity/Operational`, `Microsoft-Windows-Windows Defender/Operational` and `Microsoft-Windows-AppLocker/EXE and DLL` are enabled and readable by this account; `Microsoft-Windows-SmartScreen/Debug` is disabled (`enabled: false`), and the run did not enable it.
* .NET SDK 10.0.403 x64, with runtimes 10.0.14, installed for this run only under the account's own directory by `dotnet-install.ps1` [observed: `logs/sdk.txt`]; no other SDK or runtime was used.

The laptop is in daily use by its owner, not a clean install; nothing here says what a fresh install does [inferred: SmartScreen and Defender keep state across launches].

### The account

[observed: `host-before.txt`, `host-after.txt`; the account's creation and its group changes from the private remote logs `01`, `04` and `19`]

`wmn-test` is a local account created for this run in `BUILTIN\Users` only, never in Administrators.
Each step ran as `wmn-test` in a one-time scheduled task with a password logon and the `Limited` run level, so in a batch logon:

* the token holds `BUILTIN\Users`, `BUILTIN\Performance Log Users`, `NT AUTHORITY\BATCH`, `CONSOLE LOGON`, `Authenticated Users`, `This Organization`, `Local account`, `LOCAL` and `NTLM Authentication`, with the Medium integrity label, and no `S-1-5-32-544`;
* `net localgroup` resolved from `S-1-5-32-544` does not list the account, so `host.ps1` printed `account-verdict standard` before and after the run;
* the process ran in session 0, with no `SSH_CONNECTION`, and no Explorer in that session.

Why `CONSOLE LOGON` is in a batch logon's token is [unknown]; it is quoted as `whoami /groups` printed it.
`Performance Log Users` was added for the run only, and removed after it (see [Deviations](#deviations-from-the-plan)).

## Packages under test

[observed: the GitHub run and artifact APIs; `logs/arrive.txt`; `meta.ps1` output in `logs/measured/setup.log`] One feed, the `native-packages` artifact 13782596122 of CI run [42853970987](https://github.com/tcortega/Whatsmeow.NET/actions/runs/42853970987), a push to main at `210097c386a9b816dd21203f8e0a22dee8bd8879` whose 20 jobs all succeeded, version `0.0.0-alpha.0.94`.
The zip, 90,143,289 bytes, has SHA-256 `ac565ed99f777e1f22c5be8e2886ec7d5c767f750734856db270a81c77bee8b9`, the digest the API reports, before and after it reached the laptop.

| File | SHA-256 | Authenticode |
| --- | --- | --- |
| `runtimes/win-x64/native/whatsmeow-net.exe`, 26,953,216 bytes | `524c2a39948353e5f90ddd09cfcb8f4a8f2d0d0bcb356731e8a74b54b63595b8` | `NotSigned` |
| `runtimes/win-arm64/native/whatsmeow-net.exe`, 25,309,696 bytes, never launched | `f827913d2b600d873c7d891f2d6c8ea563bdc9bf7c0210d2c51db8c41a64aa10` | `NotSigned` |
| `Whatsmeow.Sidecar.Native.Windows.0.0.0-alpha.0.94.nupkg` | `256966c4787ba00915d1c8b55d475285fa695a68609d7fd579f85ea57661316f` | |
| `Whatsmeow.Client.0.0.0-alpha.0.94.nupkg` | `e0d6e7e30acbe1f373c79a18867b807eb8ff5b7d15e7f22b5639bd800686ebce` | |
| `Whatsmeow.Protocol.0.0.0-alpha.0.94.nupkg` | `021debd015a3ab42c2de372e7435d7418b242711ef823168cdf5e33378ea93ea` | |
| `Whatsmeow.Protocol.WhatsApp.0.0.0-alpha.0.94.nupkg` | `f0efb37389d46b95ee868cc55225961403082e0bc13b4ca70cc64e45258fb772` | |
| `Whatsmeow.Extensions.Hosting.0.0.0-alpha.0.94.nupkg` | `4ffe0b8b8251367cea305fd22d245e0dc1dad1da7aeb458c9c0e926157449fd0` | |
| `Whatsmeow.Testing.0.0.0-alpha.0.94.nupkg` | `bc04f54cb56f7884681e0c295282fbb39259037cae45bcd07cd6bea5cae70599` | |
| `Whatsmeow.Sidecar.Native.Linux.0.0.0-alpha.0.94.nupkg` | `7b3b16a7fafa5189a8df8c7dfa0a82c73c35cb1d529a6541743db2ca92337f07` | |
| `Whatsmeow.Sidecar.Native.MacOS.0.0.0-alpha.0.94.nupkg` | `5ea0d18fc5783f1a4f1f019895dcf44a5fd0db90cc96a5f6501232317cba7c85` | |
| `whatsmeow-net_0.0.0-alpha.0.94_source.tar.gz` | `665159af88d15fe7141b5f50aa5d920b5f136b0f675d06405d7fa90da6de28ee` | |
| The consumer's apphost `Consumer.exe` (portable, framework-dependent and self-contained publishes) | `9791f4036962f963cd0b3412a97d33b14fdca085327c0ba06588d287432c5849` | `NotSigned` |
| The consumer's single-file `Consumer.exe`, with or without `IncludeNativeLibrariesForSelfExtract` | `64ebbef7f0201f54369a4c615d0ab9cf07690805298ee9e01adbf042e5f8543b` | `NotSigned` |

Every copy of the win-x64 sidecar in every case had the hash above, before and after its launch.

## Method

[observed: the archive's `scripts/`, hashed below]

* The consumer is `build/package-smoke/Program.cs` from `210097c`, byte for byte (`82432ad4…42b3`, the same bytes as the macOS run's), in a `dotnet new console` app that references `Whatsmeow.Client`, `Whatsmeow.Extensions.Hosting` and `Whatsmeow.Testing` at the feed version.
  It uses the public API only: start, `GetInfoAsync`, dispose, with no `SidecarPath`, `WHATSMEOW_NET_SIDECAR_PATH` unset, and no pairing; its `StartupTimeout` is the 30 s default.
  It first prints the runtime and checks that the sidecar is at the path the layout argument names, then prints `sidecar <version>, protocol <n>` only after `GetInfoAsync` returned.
* The feed is a static NuGet v3 tree of the eight nupkgs, served over plain HTTP on a loopback port by a small Kestrel app, `Serve`, with a request log, and mapped to `Whatsmeow.*` with `allowInsecureConnections`; every other package came from nuget.org [observed: `http-server.log` and NuGet's `GET` lines in `setup.log`].
  It is a local HTTP feed, not nuget.org.
* `DOTNET_CLI_HOME`, `NUGET_PACKAGES` and `NUGET_HTTP_CACHE_PATH` pointed into the run's directory and were emptied before setup, so the restore came over HTTP; each launch had its own `TEMP` and `TMP`, except A2, which needs the real one.
* `meta.ps1` records, per file and stage: SHA-256, size, every NTFS stream, the `Zone.Identifier` text, the Authenticode status and `icacls`.
* `case.ps1` starts each case with `Start-Process` and redirected output (so `CreateProcess`, not a shell launch [inferred: PowerShell's redirection needs `UseShellExecute` false]), caps it at 90 s, never reached, and samples every 0.3 s: the launch tree (`ps.log`), and any `smartscreen.exe`, `consent.exe`, `CompatTelRunner.exe`, `WerFault.exe` or `WerFaultSecure.exe` created during the launch, the only other processes it records (`new-procs.log`).
  After a 30 s tail it reads the four channels above for the window from 5 s before the launch to the end of the tail, keeping only events that name the case's files, and exports the whole window to a private `.evtx`.
  It kills only PIDs in its own launch tree, and killed none.
* The harness changed no Defender, SmartScreen, UAC, Smart App Control or system policy setting.

### Deviations from the plan

#45's plan was a desktop run: the files on a USB stick, a standard account signed in at the laptop, and an Explorer double-click last.
The owner chose instead, on 2026-11-10, to have the agent drive his laptop over SSH with nobody at it.
So:

* **No USB; Mark-of-the-Web applied by the harness.**
  The zip was copied over the network with `scp`, which left no `Zone.Identifier` stream on it [observed: M1].
  `arrive.ps1` then verified every file against the Mac's checksums and wrote, with `Set-Content -Stream`, the stream a browser download writes, `ZoneId=3` with `ReferrerUrl` and `HostUrl` naming the run and the artifact, on the zip and, after extracting it, on every extracted file, as Explorer's Extract All propagates it [observed: M1; that a browser and Explorer write exactly this is inferred].
  Nobody downloaded the artifact with a browser.
* **SSH with batch logons in session 0.**
  The laptop's sshd accepts public keys only, for two existing accounts, so `wmn-test` could not log in over SSH without an sshd change, which the coordinator ruled out [observed: the private preflight record].
  Instead an existing administrator account staged the files and ran each step as `wmn-test` in a one-time scheduled task, deleted right after; every case therefore ran in session 0 with no desktop.
* **Performance Log Users, temporarily.**
  A password-logon task for a `Users`-only account never ran: the Security log has 4625 events, logon type 4, "The user has not been granted the requested logon type at this machine", and `SeBatchLogonRight` is held by `S-1-5-32-544`, `S-1-5-32-551` and `S-1-5-32-559` only [observed: the private remote logs `03b` and `03c`].
  A `Start-Process -Credential` attempt exited `0xC0000142`, though it created the profile [observed: `03d`]; why is [inferred: no window station in session 0].
  So `wmn-test` joined `Performance Log Users` (`S-1-5-32-559`), which holds that right, at 17:18:45, before the first step that ran, and left it at 17:39:43, after the last; `SeBatchLogonRight` itself was not changed [observed: `04`, `19`].
  The account stayed a non-administrator throughout (above).
* **No desktop session, so Z3 NOT RUN.**
  Windows 11 Home has no Remote Desktop host [inferred: Home editions do not host RDP] and nobody was at the laptop, so the Explorer double-click (Z3) was not run, and no case ran in an interactive desktop session; `run-cases.ps1` wrote the reason to `cases/measured/Z3.txt`.
  Z1 and Z2 launch the marked files through the consumer instead, which is how the library launches the sidecar.
* A dry run (`dryrun1`, 17:21 to 17:29) with the same scripts, unchanged after it, passed every case first; it is not evidence, but it launched the same bytes from the same paths 7 to 8 minutes before each measured launch, after which setup deleted and rebuilt every derived directory [observed: both runs' `case.txt` and `setup.log`; `setup.ps1`].
  Whether those earlier launches changed the measured ones is [unknown].

### Commands per case

Commands are as `case.txt` recorded them, relative to the run directory; `dotnet` stands for the SDK's `dotnet.exe` and `V` for `0.0.0-alpha.0.94`.
Each publish also passes `app -c Release -warnaserror -o out/<name>` [observed: `setup.log`].

| Case | Command | Preparation |
| --- | --- | --- |
| D1 | in `consumer`: `dotnet run --project app -c Release --no-build -- start V win-x64 portable` | `dotnet build app -c Release -warnaserror` |
| D2 | in `out/portable`: `dotnet out/portable/Consumer.dll start V win-x64 portable` | `dotnet publish --self-contained false` |
| D3 | `out/fd/Consumer.exe start V win-x64 flat` | `-r win-x64 --self-contained false` |
| D4 | `out/sc/Consumer.exe start V win-x64 flat` | `-r win-x64 --self-contained true` |
| D5 | `out/single/Consumer.exe start V win-x64 flat` | D4's, plus `-p:PublishSingleFile=true` |
| D6 | `out/single-native/Consumer.exe start V win-x64 flat` | D5's, plus `-p:IncludeNativeLibrariesForSelfExtract=true` |
| D7 | `copies/copyitem/Consumer.exe start V win-x64 flat` | `Copy-Item -Recurse` of `out/sc` |
| D8 | `copies/robocopy/Consumer.exe start V win-x64 flat` | `robocopy out/sc copies/robocopy /E`, exit 1 (files copied [inferred: robocopy's exit codes]) |
| Z1 | `copies/Z1/Consumer.exe start V win-x64 flat` | a `Copy-Item` of `out/sc`, then a `Zone.Identifier` on its `whatsmeow-net.exe` only |
| Z2 | `copies/Z2/Consumer.exe start V win-x64 flat` | the same, with a `Zone.Identifier` on every file in the copy |
| A2 | `dotnet aclprobe-bin/AclProbe.dll keep`, with the account's real `TEMP` | `dotnet build AclProbe -c Release -warnaserror` |

Z1's and Z2's stream is `[ZoneTransfer]`, `ZoneId=3`, `HostUrl=about:internet`, 50 bytes, written by `Set-Content -Stream` [observed: `setup.ps1`, `meta-pre.txt`]; it is shorter than M1's browser-style stream, and whether SmartScreen treats the two differently is [unknown].

## Metadata, M1 to M10

[observed: `logs/arrive.txt` and `logs/measured/setup.log`]

* M1, arrival: after `scp`, the zip, `feed.env` and `checksums.sha256` had no `Zone.Identifier`; `arrive.ps1` then marked the zip.
  The ten files `ZipFile.ExtractToDirectory` extracted had no `Zone.Identifier` either, and `arrive.ps1` marked each of them; at setup, the zip and all ten extracted files carried the browser-style stream:

  ```text
  ## M1-at-setup <HOME>\wmn\artifacts\feed\Whatsmeow.Sidecar.Native.Windows.0.0.0-alpha.0.94.nupkg
    sha256 256966c4787ba00915d1c8b55d475285fa695a68609d7fd579f85ea57661316f size 18920320 mtime 2026-11-10T17:19:23.8480452Z
    stream :$DATA 18920320
    stream Zone.Identifier 204
    zone [ZoneTransfer]
    zone ZoneId=3
    zone ReferrerUrl=https://github.com/tcortega/Whatsmeow.NET/actions/runs/42853970987
    zone HostUrl=https://github.com/tcortega/Whatsmeow.NET/actions/runs/42853970987/artifacts/13782596122
  ```

* M2, the native package NuGet fetched over the loopback feed into the empty packages folder: no `Zone.Identifier`.

  ```text
  ## M2-nupkg-in-packages <HOME>\wmn\packages\whatsmeow.sidecar.native.windows\0.0.0-alpha.0.94\whatsmeow.sidecar.native.windows.0.0.0-alpha.0.94.nupkg
    sha256 256966c4787ba00915d1c8b55d475285fa695a68609d7fd579f85ea57661316f size 18920320 mtime 2026-11-10T17:30:32.4212566Z
    stream :$DATA 18920320
    zone (no Zone.Identifier stream)
  ```

* M3, the two binaries NuGet extracted from it: no `Zone.Identifier`, `NotSigned`.
* M4, the portable build output, and M5 to M9, the portable, framework-dependent, self-contained, single-file and single-file-with-native-libraries publishes: no `Zone.Identifier` on the sidecar, nor on the publishes' `Consumer.exe`.
* M10, the `Copy-Item` and `robocopy` copies of the self-contained publish: no `Zone.Identifier` on either file.

So on this path the nupkg on disk that NuGet installed from carried no Mark-of-the-Web, although the nupkg the loopback feed was built from did.
That HTTP cannot carry an NTFS stream, and that NuGet writes none, explains it [inferred]; whether the tree the server served still carried the stream after `make-feed.ps1`'s `Copy-Item` was not recorded, and does not matter to that.
The sidecar's hash did not change at any stage, and every file's ACL was inherited from the run directory: `SYSTEM`, `BUILTIN\Administrators` and the account, full control.

## Launches

[observed: `cases/measured/<id>/case.txt`, `stdout.txt`, `stderr.txt`; the Sidecar column from `ps.log`] Each launch goes through the public client: start, `GetInfoAsync`, dispose.
Every launch exited 0, wrote nothing to stderr, and printed `runtime win-x64, process X64, OS X64` and `sidecar 0.0.0-alpha.0.94, protocol 1`.

| Case | Delivery path | Sidecar | Result |
| --- | --- | --- | --- |
| D1 | portable build, `dotnet run` | `bin\Release\net10.0\runtimes\win-x64\native\` | exit 0, 1.839 s, sidecar PID 2724 |
| D2 | portable publish, `dotnet Consumer.dll` | `runtimes\win-x64\native\` | exit 0, 1.451 s, PID 5756 |
| D3 | framework-dependent `-r win-x64` | next to the app | exit 0, 1.475 s, PID 10112 |
| D4 | self-contained `-r win-x64` | next to the app | exit 0, 1.842 s, PID 16440 |
| D5 | single-file | next to the app | exit 0, 0.65 s; not in `ps.log` |
| D6 | single-file with `IncludeNativeLibrariesForSelfExtract` | next to the app | exit 0, 0.64 s; two children with no path in `ps.log` |
| D7 | `Copy-Item` copy of D4, run from the copy | next to the app | exit 0, 1.859 s, PID 11956 |
| D8 | `robocopy` copy of D4, run from the copy | next to the app | exit 0, 1.88 s, PID 500 |
| Z1 | synthetic control: copy of D4, `Zone.Identifier` on `whatsmeow-net.exe` only | next to the app, marked | exit 0, 1.862 s, PID 4428 |
| Z2 | synthetic control: copy of D4, `Zone.Identifier` on every file, `Consumer.exe` included | next to the app, marked | exit 0, 1.855 s, PID 5312 |
| Z3 | synthetic control: Explorer double-click of a marked sidecar | | NOT RUN: no desktop session |
| | win-arm64 | | not run: x64 laptop |

* D5's sidecar exited before a 0.3 s sample saw it, and D6's samples caught the app's child and grandchild after their paths were gone; that both ran is [inferred: the `sidecar …` line is printed only after `GetInfoAsync` returned], and which binary D6's child was is [unknown].
* Every case's sidecar had the same hash, Authenticode status and streams after the launch as before it [observed: `meta-pre.txt`, `meta-post.txt`].
  Z1's and Z2's marks were still there after the launch:

  ```text
  ## Z2-post <HOME>\wmn\copies\Z2\whatsmeow-net.exe
    sha256 524c2a39948353e5f90ddd09cfcb8f4a8f2d0d0bcb356731e8a74b54b63595b8 size 26953216 mtime 2026-11-10T17:31:16.3364041Z
    stream :$DATA 26953216
    stream Zone.Identifier 50
    zone [ZoneTransfer]
    zone ZoneId=3
    zone HostUrl=about:internet
    authenticode NotSigned
  ```

  (`Get-AuthenticodeSignature` appends a `StatusMessage` about script execution policy to `NotSigned`, and every `meta.ps1` entry ends with its `icacls` lines; both are cut from the excerpts here.)
* Z1 and Z2 took 1.862 s and 1.855 s against D4's 1.842 s, so the marks added no delay that one launch each can show.
* Every sampled sidecar ran from the path the layout names, so none of those launches used the client's copy fallback; for D5 and D6 that is [unknown].
  No process was left after any case, and the 90 s cap never fired.

The windows-arm64 sidecar was only hashed here; CI run 42853970987 passed `install (win-arm64)` and `aot (win-arm64)` on native arm64 runners (§11), which recorded no Mark-of-the-Web metadata, so whether those copies carried it is [unknown].

## What the OS logged

[observed: each case's `case.txt`; `new-procs.log`; the private `.evtx` exports]

* No `smartscreen.exe`, `consent.exe`, `CompatTelRunner.exe`, `WerFault.exe` or `WerFaultSecure.exe` process was created during any launch, Z1 and Z2 included: every `new-procs.log` holds only its allowlist header.
* In every case, all four channels returned `No events were found that match the specified selection criteria.` for the launch window, not only for events naming the case's files.
  Every exported `.evtx`, 88 across both runs, is 69,632 bytes, which fits an empty log [inferred: a header and one empty chunk; the exports were not parsed].
* `Microsoft-Windows-SmartScreen/Debug` was disabled, so its silence says nothing; the CodeIntegrity, Defender and AppLocker channels were enabled and readable, so in them no event in the window is an observation.
* The Security log was not read: it needs an administrator.

## Temp ACL, A1 and A2

[observed: `cases/measured/A1.txt`, `A2/stdout.txt`, `A2/icacls.txt`; A2 ran at 17:35:50]

A1 is the account's real `%LOCALAPPDATA%\Temp`; A2 is `AclProbe`, which calls `Directory.CreateTempSubdirectory("wmn-acl-")` as the client does for its socket directory, writes one file in it, and prints the owner and ACL of both.

```text
icacls <HOME>\AppData\Local\Temp NT AUTHORITY\SYSTEM:(I)(OI)(CI)(F)
icacls                                      BUILTIN\Administrators:(I)(OI)(CI)(F)
icacls                                      <MACHINE>\<USER>:(I)(OI)(CI)(F)
owner <MACHINE>\<USER>
sddl O:<USER-SID>G:<MACHINE-SID>-513D:(A;OICIID;FA;;;SY)(A;OICIID;FA;;;BA)(A;OICIID;FA;;;<USER-SID>)
```

```text
dir-owner <MACHINE>\<USER> <USER-SID>
dir-protected False
dir-ace NT AUTHORITY\SYSTEM Allow FullControl inherited=True flags=ContainerInherit, ObjectInherit/None
dir-ace BUILTIN\Administrators Allow FullControl inherited=True flags=ContainerInherit, ObjectInherit/None
dir-ace <MACHINE>\<USER> Allow FullControl inherited=True flags=ContainerInherit, ObjectInherit/None
file-owner <MACHINE>\<USER> <USER-SID>
file-protected False
file-ace NT AUTHORITY\SYSTEM Allow FullControl inherited=True flags=None/None
file-ace BUILTIN\Administrators Allow FullControl inherited=True flags=None/None
file-ace <MACHINE>\<USER> Allow FullControl inherited=True flags=None/None
```

So for this standard account the temp directory, the `CreateTempSubdirectory` directory and the file in it grant full control to `SYSTEM`, `BUILTIN\Administrators` and the user, all inherited, and nobody else; unlike the elevated CI runner §10 cites, the directory and the file are owned by the user, not by `BUILTIN\Administrators`.
The process was not elevated (`admin-role-elevated False`).
This was a batch logon; that a desktop logon of the same account gets the same ACL is [inferred: the ACL is inherited from the profile directory, not set per session].
The probe directory was deleted after `icacls` read it, and no `wmn-*` directory was left in the account's temp directory [observed: `run-cases.log`, the private leftovers record].

## What this settles, and what it does not

* The unsigned `whatsmeow-net.exe` from these packages, restored from a loopback HTTP feed whose source nupkgs carried Mark-of-the-Web, reached the packages folder, the build output, every publish and both copies without a `Zone.Identifier`, and each of D1 to D8 started it [observed: M2 to M10, D1 to D8].
  For this path, NuGet's restore never carried the mark onto the package or the binary [observed]; for a restore from nuget.org it is [unknown] until #52 publishes, though the `protoc.exe` that `dotnet restore` pulled from nuget.org had none ([ARCHITECTURE §14](../ARCHITECTURE.md#spike-results-2026-10-31) question 2).
* The same sidecar with a `Zone.Identifier` of zone 3, and, in Z2, an apphost with one too, was started through `CreateProcess` by the consumer in a session 0 batch logon, and ran, with no SmartScreen process, no CodeIntegrity, Defender or AppLocker event, and no measurable delay [observed: Z1, Z2].
  So on this path Mark-of-the-Web alone did not block or slow the sidecar.
  That SmartScreen's app reputation check runs on a shell launch, not on `CreateProcess`, would explain it [inferred]; it was not measured.
* Z1 and Z2 are synthetic controls, not delivery paths.
  No measured supported delivery path was blocked or marked, so ADR 0007's decision stands and no signing ADR is triggered.
* For a standard account, the `%LOCALAPPDATA%\Temp` ACL and a `CreateTempSubdirectory` directory in it are `SYSTEM`, `BUILTIN\Administrators` and the user, full control, inherited, owned by the user [observed: A1, A2]; the sidecar's protected DACL naming the user's SID (§10) does not depend on it.

Unmeasured, so no claim covers them:

* any interactive desktop session: whether SmartScreen, or anything else, checks or prompts when a desktop user's app starts a marked sidecar, and the Explorer double-click (Z3), are [unknown];
* a restore from nuget.org (#52);
* Smart App Control in enforcement or evaluation mode: it was off on this laptop, so whether it blocks an unsigned sidecar is [unknown];
* the mark a real browser download and Explorer's Extract All write, which the harness imitated [inferred];
* win-arm64 on a desktop, other Windows editions and builds, and a clean install;
* the measured launches without the dry run's launches of the same bytes 7 to 8 minutes earlier;
* the SmartScreen debug channel, which stayed disabled, and the Security log, which needs an administrator;
* which tools, transfers or user actions put Mark-of-the-Web on a consumer's sidecar, beyond the paths measured here.

## Redaction review

`redact.ps1` replaced the account's profile path, its SID, the machine name and the user name in the text records (941, 21, 186 and 191 times) and copied them to `wmn-redacted/`, 171 files; it never copies `.evtx` exports or anything under `private\`.
Every one of those files was then read line by line before this record was written; repeated NuGet restore lines were checked against a pattern that admits only package names, versions, content hashes and nuget.org or loopback URLs, and every line matched.
The review found no user name other than the placeholders and the generic `wmn-test`, no Tailscale address, serial number, lender or owner name, and no event-log content: no event was in any window, and the `.evtx` exports stay private.
Dropped or changed here:

* the SDDL of A1 and A2 names the machine's own domain SID in its group (`G:S-1-5-21-…-513`), which `redact.ps1` does not replace, because it replaces only the user's SID; it is written `<MACHINE-SID>-513` above;
* the host record's vendor and model line is left out, keeping only the machine class;
* the host record's `statement` line, which names the laptop's SSH alias, is not quoted;
* the private remote logs, which were never redacted and name the machine and the accounts sshd admits, are cited by file and paraphrased, never quoted.

## Corrections to the raw record

The measurer's `RESULTS.md` stays as recorded (hash below), and this record wins where they differ.

* "no event in any of the four channels in any launch window": true as logged, but the SmartScreen debug channel was disabled, so only the other three channels' silence is an observation.
* "the redacted files are not yet read by a person": they now are, and one machine-specific SID was found that the redaction missed (above).
* The archive's `scripts/admin-run-as.ps1` (`3e87aaf5…`) is the copy staged at copy-in; every step from arrival on ran the Mac-side script (`8e3c5931…`), which `remote.sh` pipes over SSH and which differs only by reporting a task that has not started within 90 s [observed: `remote.sh`, both files; that no step ran the older copy is inferred from the Mac-side file's modification time, 17:19:09, before arrival at 17:19:20].

## Evidence provenance

The raw records stay private: they name the machine, the accounts and their SIDs, the `.evtx` exports cover whole time windows of a daily-use laptop, and the remote logs name the accounts sshd admits.
They are kept outside git in `~/Library/Application Support/Whatsmeow.NET/evidence/` on the measuring Mac (directory 0700, files 0600).
Left on the laptop: the `wmn-test` account and its profile, with the run directory, until the owner decides, and the 4625 events of the refused task logons in its Security log (three were listed; no later count was taken); removed: the staging directory, every `wmn45-*` task, and the `Performance Log Users` membership [observed: the private remote logs `18`, `19`, `20`].

| Private artifact | SHA-256 |
| --- | --- |
| `wmn45-win-measured.zip`, 261,885 bytes: the laptop's `scripts`, `logs`, `cases` and `wmn-redacted`, 449 files; hashed on the laptop and on the Mac | `54533c5e8326f85fe50257c6bb479296c04d40ef0d603a04581fec659676ed0f` |
| `wmn45-win-mac-side-2026-11-10.tar.gz`, the Mac-side harness, runbook, remote logs and feed | `270eb034d246646b5bee51fa2b133064ef8d3e7119f5ee5237fa6f4b6203d821` |
| `cases/measured/`, its 123 files | `a3c1f13113bd7fadcf28b746a89b6adb68eac57991e37e827d11ffb2b2145767` |
| `wmn-redacted/`, its 171 files, the reviewed copy | `a3120837c34a8a55b70051801a024e5fc399e65e9f45d2acefac106f3ad71e42` |

The `cases/measured/` digest is `find cases/measured -type f | LC_ALL=C sort | xargs -I{} shasum -a 256 "{}" | shasum -a 256`, run in the extracted archive; the `wmn-redacted/` digest is the macOS record's `printf` form of the same list, run inside it.

| Artifact | SHA-256 |
| --- | --- |
| `RESULTS.md`, the measurer's results | `8fd5c96e9333f6aba0d94388120a52653c7693def6523785775b19107b2bb318` |
| `RUNBOOK-SSH.md`, the run order | `5409b9d561ea203a32620827e4a4a2fa2f1f8e53aa5ccc2e027e3c14557f5163` |
| `README.md`, the harness reference | `d656f8c17b7d4bde68b06146a8baff1ea1df2111ba76c9eebf0ec34f0b457161` |
| `mac/remote.sh` | `8b40684abf6631f00ef5b6833f427bd844ee5e123e83f227a6e20dbd100063d2` |
| `scripts/admin-run-as.ps1`, Mac-side | `8e3c5931c9e4d2151193eb42440be9ed6b3bcbba8d34a01a47a4262f8a7a0e36` |
| `scripts/admin-batch-group.ps1` | `4bb4558ddf995514345034a044e7b482043e8fe1d4dfd9084a122948fec28836` |
| `scripts/arrive.ps1` | `d5e30c34f3dd041e1494eed2c3f770e95d82635ab44aab4f045d3eb76fbded08` |
| `scripts/setup.ps1` | `04ad0ebf5569d4b13bad4fc83ba82dc94b5a51f3efc5707e98ca67066100664c` |
| `scripts/run-cases.ps1` | `7e2772ba41dfa78e953b5660e2a5ebd0d1554133b447d0d4747bb43ff4d20b64` |
| `scripts/case.ps1` | `b346db01b140ba4274faf3439ec95ae935b9ca944141d1805c7cc38dbcb426a2` |
| `scripts/meta.ps1` | `59b1516f921c916c8498b59460e3807f5433312f8e72a5f3d6013c737e987291` |
| `scripts/host.ps1` | `bec0726aadcc2d0c3bf4fa828fddb9e050a08f893a32395c64eb0651235709f1` |
| `scripts/make-feed.ps1` | `671910ebcc9a38d7b3afbaa62c344ea26c5e951e63ce285929f9d39f83c1f15d` |
| `scripts/redact.ps1` | `0da0c3aa68d5dc841d2bd39afbe233fe410e1ef1e004400569ba4dc3fff544c7` |
| `scripts/env.ps1` | `e801a68bf685cb1dea9acae533f4824edd88c44b75400e5f20b1f622a70b76ae` |
| `scripts/sdk.ps1` | `e1ad92ab110aef460cc1ebb91c86346265ecc86c18ce361c971540526237c2db` |
| `scripts/Program.cs`, `build/package-smoke/Program.cs` at `210097c` | `82432ad4b9875280c6862576880d921f9fbfe8769d266814bf38f06e9d9442b3` |
| `scripts/nuget.config` | `a296d23a37c4bfb72146d5dc38a16962977d7f165e8f252259bf02b65de49f30` |
| `scripts/global.json`, the consumer's SDK pin | `974a1476882b8cf92fd9e2226347dc467b70c24d1016a9122fa6dd04c983fde5` |
| `AclProbe/Program.cs` | `7028fd7f95a664e04410c2dbfad70082546db64fa64a3f10cfeec17706568d87` |
| `Serve/Program.cs`, the loopback feed | `cf39c2629d48b46c3adfbdb625f763d99afa105314998449163bd6a50f3b58fe` |
| `logs/arrive.txt`, M1 | `5a9b09590c34e799dfc0b0339c65d4eebec625be7a836dce6fcd369b85cd9e4d` |
| `logs/measured/setup.log`, restore, build, publish, M1 to M10 | `25bd27a42a6069fb81ac80413004f463c0b0d47448e79c2e4d4930fa2d3601d0` |
| `logs/measured/host-before.txt` | `24d61d76237b873dc72a30989f95ef1bc620b06b4f65b81f1e524c6fdf449eb6` |
| `logs/measured/host-after.txt` | `955f874e0eeea02f533908af5063c8de7afc2ee2c08ffa57810597bfb7a92dac` |
| `logs/measured/http-server.log`, the loopback feed's requests | `aed890ac437f1c534ad2bdd62ca818eba0c41013ec6536a6cb722098992fc9d8` |
| `cases/measured/run-cases.log` | `08b995a602ddd7ef5b3b32e545567c67f1c0b0f9a5da7bdeefa0c8aa45adbc55` |
| `cases/measured/A1.txt` | `67c5625398a5d44464324edf05a79053c305106b45a3089750b83077a7db55ac` |
| `cases/measured/Z3.txt` | `8ec14a546dc596eebe6fce7c690ebd06a072aca82f38057a67d5397f58aa1b31` |

To repeat the measurement: download the run's `native-packages` artifact, then follow `RUNBOOK-SSH.md`: stage, `arrive.ps1`, `sdk.ps1`, then `host.ps1`, `setup.ps1` and `run-cases.ps1` under a new label, each as the standard account; the scripts expect the run directory under that account's profile.
A desktop repeat should run Z3 and the Z cases from the signed-in desktop of a standard account, with the SmartScreen debug channel enabled by an administrator first.
