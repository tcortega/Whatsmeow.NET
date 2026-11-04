# Security

## The database holds the account's keys

The sidecar's SQLite database, `whatsmeow-net.db` by default, holds each session's long-term private keys, and anyone with a copy can read and send that account's messages (§10).

* The sidecar creates it `0600`, readable by its owner only, before SQLite opens it; its `-wal` file inherits that mode, and the lock file next to it is `0600` too (§10). On Windows, Go's file modes set no ACL, so the file has its directory's [inferred: Go's `os.Chmod` documentation].
* Keep it on an encrypted disk.
* Its temporary files, media on the way to and from WhatsApp, are in `<database>.tmp/`, `0700` and emptied at each start (§7.6).

## One database, one sidecar

Two sidecars must never use one database at the same time.
The sidecar holds an OS lock on `<database>.lock` while it runs, so a second one fails before it is ready, with "in use by another whatsmeow-net"; the OS releases the lock when the process dies, so a crash never blocks a restart [observed: ADR 0020].
Whether that lock holds on a network filesystem is [unknown]: keep the database on a local disk.

The lock cannot stop a copy of the keys running somewhere else, such as a restored backup on a second machine.
Two live copies of one device make WhatsApp replace one with the other (`Replaced`) and can break the encryption sessions between them [inferred: RESEARCH, "At-least-once delivery"].

## Backups

A backup holds the same keys, so encrypt it yourself (§10).

* With the sidecar stopped, copy the database together with its `-wal` and `-shm` files as they are. That is how this project's live tests take theirs, holding `<database>.lock` meanwhile so no sidecar can start [observed: `StoreBackupTests`].
* With the sidecar running, SQLite's `VACUUM INTO 'backup.db'`, for example from the `sqlite3` shell, writes a consistent copy [inferred: SQLite's documentation; not tested here].

Restore a backup only when the database it came from is gone for good, never next to it.
A stale backup has old encryption state, and restoring one can cause a burst of `UndecryptableMessageReceived` [inferred: RESEARCH, "At-least-once delivery"].

## The socket and the token

* The client creates a `0700` directory with a random name for the sidecar's socket, and on Windows the sidecar gives it an ACL for the current user only (§10).
* Each start gets a new random 32-byte token, passed on stdin, never in an argument or an environment variable, and checked on every call; a nonce makes sure the client talks to the sidecar it started.
* The named pipe fallback on Windows has a random name and only the current user can open it.
* In [External mode](external-mode.md) the token comes from a file, and TCP carries it in clear text: use TCP only on a private network.

## Media paths

The sidecar opens media paths only under `MediaRoot`, and refuses `..` and symlinks that leave it, without a race between the check and the use (§10).
Files it downloads are created `0600`.
Errors and logs never carry a media URL's query, which holds WhatsApp's media credentials (§7.6).

## Personal data in logs

* At info level and above, the sidecar replaces every phone number and JID user part in its log lines with a token, and the client's own lines name no JID; only `Debug` or below can show them (§8.8, [Errors and telemetry](errors-and-telemetry.md#logs)).
* A pairing QR code carries the new device's secret: draw it, never log its text.
* Session ids appear as given: do not use phone numbers as ids.
* A group's JID can carry its creator's phone number, so the library treats it as personal data too (§7.8).
