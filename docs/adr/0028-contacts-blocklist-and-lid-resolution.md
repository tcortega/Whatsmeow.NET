# 0028. Contacts, blocklist and LID resolution

- Status: Accepted
- Date: 2026-11-01
- Evidence: issue #33, its plan review, ARCHITECTURE §14 "Measured for Contacts (#33)", and the tests named below

## Context

Issue #33 adds the Contacts & LID slice: checking numbers, reading profiles and pictures, the two address forms of a user, the session's contacts and blocklist, and contact change events.
Reading whatsmeow at the pin for it showed facts the design had to follow [observed: ARCHITECTURE §14 "Measured for Contacts (#33)"]:

- The LID map is one table for the whole container, shared by every session (store/sqlstore/container.go:260), and an unknown pair is the empty JID, not an error (store/sqlstore/lidmap.go:91-116).
- A device that was never paired has no stores (container.go:212-224 against 257-263), so a store read needs a paired session.
- whatsmeow has no accessor for a client's store: it is the field `Client.Store`, which no interface can name.
- No contact change event is gated. `PushName` and `BusinessName` are dispatched on their own goroutines from a message (message.go:56-61), `Picture` and `UserAbout` from notifications acked whatever the handlers return (notification.go:91-115, 449-466, 472), a saved name from app state, whose handler result `FetchAppState` ignores (appstate.go:31-39), and `Blocklist` from the account_sync goroutine (notification.go:245-264, 487).
- `PushName` and `BusinessName` fire only when the stored value changed (user.go:683, 713).
- whatsmeow logs other people's JIDs at warn or error on a few failure paths (user.go:214, 255; client.go:1098).

## Decision

- **Contract.** `contact.proto`: `ContactService` with `IsOnWhatsApp`, `GetUserInfo`, `GetProfilePicture`, `ResolveJids`, `ListContacts`, `GetBlocklist` and `UpdateBlocklist`, and `ContactEvent` as a new `StreamEventsResponse` case.
  An empty list returns an empty list without asking WhatsApp or looking the session up.
- **Online and offline calls.** The calls that ask WhatsApp take the session's client from `session.(*Service).Online`, the one Phase 2 slices share: a session that is not Connected or CaughtUp fails at once with its reason.
  The slice reaches whatsmeow by asserting the returned `session.Client` to its own `Client` interface, and fails INTERNAL when it cannot, as pair.go does for `PreKeysUploaded`.
  `ResolveJids` and `ListContacts` read the store through `session.(*Service).Device`, a paired session's device in any state, so they work while the session is disconnected.
- **Resolution reads the store only.** `ResolveJids` never asks WhatsApp; the store learns pairs from messages with both forms, `IsOnWhatsApp`, `GetUserInfo` and the blocklist. It is offline, never rate limited, and an unknown LID is an empty alt. Because the LID map is the container's, a pair one session learned resolves for every session of the sidecar.
- **Contact events through the outbox, unkeyed and best effort.** `PushName`, `BusinessName`, `Picture` (groups included) and `UserAbout` are recorded as `ContactEvent` with an empty key, as a `ConnectionStateEvent` is: they are never redispatched from the decrypted-event buffer, and a repeat carries the same value. whatsmeow does not wait on the handler for any of them, so one the sidecar fails to record is lost. Saved-name (`Contact`) and `Blocklist` events are not mapped; the issue does not ask for them.
- **A slice maps its own events.** `events.Mapper`, `func(sessionID string, evt any) (key, event, ok)`, is how a slice records whatsmeow events: `cmd/whatsmeow-net` passes each slice's mapper to `events.New`, and the Events slice's one handler, with its one recover and its one write path, records what the first mapper that maps an event returns. This is the pattern for every later slice.
- **.NET.** Flat members on `IWhatsAppClient` (ADR 0025): `IsOnWhatsAppAsync`, `GetUserInfoAsync`, `GetProfilePictureAsync`, `ResolveJidsAsync`, `GetContactsAsync`, `GetBlocklistAsync`, `BlockAsync`, `UnblockAsync`; records in `Whatsmeow.Contacts`; `ContactUpdated` with a `ContactChange` case per change; `Jid.IsPhoneNumber` and `Jid.IsLid`; `RecipientNotFoundException` for `RECIPIENT_NOT_ON_WHATSAPP`.
- **Blocklist items.** Every item WhatsApp lists is returned with its JID and its other form; what WhatsApp's `active` flag means is [unknown], so it is not exposed.
- **PII.** The slice logs no number or JID, and its own error texts carry none. Two residuals are accepted: whatsmeow's own warn and error lines above, and `server.toStatus`, which logs an INTERNAL error's text at error level, where whatsmeow's text for a store failure in `UpdateBlocklist` carries the JID (user.go:1034, 1039, 1052). ARCHITECTURE §7.8 says so.

## Consequences

- A contact change can be lost; an application that needs the current value reads it (`GetContactsAsync`, `GetUserInfoAsync`).
- A pair learned by one session resolves for the others; the sessions of one sidecar belong to one application.
- What WhatsApp answers for an unregistered number, whether it answers every query of a batch, and how it keys a LID query's answer are [unknown] and modelled by the fake as [inferred]; the live checks are #10's and #36's.
- Tests: `internal/contacts` (whatsmeow-free, over bufconn through the real `server.Server`), `internal/session/online_test.go`, `internal/events/mapper_test.go`, `internal/fakewhatsapp/contacts_test.go`, and `Contacts/ContactTests.cs` on the shared fixture.
