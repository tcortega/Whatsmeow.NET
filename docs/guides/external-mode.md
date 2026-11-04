# External mode

By default your app starts `whatsmeow-net` as its child process (embedded mode).
In External mode something else runs the sidecar, such as a second container in the same pod, and the client only connects to it ([ADR 0046](../adr/0046-external-mode.md)).
Use it when your platform manages processes, or when you do not want to ship the sidecar binary inside your app ([Licensing](licensing.md)).

## The sidecar

```sh
whatsmeow-net --database /data/whatsmeow-net.db --listen tcp:0.0.0.0:7777 --token-file /run/secrets/whatsmeow-token
```

* `--listen` is `unix:<absolute path>` or `tcp:<host>:<port>`.
* `--token-file` holds the shared secret: 32 bytes as 64 hex digits, on one line.
* Optional: `--media-root`, `--send-burst`, `--send-interval`, and the history flags `--history-full-sync`, `--history-days-limit`, `--history-size-limit-mb` and `--history-storage-quota-mb`, the same settings as the embedded options.

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
* Disposing the client stops reading events and acks them, but never stops the sidecar.
* `SidecarPath`, `DatabasePath`, `Transport`, `History` and `SendPacing` fail validation here: they configure a sidecar the client starts, and this one has its flags.
* `MediaRoot` is your process's path to the sidecar's `--media-root`, and must be the same absolute path on both sides, such as a volume mounted at the same place.
* `Consumer` names the client's place in each session's event stream, `client` by default. Several apps sharing one sidecar each need their own; a name nobody uses any more keeps its events in the database ([Delivery](delivery.md#acks-and-stopping)).

## The token

The token never goes in an argument or an environment variable; both sides read it from a file, such as a mounted secret.
To rotate it, rewrite the file on both sides: when a client presents the new token, the sidecar reads its file again and switches, and from then on refuses the old one.
Until a client presents the new one, the old one is still accepted [inferred: ADR 0046].
A client the sidecar refuses reads its own file again on its next connect.

## TCP is for a private network only

Over TCP the token travels in clear text, and there is no TLS.
Use TCP only on a private network, such as between the containers of one pod; anything else should use a Unix socket on a shared volume (§10).
A gRPC health probe needs the token, so probe the TCP port instead.

A client pings a connection silent for 15 s and drops it when no answer comes in 15 s more, so a sidecar that vanished fails the calls on it instead of hanging them.
