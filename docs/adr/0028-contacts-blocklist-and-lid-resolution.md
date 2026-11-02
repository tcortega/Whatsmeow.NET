# 0028. Contacts, blocklist and LID resolution

- Status: Accepted
- Date: 2026-11-01
- Evidence: issue #33, its plan review, ARCHITECTURE §14 "Measured for Contacts (#33)", and the tests named below

## Context

Issue #33 adds the Contacts & LID slice: checking numbers, reading profiles and pictures, the two address forms of a user, the session's contacts and blocklist, and contact change events.
Reading whatsmeow at the pin for it showed facts the design had to follow [observed: ARCHITECTURE §14 "Measured for Contacts (#33)"]:

- The LID map is one table for the whole container, shared by every session (store/sqlstore/container.go:260), and an unknown pair is the empty JID, not an error (store/sqlstore/lidmap.go:96-98, 105-107, 115); a known one comes with the input's device (lidmap.go:100, 113).
- A device that was never paired has no stores (container.go:212-224 against 257-263), so a store read needs a paired session.
- whatsmeow has no accessor for a client's store: it is the field `Client.Store`, which no interface can name.
- No contact change event is gated. `PushName` and `BusinessName` are dispatched on their own goroutines from a message (message.go:56-61), `Picture` and `UserAbout` from notifications acked whatever the handlers return (notification.go:91-115, 449-466, 472), a saved name from app state, whose handler result `FetchAppState` ignores (appstate.go:31-39), and `Blocklist` from the account_sync goroutine (notification.go:245-264, 487).
- `PushName` and `BusinessName` fire only when the stored value changed (user.go:683, 713).
- whatsmeow logs other people's JIDs at warn and error, on failures and on paths that are not (message.go:327, 345, 351, 412, 415; notification.go:44, 48, 174, 183, 332; user.go:214, 255, 672-724; client.go:1098), and names the JID in the errors of `UpdateBlocklist` (user.go:1034, 1039, 1043, 1052); an `IQError` without a code prints its node (errors.go:216-226).

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
- **Blocklist items.** Every item WhatsApp lists is returned with its JID and its other form. At the pin whatsmeow reads `active` only to store the pairs of the active items (user.go:975-980), so what an inactive item means is [unknown]: it is not exposed, and inactive items are not filtered out. The live check is #10's.
- **Block and unblock take the user without its device.** whatsmeow sends the JID as given, and the LID it stores for a phone number keeps the device (user.go:1027-1066), so the slice drops the device first.
- **A read without an answer is `WHATSAPP_TIMED_OUT`.** whatsmeow gives up on a request after 75 s (`ErrIQTimedOut`, request.go:152-182). A read did nothing, so it is the new `WHATSAPP_TIMED_OUT` (UNAVAILABLE), safe to repeat, on `WhatsmeowException` with its `ErrorCode`; no existing reason fits: `NOT_CONNECTED` says the session is not logged in, which a slow answer does not show, and `OUTCOME_UNKNOWN` is for writes. A write without an answer stays `OUTCOME_UNKNOWN`.
- **A LID answer without a phone number.** When `IsOnWhatsApp` answers a number with a LID and no `pn_jid` (user.go:194-204), the check's JID is the number asked for.
- **PII.** The slice logs no number or JID, and no error it returns carries one: `classify` keeps whatsmeow's sentinel, or a copy of its typed error without the nodes, never the text whatsmeow wraps around it, so neither the status message the application sees nor the error line `server.toStatus` writes for an INTERNAL one has the JID. The fake WhatsApp returns whatsmeow's texts, JID included, so the tests see them. The residual is whatsmeow's own warn and error lines above, which reach stderr; ARCHITECTURE §7.8 lists them, and redacting them is a follow-up.

## Consequences

- A community's picture is read as a plain group's: whatsmeow asks for it differently (`GetProfilePictureParams.IsCommunity`, user.go:574-586), and the slice does not tell a community from a group, so what WhatsApp answers for one is [inferred: not the community's picture].
- A contact change can be lost; an application that needs the current value reads it (`GetContactsAsync`, `GetUserInfoAsync`).
- A pair learned by one session resolves for the others; the sessions of one sidecar belong to one application.
- What WhatsApp answers for an unregistered number, whether it answers every query of a batch, and how it keys a LID query's answer are [unknown] and modelled by the fake as [inferred]; the live checks are #10's and #36's.
- Tests: `internal/contacts` (whatsmeow-free, over bufconn through the real `server.Server`), `internal/session/online_test.go`, `internal/events/mapper_test.go`, `internal/fakewhatsapp/contacts_test.go`, and `Contacts/ContactTests.cs` on the shared fixture.
