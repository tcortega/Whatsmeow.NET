# 0060. A live test control for group photos, and a fresh companion on opt-in

* Status: Accepted
* Date: 2026-11-06
* Evidence: #36 AC6 and its exit audit (the live contacts suite does not cover the Contacts FullCycle), whatsmeow at `307a2edf117d` and the sidecar (lines below)
* Amends: [ADR 0032](0032-live-test-seams-and-the-peer-session.md) (the `livetest` tag in the groups slice too, a control service, and a throwaway companion again, on opt-in only), docs/guidelines/tests.md (Live WhatsApp)

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
`tools/check-repo` allows the `livetest` tag in `sidecar/internal/groups/` too; `build/ci.sh` keeps failing when the release build links any `livetest_on.go`.
`SetGroupPhoto` stays excluded from the contract in `sidecar/parity.excluded`, since the report reads the release build.
* **A fresh companion, on opt-in only.** One live test pairs a new companion of the peer's account by QR code, on a new database it creates itself, in a sidecar of its own; it runs only when `WHATSMEOW_LIVE_FRESH_DATABASE` names that new file, so no ordinary live run asks for a scan.
It logs out and deletes its session only once it proved the account is the peer's, the device is new, and the database and session are the run's own; otherwise it stops its sidecar and keeps the database, and fails.
The same pairing serves #42's history bounds record, written to a file of its own that the test creates.

## Consequences

* A run with the opt-in needs one QR scan by the peer account's phone; every later run with it needs another, since the first sight is spent.
* A release binary and the packages still contain no seam and no control; CI proves it as before.
* The photo is removed before the group is closed. Whether both phones show the change and its removal, and whether a member's device gets the `picture` notification at all, is [unknown] until the first run with the opt-in.
* The test checks push name, business name and picture with the fake-mode checks, and records About and the saved name as not run; it is not a FullCycle pass, so #36 AC6 stays open for Contacts.
