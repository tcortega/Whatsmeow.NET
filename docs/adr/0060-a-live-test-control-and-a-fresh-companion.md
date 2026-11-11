# 0060. A live test control for group photos, and a fresh companion on opt-in

* Status: Accepted
* Date: 2026-11-06
* Evidence: #36 AC6 and its exit audit (the live contacts suite does not cover the Contacts FullCycle), whatsmeow at `307a2edf117d` and the sidecar (lines below)
* Amends: [ADR 0019](0019-fake-whatsapp-mode.md) (a second test-only control, and the groups slice's `livetest_*` files), [ADR 0032](0032-live-test-seams-and-the-peer-session.md) (the `livetest` tag in the groups slice too, a control service, and a throwaway companion again, on opt-in only), docs/guidelines/tests.md (Live WhatsApp)
* Amended by: [ADR 0061](0061-a-push-name-first-sight-on-a-persistent-store.md) (the push name branch may pass on a persistent store whose precondition read holds, on opt-in, with no claim about a fresh device; its Addendum has the push name step send a named unavailable presence before its text); [ADR 0062](0062-the-contacts-fullcycle-on-the-persistent-sessions.md) (the Contacts FullCycle on the persistent sessions, the saved name from the owner on opt-in); [ADR 0063](0063-the-about-branch-on-the-persistent-sessions-on-consent.md) (a second control service, `LiveContactService`, and the `livetest` tag in the contacts slice); [ADR 0070](0070-group-photos.md) (the group photo is set and removed through the contract, so `LiveTestService`, the groups slice's `livetest_*` files and the `livetest` tag there go)

## Context

#36 AC6 asks each Phase 2 slice's FullCycle to pass live with the fake-mode assertions.
The live contacts tests cover registration, LID queries and the blocklist, not the Contacts FullCycle's `ContactUpdated` changes [observed: `LiveContactsTests.cs`, the #36 exit audit].
Each of those changes fires only when WhatsApp reports something new, and the owner's two accounts already know each other:

* A push name and a verified business name dispatch only when the stored value changes; a store with no row counts as changed (user.go:679-737; store/sqlstore/store.go:617-657) [observed: source].
History sync stores push names, never business names, and dispatches nothing (message.go:799-818, user.go:659-677) [observed: source].
So a companion paired for the run, on a store of its own, has no business name cached for the business account, and no push name until a message or a `PUSH_NAME` history blob brings one [observed: source].
The test checks the rows before each stimulus and fails when the value is cached already: a history blob that comes first fails the push name branch honestly [inferred: blob order is unknown].
The persistent stores have both values cached, and dispatch again only when the name really changes, which the run does not do.
* A picture change arrives as a `picture` notification for any JID, a group included (notification.go:93-117, 510-511; types/events/events.go:530) [observed: source].
`SetGroupPhoto` sets a group's photo and returns its id, or removes it given no image (group.go:325-354) [observed: source].
Changing the run's own temporary group touches no account's profile; the live tests already rename it and change its settings.
The contract has no group photo call: it is excluded (ADR 0030, `sidecar/parity.excluded`).
* About and the saved name are not reachable here: About needs an account-wide profile change, which is not authorized, and the saved name's expected value needs a fact from the owner that the code under test cannot supply. Both branches stay open, stated by the test.

ADR 0032's seams are constants: nothing lets a live test ask its sidecar to do something the contract does not offer.
ADR 0032 also gave up a throwaway companion paired by QR code each run, because it needed a person every run.

## Decision

* **A live test control, `whatsmeow.livetest.v1.LiveTestService`**, in `proto/whatsmeow/livetest/v1/livetest.proto`, generated to Go in `sidecar/gen` and to C# in `Whatsmeow.LiveTests` only (never a shipped assembly), as `whatsmeow.fake.v1` is (ADR 0019).
It has one RPC, `SetGroupPhoto`: a session sets the photo of a group from a JPEG at a path on the sidecar's machine (§13 invariant 8), or removes it given no path, and gets the picture id back.
* **Served only by a livetest build.** `sidecar/internal/groups/livetest_on.go` registers the service on the sidecar's server, the same socket and token; its `livetest_off.go` twin registers nothing, so a release build answers `UNIMPLEMENTED`, as for any unknown service. `cmd/whatsmeow-net` calls the groups slice's register function in every build.
`tools/check-repo` allows the `livetest` tag in `sidecar/internal/groups/` too.
`SetGroupPhoto` stays excluded from the contract in `sidecar/parity.excluded`, since the report reads the release build.
* **A fresh companion, on opt-in only.** One live test pairs a new companion of the peer's account by QR code, on a new database it creates itself (one exclusive create, 0600, in a 0700 directory that is no link), in a sidecar of its own; it runs only with `WHATSMEOW_LIVE_FRESH_DATABASE`, `WHATSMEOW_LIVE_HISTORY_BOUNDS` and `WHATSMEOW_LIVE_KNOWN_DEVICES` set, so no ordinary live run asks for a scan.
* **Its proof.** After `Paired` it checks the session is the only one of that database, its account is the peer's and not the live one, and its device is on neither the live nor the peer database nor in `WHATSMEOW_LIVE_KNOWN_DEVICES`.
That list is root's preflight: the peer account's devices in its other stores, read from a consistent snapshot without opening them in a sidecar; a list that is empty, repeated or not the peer's devices is refused before the scan, so no device is linked for it. WhatsApp assigns the device in its pair-success (pair.go:139-140); nothing in the source shows it never reissues one still linked, so the list is checked, not assumed [inferred].
A failed or unfinished proof stops there: nothing is logged out, the device may still be linked, the database is kept for a later logout from it.
* **Its cleanup**, on its own five-minute bound, whatever the test's token did: from Disconnected it connects, from Connecting or Reconnecting it waits for a usable connection, then it logs out, and deletes the session only after it reads LoggedOut with code 0 following that logout [observed: the sidecar records it only once whatsmeow's unlink and store delete returned, session.go:999-1003, client.go:756-781].
A session that was LoggedOut before (any code) was logged out by WhatsApp or the phone (client.go:749-750) and is reported, never as this cleanup's logout, and kept; any error, other state or the bound keeps the session and its keys; a failure after this cleanup's logout succeeded (the LoggedOut check, the delete or the read after it) is reported as such: the logout succeeded, and the session's deletion is not confirmed.
* **Its order.** The business name and push name steps run as soon as the session is Connected, before it waits for CaughtUp: the sidecar's history worker starts only at OfflineSyncCompleted and downloads only while online (history.go:221-222, 403-424), and a `PUSH_NAME` blob's push names are stored at its download.
The history is also held from its first event until those steps ended; that stops recording at the window of 4 unacked events, and does not stop a download (history.go:583, 660, 746, 773-785), so a cached push name stays possible and fails its branch honestly. Each step's failure is kept and the next runs; no step runs after the capture's 30-minute bound from `Paired`.
* **The shared code and its tests.** The proof, the steps, the hold, the bound and the cleanup are `IntegrationTests/Support/FreshCompanion.cs`, which the live test calls and `FreshCompanionTests` checks on fake-mode sidecars of its own, since a new database is the behaviour under test. #42's record is `IntegrationTests/Support/LiveHistoryBounds.cs` with its tests beside it.
`Whatsmeow.Client` gives its internals to `Whatsmeow.LiveTests`, as to the integration tests: the control's channel and its error mapping (`RpcErrors`).
* **The kept database** holds the device's keys while it is linked, and the contacts, push names, history notifications and outbox rows the session saw; SQLite's free pages and WAL may keep deleted rows [inferred]. It is private and deleted by hand; nothing deletes it automatically.

## Consequences

* A run with the opt-in needs one QR scan by the peer account's phone; every later run with it needs another, since the first sight is spent.
* A release binary and the packages contain no seam and no control: `build/ci.sh` checks that the release build links neither a `livetest_on.go` nor the control's generated package, and that no `dotnet/src` assembly contains `Whatsmeow.LiveTests.Control`, each with the tagged build or the live tests' assembly as its positive control.
* The photo is removed before the group is closed. Whether both phones show the change and its removal, and whether a member's device gets the `picture` notification at all, is [unknown] until the first run with the opt-in.
* The test checks push name, business name and picture with the fake-mode checks, and records About and the saved name as not run; it is not a FullCycle pass, so #36 AC6 stays open for Contacts.
