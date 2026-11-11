# External mode

By default your app starts `whatsmeow-net` as its child process (embedded mode).
In External mode something else runs the sidecar, such as a second container in the same pod, and the client only connects to it ([ADR 0046](../adr/0046-external-mode.md)).
Use it when your platform manages processes, or when you do not want to ship the sidecar binary inside your app ([Licensing](licensing.md)).

## The sidecar

```sh
whatsmeow-net --database /data/whatsmeow-net.db --listen tcp:0.0.0.0:7777 --token-file /run/secrets/whatsmeow-token
```

* `--listen` is `unix:<absolute path>` or `tcp:<host>:<port>`.
  On Windows the path needs a drive or a UNC root, such as `unix:C:\run\whatsmeow-net.sock`; `unix:\run\s`, with no drive, is refused.
* `--token-file` holds the shared secret: 32 bytes as 64 hex digits, on one line.
* Optional: `--media-root`, `--send-burst`, `--send-interval`, and the history flags `--history-full-sync`, `--history-days-limit`, `--history-size-limit-mb` and `--history-storage-quota-mb`, the same settings as the embedded options.
* A proxy for WhatsApp traffic goes in the sidecar's environment ([Deployment](deployment.md#proxy)).

It runs until SIGTERM, SIGINT, or a `Shutdown` call from any client holding the token; nothing ties it to a client's life.
A Unix socket is created `0660`, so run both sides as the same user or with a shared group, such as a Kubernetes `fsGroup` [inferred: not measured in a pod].
Of two sidecars started on the same socket path only one binds; any other file at the path refuses the start.

## The client

```csharp
var options = new WhatsmeowSidecarOptions
{
	External = new ExternalSidecarOptions { Address = "tcp:whatsmeow:7777", TokenPath = "/run/secrets/whatsmeow-token" },
};
```

Or in configuration, `Whatsmeow:External:Address` and `Whatsmeow:External:TokenPath`.

* The client waits up to `StartupTimeout` for the sidecar to answer, then watches it, and reconnects with the restart backoff when the connection drops, connecting its sessions again as after a crash.
* It checks only the protocol version, so the sidecar can be a different release of the same protocol.
  It accepts protocols 1 and 2: a protocol 1 sidecar lacks the group photo calls, which then throw `WhatsmeowException` with `ErrorCode` `Unknown` [observed: ADR 0070, a run against a protocol 1 sidecar].
* Disposing the client stops reading events and acks them, but never stops the sidecar.
* `SidecarPath`, `DatabasePath`, `Transport`, `History` and `SendPacing` fail validation here: they configure a sidecar the client starts, and this one has its flags.
* `MediaRoot` is your process's path to the sidecar's `--media-root`, and must be the same absolute path on both sides, such as a volume mounted at the same place.
* `Consumer` names the client's place in each session's event stream, `client` by default.
  Several apps sharing one sidecar each need their own; a name nobody uses any more keeps its events in the database ([Delivery](delivery.md#acks-and-stopping)).

## The token

The token never goes in an argument or an environment variable; both sides read it from a file, such as a mounted secret.
To rotate it, rewrite the file on both sides: when a client presents the new token, the sidecar reads its file again and switches, and from then on refuses the old one.
Rewrite each file in one step: write the new token to a file beside it, in the same directory, then rename that file over the old one, which replaces it at once on the same volume: a reader in a loop saw no partial token across 500 renames [observed: #174, on Linux, macOS and Windows].
A file rewritten in place may be read half written [inferred: an in-place write is not atomic]; the client then treats it as unreadable and tries again with the restart backoff [observed: `ExternalTokenTests`, `SidecarSupervisorTests`].
A .NET writer that copies over the file with `File.Copy` can throw `IOException` while the client reads it, because .NET locks files on Linux and macOS [observed: #132, on both].
On Linux and macOS the rename lands while the client or the sidecar reads the file [observed: `ExternalTokenTests`; #174 for the sidecar's read].
On Windows a rename over the file fails while either side has it open, the client at each connect or the sidecar at its start and after a refused token, each only for the read.
`File.Move(next, path, overwrite: true)` fails with access denied (`UnauthorizedAccessException`, error 5) while either side does, and Go's `os.Rename` likewise while the client does [observed: `ExternalTokenTests` on win-x64, run 42865441309; #174 for the rest, Windows 11 on NTFS].
A rename with `FILE_RENAME_FLAG_POSIX_SEMANTICS` lands while the client reads, but fails with a sharing violation (error 32) while the sidecar does [observed: #174].
So on Windows retry the rename on error 5 or 32 only, about every 10 ms, for a few seconds, then fail loudly: against a client reading in a loop, 500 renames retried every 10 ms all landed, the slowest after 9 failures and 142 ms [observed: #174].
Waiting is safe: the file keeps the old token whole while the rename fails [observed: `ExternalTokenTests`], and the sidecar keeps accepting the old token until a client presents the new one [inferred: ADR 0046].
A client the sidecar refuses reads its own file again on its next connect.

## Shipping no binary

`Whatsmeow.Client` brings the sidecar binaries in `Whatsmeow.Sidecar.Native`.
An app that runs only in External mode can leave them out with a `PackageReference` to `Whatsmeow.Sidecar.Native` at exactly its `Whatsmeow.Client` version, with `ExcludeAssets="all"`; it then builds and runs with no `whatsmeow-net` in its output [observed: `build/ci.sh install`, ADR 0046].
Up to `1.0.0-rc.1` the binaries came in three packages, `Whatsmeow.Sidecar.Native.Linux`, `.MacOS` and `.Windows`; since then they are one, `Whatsmeow.Sidecar.Native` ([ADR 0069](../adr/0069-one-native-package.md)).
An app that excluded the three moves its exclusion to `Whatsmeow.Sidecar.Native` when it upgrades: an exclusion on the old ids does not reach the new package, so its binaries would land in the app's output again [inferred: NuGet applies `ExcludeAssets` to the package it names].

## TCP is for a private network only

Over TCP the token travels in clear text, and there is no TLS.
Use TCP only on a private network, such as between the containers of one pod; anything else should use a Unix socket on a shared volume (§10).
A gRPC health probe needs the token, so probe the TCP port instead.

A client pings a connection silent for 15 s and drops it when no answer comes in 15 s more, so a sidecar that vanished fails the calls on it instead of hanging them.
