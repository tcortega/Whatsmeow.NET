# Guides

How to use Whatsmeow.NET, and what it promises.
The [README](../../README.md) has the install and the quick start.

* [Sessions and pairing](sessions-and-pairing.md): sessions, their states, pairing by QR code or phone number, reconnecting.
* [Delivery](delivery.md): which events are at least once and which are best effort, idempotent handlers, ordering, durability.
* [Messaging](messaging.md): text, replies, mentions, reactions, edits, revokes, polls, disappearing timers, receipts, pacing.
* [Media](media.md): upload, download, the media kinds, voice notes, expired media.
* [Groups](groups.md): create, members, settings, invite links, group events.
* [Chats and contacts](chats-and-contacts.md): presence and typing, mute, pin, archive and the rest, contacts, the blocklist, LID and phone numbers.
* [History](history.md): history sync after pairing and on demand.
* [Hosting and testing](hosting.md): `AddWhatsmeow()`, configuration, keyed sessions, DI handlers, `Whatsmeow.Testing`.
* [Errors and telemetry](errors-and-telemetry.md): every exception and its code, cancellation, logs, traces and metrics.
* [External mode](external-mode.md): a sidecar your orchestrator runs, in another container or process.
* [Deployment](deployment.md): platforms, Native AOT, single file, containers, macOS and Windows, capacity, troubleshooting.
* [Security](security.md): the database's keys, backups, the socket and token, personal data in logs.
* [Licensing](licensing.md): MPL-2.0 and GPL-3.0-only, and what redistributing the sidecar owes.

## How sure each claim is

Every promise here is something the code or a test enforces, unless it carries a tag:

* `[observed: where]`: measured, and where; mostly against real WhatsApp or whatsmeow's source at the pinned version.
* `[inferred]`: what we expect from the evidence we have, not measured.
* `[unknown]`: nobody has measured it yet.

A § number points to a section of [ARCHITECTURE.md](../ARCHITECTURE.md), which has the evidence in full.
Treat `[inferred]` and `[unknown]` as open: design so that your app still works if they turn out wrong.
