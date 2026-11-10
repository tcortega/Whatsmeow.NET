# 0065. GetUserInfo's verified name through whatsmeow's own usync, until the pin carries tulir/whatsmeow#1334

* Status: Accepted
* Date: 2026-11-10
* Evidence: #153, #36 AC6, the owner's direction that #36 not wait on upstream timing and his decisions of 2026-11-10 (below), tulir/whatsmeow#1334, whatsmeow at `307a2edf117d` and `79dc2a8c33df` (lines below), the sidecar
* Amends: [ADR 0013](0013-whatsmeow-config-follows-mautrix-no-fork.md) (gaps are fixed upstream; this one is bridged locally until the fix is in the pin), [ADR 0062](0062-the-contacts-fullcycle-on-the-persistent-sessions.md) (its `contacts_pin_known_bad` refusal and "only once a main pin carries #153's fix"), `sidecar/parity.excluded` (`DangerousInternals`)

## Context

* At the pin, `GetUserInfo` sends one usync, `full` and `background`, asking `business/verified_name`, `status`, `picture`, `devices` and `lid`, with the privacy token (user.go:232-241) [observed: source].
  It parses the verified name (user.go:253), hands it to `updateBusinessName` (user.go:269-271), and stores the entry without it (user.go:272) [observed: source].
  So `GetUserInfoAsync`'s `VerifiedBusinessName` is null whatever WhatsApp sent, against the contract (#153) [observed: source, and two live runs, ARCHITECTURE §14 Contacts].
* `user.go` is byte-identical at `79dc2a8c33df`, the target of the bump in #169 [observed: `cmp` of the two module trees].
* The fix, the one missing assignment, is tulir/whatsmeow#1334; on 2026-11-10 it had no checks and no review, waiting on a maintainer's workflow approval for a first-time contributor [observed: `gh pr view 1334`].
  When it merges is [unknown], and the owner directed that #36 not wait on it.
* An isolated measurement build of the pin with that line answered the name for a query by phone number and by LID [observed: root's #153 measurement, 2026-11-08 03:38 UTC].
* `DangerousInternals()` is exported and marked `Deprecated: dangerous`; it exposes the unexported `usync` as `Usync` and `updateBusinessName` as `UpdateBusinessName` (internals.go:35-40, 830-832, 842-844), but not `parseVerifiedName` [observed: source].
  `parseVerifiedName` only unmarshals the certificate and its details, checking no signature (user.go:737-775) [observed: source].
* No other exported call gives a JID's verified name from a profile answer: `IsOnWhatsApp` sets one (user.go:212), but from another, `interactive`, query by phone number only; `types.BusinessProfile` has none (types/user.go:245-253) [observed: source].
  A store read after `GetUserInfo` cannot tell an answer with no name from an older stored one, and a contact store wrapper would be replaced by `SetAllStores` at pairing and deletion (store/sqlstore/container.go:251-262, store/store.go:297, 301-310) [observed: source].
* ADR 0044 refused a `DangerousInternals` path that would keep whatsmeow's nodes, "a fork in all but name".
  This one keeps nothing: it sends whatsmeow's own query through whatsmeow's own function and runs whatsmeow's own update on the answer.
* The owner's decisions of 2026-11-10, which settle the rest of #36 and change nothing here: the about branch is consented, text only (ADR 0063's `text-only`); evidence combined from the fresh companion and the persistent sessions is accepted for #36 AC6; on the saved name he said "I don't care", so its expected value is not independently supplied, and ADR 0062's branch 6 stays as it is, failing `savedname_input_missing` without it [observed: the coordinator's relay of the owner's decisions].

## Decision

* **`sidecar/internal/contacts/userinfo.go`**, one file: for a `*whatsmeow.Client`, the slice's `GetUserInfo` sends `GetUserInfo`'s usync through `DangerousInternals().Usync` with the same query and extras, and reads the answer step for step as user.go:246-279 does: about, picture id and LID; the verified name, parsed as `parseVerifiedName` parses it, a malformed one logged as whatsmeow logs it; `UpdateBusinessName` with the name, so the stored names and the `BusinessName` event are whatsmeow's; then the LID pairs, a failed write logged and the answer kept.
  It also sets the parsed verified name on the entry, the line #1334 adds.
  It leaves out the device list, which the contract does not carry.
  Any other client, the fake WhatsApp's or a test's, answers with its own `GetUserInfo`; the fake already answers the name.
* One `//nolint:staticcheck` on the `DangerousInternals` call, its reason naming #153, #1334 and the removal trigger.
* **Removal**: once the pin's `GetUserInfo` sets `VerifiedName`, the file is deleted and the slice calls `client.GetUserInfo` again; ARCHITECTURE §14 Contacts records the trigger, and the bump re-reads user.go on every bump.
* `DangerousInternals` leaves `sidecar/parity.excluded`: the parity report lists it as covered by `ContactService/GetUserInfo`.
* ADR 0062's `contacts_pin_known_bad` refusal is retired: the live sidecar is built from the working tree (`LiveSidecar.BuildAsync`), so every run from a main with this file answers the name, at `307a2edf117d`, at `79dc2a8c33df` and at a pin with #1334; the refusal matched `307a2edf117d` alone, so the bump in #169 would have passed it with the bug in place [observed: `ContactsCycle.cs` before this change].
  The test still logs the whatsmeow version.
* No contract, `protocol`, public API, record or store change; no §13 invariant changes: `UpdateBusinessName` dispatches `BusinessName` on the call's goroutine, as `GetUserInfo` does (user.go:270, 709-736) [observed: source], and the handler still only writes the outbox.

## Consequences

* `GetUserInfoAsync` answers the verified name WhatsApp sent in that answer, at any pin, and null when it sent none, whatever the store holds.
  That a live answer carries it on this path is [inferred] from the measurement build's run until a run on main records it.
* The usync path itself is checked offline only up to the query: `userinfo_test.go` feeds the read a usync answer, and a real client that is not connected fails `NOT_CONNECTED`; sending needs whatsmeow's unexported socket, so a connected query is checked live only.
* A later upstream `GetUserInfo` that adds a step is not copied until the bump re-reads it [inferred]; a changed `Usync` or `UpdateBusinessName` signature fails the build.
* ADR 0062's branch 2 now runs its unchanged answer checks live; on the persistent stores it still fails `businessname_precondition_spent` after them, by design.
