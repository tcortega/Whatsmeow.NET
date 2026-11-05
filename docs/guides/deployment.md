# Deployment

## Platforms

`Whatsmeow.Client` brings the sidecar for six targets: linux-x64, linux-arm64, osx-x64, osx-arm64, win-x64 and win-arm64.
The Linux binary has no C dependencies, so it runs on glibc and on musl (Alpine) alike [observed: §8.5].
CI installs the packages into a new app and runs it on linux-x64, Alpine x64, osx-arm64 and win-x64, built and published several ways [observed: `build/ci.sh install`].
It also publishes a Native AOT app from the packages on every target, Alpine x64 and arm64 included, each on a machine of that OS and architecture, and runs it with the packaged binary [observed: `build/ci.sh aot`, ADR 0056].
Other targets, win-x86 included, have no binary, and the start fails naming the paths it tried.

## Where the binary comes from

The client looks for `whatsmeow-net` at `WhatsmeowSidecarOptions.SidecarPath`, then `WHATSMEOW_NET_SIDECAR_PATH`, then next to your app, then under `runtimes/<rid>/native/` (§8.5).

* A portable build or `dotnet run` copies all six binaries into its output, about 148 MB [observed: ADR 0023].
* A RID-specific, self-contained, single-file or Native AOT publish puts only its own binary next to the app.
* A single-file publish never bundles it, so it stays a file beside the app; with `IncludeAllContentForSelfExtract`, set `SidecarPath` to it.

`SidecarPath` must be the binary itself, not a script that starts it: on Linux and macOS the sidecar stops when your process is no longer its parent.
Your app's version and the sidecar's must match exactly, which the packages ensure; a mismatch fails the start with `SidecarVersionMismatchException`.

## Native AOT and trimming

Every package is AOT and trimming compatible, with zero warnings: CI publishes a Native AOT app that roots all of them, from the packages, on every target, and fails on any warning (§8.9, ADR 0056).
With `Whatsmeow.Extensions.Hosting`, the options bind through the configuration binding source generator, not reflection.
The experimental `Raw` members work under AOT too, but their types change with whatsmeow ([Messaging](messaging.md#raw-messages)).

## Containers

* NuGet gives the binary an execute bit for its owner only, so an image whose files belong to root and that runs as another user cannot run it in place: the client copies it to `LocalApplicationData/whatsmeow-net/<version>/` and runs the copy [observed: ADR 0023], and does so on every start [inferred].
  Set the mode in the image instead, for example `RUN chmod 755 /app/whatsmeow-net`.
* On a `noexec` mount the client takes the same copy route.
* The sidecar lives and dies with your process.
  To run it as its own container, use [External mode](external-mode.md).
* Keep the database on a persistent volume, local to the node ([Security](security.md)).

## macOS and Windows

The binaries are not notarized or Authenticode signed (§12).
What was measured is another nuget.org binary: the `protoc` that `dotnet restore` pulled from Grpc.Tools had no quarantine attribute on macOS and no Mark of the Web on Windows, and it and a locally built, unsigned sidecar ran [observed: §14 questions 2 and 6].
That `whatsmeow-net` restored from nuget.org is the same is [inferred], and so is that Gatekeeper and SmartScreen leave it alone because of that.
An app you ship as a notarized macOS `.app` signs its embedded binaries in its own release, the sidecar included [inferred: standard macOS app signing, not measured yet].
On Windows a Job Object kills the sidecar when your process dies (§8.5).

## Capacity

One sidecar holds many sessions in one database.
The EchoBot sample exercises two sessions against the fake WhatsApp [observed: `Samples/EchoBotTests.cs`].
Startup was measured separately with 1, 2, 10, 100 and 1,000 stored, paired synthetic sessions on linux-x64, osx-arm64 and win-x64 (§14, #126).
The release sidecar's median launch through validated ready rose from 11 to 319 ms on Linux, 47 to 446 ms on macOS and 97 to 781 ms on Windows between 1 and 1,000 sessions [observed: [raw measurements](https://github.com/tcortega/Whatsmeow.NET/issues/126#issuecomment-6665964974), five launches per count].
At 1,000, the separate public `StartAsync` measurement, including its first `GetInfo`, had maxima of 334, 512 and 829 ms respectively (rounded) [observed: the same raw measurements].
This covers restoring paired synthetic stores and constructing real clients, with no WhatsApp connection, sync or message traffic; it does not establish a supported connected-session count (§14).
How many sessions one process handles against real WhatsApp, and the memory each takes, are [unknown].
History holds at most one blob in memory at a time while it reads it ([History](history.md)).

## Troubleshooting

**Windows: the start fails to connect to the sidecar's socket.**
The default transport on Windows is a Unix domain socket too ([ADR 0014](../adr/0014-af-unix-default-on-windows.md)).
Third-party network stacks on managed machines have been seen to break AF_UNIX `connect` for other runtimes; whether they break this library is [unknown].
If it happens, switch to the named pipe fallback, which passed the same soak tests (§14 question 2):

```csharp
var options = new WhatsmeowSidecarOptions { Transport = SidecarTransport.NamedPipe };
```

or `"Whatsmeow": { "Transport": "NamedPipe" }` in configuration.

**The binary is not found.**
The exception lists the paths it tried.
Check that your RID is one of the six, or set `SidecarPath`.

**The database is in use.**
Another `whatsmeow-net` holds it: one database has one sidecar at a time, and a second one fails before it is ready.

**The start times out.**
`StartupTimeout` defaults to 30 seconds and covers the ready line and first `GetInfo` [observed: `WhatsmeowSidecarOptions.StartupTimeout`, `WhatsmeowSidecar.LaunchAsync`].
That was over 36× the slowest public start with 1,000 paired synthetic sessions on the three measured runners (§14, #126); the same margin on your database and host is [unknown].
Inspect the exception's stderr tail and measure startup on your deployment before raising the timeout.
A crash restart's reconnect to WhatsApp is a separate phase after `GetInfo`, outside this timeout [observed: `WhatsmeowSidecar.LaunchAsync`; §14 restarts on Windows].
