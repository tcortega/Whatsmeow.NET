# 0020. Sessions and the store

- Status: Accepted
- Date: 2026-11-01
- Evidence: issue #6, its plan review, and the tests named below
- Supersedes: the `conversions:` line of [ADR 0010](0010-vogen-ids-hand-written-jid.md)

## Context

Issue #6 adds the first slice that talks to whatsmeow: the SQLite store, sessions, their state machine and `WatchSessionState`.
ARCHITECTURE §7.4 left the single-owner mechanism open ("a lock file or lease row"), and building the slice showed facts at the pin that the design had to follow:

- `ConnectContext` returns after the websocket handshake; WhatsApp's login answer comes later, as `Connected` dispatched from a goroutine, or as a `LoggedOut`, `TemporaryBan`, `ClientOutdated` or `ConnectFailure` [observed: whatsmeow at the pin, client.go:541-599, connectionevents.go:107-208].
- `Disconnect` and `Logout` raise no event [observed: client.go:695-703, 749-779].
- whatsmeow dispatches connection events on their own goroutines, so one can arrive after the state it belongs to: a `Connected` after the application disconnected [observed: client.go:552, 621].
- `ConnectContext`'s context scopes keepalive, the node queue and reconnects [observed: client.go:541-599, the #21 review].
- libsignal sets its process-wide logger lazily on first use, which is a data race between two goroutines that create devices at once, and its default logger prints to stdout [observed: libsignal v0.2.2 logger/Logger.go:34-38, logger/DefaultLogger.go:16-28; the race detector in `internal/session` tests].
- Vogen 8.0.7 with `Conversions.SystemTextJson` generates a public nested converter with nullable annotations off, and its converter factory even with `Conversions.None`; PublicApiAnalyzers fails both with RS0041 under `TreatWarningsAsErrors` [observed: a probe and the `Whatsmeow.Client` build].
- PermanentDisconnect sessions reconnected after a sidecar restart would fight the client that replaced them for one device [observed: plan review of #6].

## Decision

- **One owner per database: an OS file lock** on `<database>.lock` (`flock` on Unix, `LockFileEx` on Windows), taken before the database opens and held until the process ends.
  The OS releases it when the process dies, so a crashed sidecar never blocks its restart; a lease row would, until it expires.
  A second sidecar fails before its ready line with "the database ...: lock ...: in use by another whatsmeow-net".
- **The database file** is created 0600 before SQLite opens it; the `-wal` file inherits that mode [observed: `TestOpenCreatesTheDatabaseOwnerOnly`].
  The connection string uses the plain path, not a `file:` URI, so a Windows path needs no escaping; a path with `?` is refused.
- **`--database` is required**; the client always passes it. `WhatsmeowSidecarOptions.DatabasePath` defaults to `whatsmeow-net.db` in the current directory, made a full path in `WhatsmeowSidecarOptionsSetup.Apply`.
- **One transition function.** Every state change, whatsmeow's events and the sidecar's own, goes through `session.(*Service).transition`; #8 records the outbox event inside it.
  Connect is `Connecting` while `ConnectContext` runs and returns after it with the session still `Connecting`; on `CONNECTION_FAILED` the session goes back to its previous state.
  If recording `Connecting` fails, the session goes back to its previous state at once, so a later Connect is not a no-op.
  Every transition is recorded even when the call that caused it was cancelled meanwhile (`context.WithoutCancel`), so a cancelled Connect never loses a stopping state across a restart.
  Disconnect records `Disconnected`, Logout `LoggedOut{0}`.
  A Logout that fails with no connection left (whatsmeow disconnects only after WhatsApp unlinked the device, client.go:771-772) records `Disconnected`: the local store delete failed, and the session is not connected.
  Connect's `TEMPORARILY_BANNED` is a precondition while a ban with a known end lasts; `LOGGED_OUT` and `NOT_LOGGED_IN` are the session's state (session.proto).
- **Guards against late events.** `Connected` applies only from `Connecting` or `Reconnecting`, `OfflineSyncCompleted` only from `Connected`, `Disconnected` only from a live state; a PermanentDisconnect applies from any state but `LoggedOut`, and `LoggedOut` from any.
- **A restart keeps the stopping states.** `LoggedOut`, `Replaced`, `Outdated`, `TempBanned` and `Error` are stored with their fields and come back as they were; a session that was `Connecting`, `Connected`, `CaughtUp`, `Reconnecting` or `Disconnected` comes back `Disconnected`, and a never-paired one `Created`.
  A session whose device rows are gone at load (whatsmeow deletes them concurrently with a server-side logout) loads as `LoggedOut{0}` with a warning.
- **The .NET client reconnects** only the sessions the application connected (and did not disconnect, log out or delete since) that come back `Disconnected`, reading `ListSessions` after each relaunch. A stopped session is never reconnected on its own.
  Logout and Delete forget a session only once they succeeded; each reconnect checks again that the application did not disconnect the session meanwhile.
- **Delete** disconnects, then deletes the device rows by the JID the session recorded, never by the device's own fields, which whatsmeow writes on its own goroutine on a server-side logout (store.go:286-297).
- **Shutdown** ends the connections' context first, which ends a connect in flight, then disconnects each session.
- **A watch survives a restart**: when the sidecar cannot be reached, `WatchStateAsync` waits for the next launch and reopens, so the restarted sidecar's current state comes first.
- **The sidecar never connects a session on its own** and sets no `AutoReconnectHook`, following mautrix-whatsapp (ADR 0013).
- **Errors.** The reasons a slice can fail with are sentinel errors in `internal/server` (`server.ErrSessionNotFound`, ...), with `server.WithMetadata` for ErrorInfo metadata; core never imports a slice, and the reasons are shared.
  Tests may read a returned status (depguard allows `status` and `errdetails` in `_test.go` files); only `internal/server` builds one.
- **libsignal's logger** is set once at startup to the sidecar's zerolog (`internal/log.SetupSignal`), with the other startup-only whatsmeow globals.
- **Pairing stand-in.** The fake control `LinkDevice(session_id, jid)` does whatsmeow's pair-time store work, then sends `PairSuccess`; the slice records the device JID and the session becomes `Disconnected`. #7 reuses it.
  `FailNextLogout` makes the next unlink request time out, as whatsmeow reports it.
- **Vogen:** `conversions: Conversions.None`, `tryFromGeneration: GenerateBoolMethod`, `systemTextJsonConverterFactoryGeneration: Omit`. No converter, so no suppression; a hand-written converter comes when a consumer needs JSON of an id.
  Vogen still makes `SessionId` implement `IConvertible` explicitly, with a public `GetTypeCode()`; no `VogenDefaults` parameter turns that off [observed: Vogen 8.0.7 generated code].
- **`WhatsmeowException.ErrorCode`** is the public enum `WhatsmeowErrorCode`, mirroring `ErrorReason` through an explicit switch, `Unknown = 0` for no reason, a local failure or a reason the client does not know.

## Consequences

- `SessionId` has no JSON converter; ADR 0010's measured consumer-JSON path no longer applies until one is written.
- After a restart the application sees a stopped session as it was, and decides itself whether to connect it again.
- The libsignal stdout default and its lazy-init race are upstream issues to report [inferred: present in libsignal v0.2.2, not checked against its latest commit].
- The fake WhatsApp's `LinkDevice` is a pairing stand-in in fake mode only; it never ships (ADR 0019).
