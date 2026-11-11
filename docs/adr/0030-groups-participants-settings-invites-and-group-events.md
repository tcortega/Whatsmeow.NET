# 0030. Groups: participants, settings, invite links and group events

- Status: Accepted
- Date: 2026-11-02
- Evidence: issue #34, its plan review, ARCHITECTURE §14 "Measured for Groups (#34)", and the tests named below
- Amended in place: 2026-11-11 (a write that got whatsmeow's bare not connected is OUTCOME_UNKNOWN)

## Context

Issue #34 adds the Groups slice: listing and reading the session's groups, creating them, changing participants and settings, invite links, joining and leaving, and the events for every change.
Reading whatsmeow at the pin for it showed facts the design had to follow [observed: ARCHITECTURE §14 "Measured for Groups (#34)"]:

- A `w:gp2` notification is gated: `handleNotification` sets `cancelled = cli.dispatchEvent(evt)` for it (notification.go:493-506), and the deferred `maybeDeferredAck` sends no ack when a handler failed (receipt.go:115-128).
- `GroupInfo` and `JoinedGroup` come only from `w:gp2`: `parseGroupNotification` gives `JoinedGroup` for a lone `create` child and `GroupInfo` otherwise (group.go:1049-1062). A notification whatsmeow fails to parse is logged and acked, never dispatched (notification.go:494-497).
- Neither event carries the stanza's id (types/events/events.go:480-528), and one `GroupInfo` can carry several changes, one per child node (group.go:886-1014).
- `UpdateGroupParticipants` and `CreateGroup` answer each participant with an `error` code and, when WhatsApp offers one, an `add_request` invite (group.go:713-740); a failure for some participants is not an error of the call.
- `JoinGroupWithLink` maps 410 to `ErrInviteLinkRevoked` and 406 to `ErrInviteLinkInvalid` (group.go:512-536), and passes any other code on as the bare `IQError`; WhatsApp refuses a well formed unknown code with 400 `bad-request` [observed: #36 live run 2026-11-04], and a malformed one too, too short or of foreign characters [observed: #120 live run 2026-11-04, live sidecar.log]; when it sends 406 is [unknown]. It answers a group that approves new members with the same JID while the session only asked to join (group.go:526-529).
- whatsmeow names participants with `%v` in some errors, which keeps no type to strip (group.go:71, 84, 221, 229).
- `SetGroupTopic` given no previous topic id reads the group itself and wraps that read's error with `%v` (group.go:370-376).
- A write's answer can lack what whatsmeow reads from it: `CreateGroup` and `UpdateGroupParticipants` then fail with an `ElementMissingError`, or a `binary.ErrorList` for a group node that does not parse (group.go:151-155, 245-248, 809), and `JoinGroupWithLink` with an `ElementMissingError`, or the empty JID for a node without a `jid` (group.go:526-533, binary/attrs.go:66-69).

## Decision

- **Contract.** `group.proto`: `GroupService` with `ListGroups`, `GetGroup`, `CreateGroup`, `UpdateParticipants`, `SetGroupName`, `SetGroupTopic`, `SetGroupAnnounce`, `SetGroupLocked`, `GetInviteLink`, `ResetInviteLink`, `JoinGroupWithLink` and `LeaveGroup`, and `GroupEvent` as a new `StreamEventsResponse` case, with `Changed` (a list of `GroupChange`) or `Joined` (the group).
  Every call takes the session's client from `session.(*Service).Online`, as Contacts does (ADR 0028), and asserts it to the slice's own `Client` interface, INTERNAL when it cannot.
- **Group events are gated, at least once, and unkeyed.** The handler's false after a failed write withholds the notification's ack, so WhatsApp sends it again (invariant 2 holds for them).
  The key is empty: there is no stanza id, and a key from the content would merge two real changes alike in the same second, a loss, where a redelivery recorded twice is a duplicate (ADR 0022: a duplicate beats a loss).
  When WhatsApp's redelivery comes, relative to later changes, is [unknown]; #10 measures it.
  It can come after them [inferred]: when the outbox write fails while the session stays connected, whatsmeow withholds that one ack only, and acks the later notifications once they are recorded (notification.go:493-506, receipt.go:115-128).
  The docs tell consumers to order a group's changes by their time, not by their arrival.
  A change none of whose parts the contract has (disappearing messages, approval mode, link and unlink, a new invite link, delete, suspension) is not recorded; the issue does not ask for them.
- **Partial participant failures never throw.** Each participant has a result; the call fails only when the whole request does. `CreateGroup` returns the group with the participants WhatsApp added, and one result per participant asked for, matched by either address form, so the creator is not one.
- **Reasons.** `INVITE_LINK_INVALID` (NOT_FOUND) is new, for a link WhatsApp does not accept, malformed, unknown or reset: the issue asks that joining with one fail with an error that says so, which a bare `WHATSAPP_ERROR` code does not.
  Amended by #120: a 400 on the join (and on the preview, ADR 0051) is `INVITE_LINK_INVALID` too, not `WHATSAPP_ERROR`, since the code is all the call sends (group.go:490-495, 512-517) [inferred: so a 400 there is about the code]; the status message keeps the 400. Every other group call keeps a 400 as `WHATSAPP_ERROR`.
  On .NET it is the base `WhatsmeowException` with `WhatsmeowErrorCode.InviteLinkInvalid`, as §8.7 allows: "Until a later slice adds its subtype, a reason maps to `WhatsmeowException` with its `ErrorCode`", the way #33's `WHATSAPP_TIMED_OUT` does.
  Missing admin rights, a group the session is not in and an unknown group have no reason of their own: they are `WHATSAPP_ERROR` with WhatsApp's code.
- **`WhatsAppErrorException`.** `WHATSAPP_ERROR` now maps to a subtype with `int Code`, read from the `code` metadata every slice already sends, for every slice: it is still a `WhatsmeowException` with `ErrorCode` `WhatsAppError`, so the change is additive. ARCHITECTURE §8.7's table says so.
- **Timeouts.** A read without an answer (`ListGroups`, `GetGroup`, `GetInviteLink`, and the read `SetGroupTopic` makes first) is `WHATSAPP_TIMED_OUT`; a write is `OUTCOME_UNKNOWN`.
- **A write that got whatsmeow's bare `ErrNotConnected`** is `OUTCOME_UNKNOWN`: whatsmeow returns that same unwrapped value before the IQ is written (client.go:976-985) and after it (request.go:198-208). A wrapped one comes from a call before the IQ and stays `NOT_CONNECTED`, as a read does [observed: `TestWhatsAppsErrorsBecomeTheirReasons` in `groups_test.go`].
- **A write's answer without its data** is `OUTCOME_UNKNOWN`, not `INTERNAL`: the write went out and may have taken effect. That is an `ElementMissingError` or a `binary.ErrorList` from a write, and a join whose JID is not a group's; .NET would otherwise fail to parse the JID after the session joined [observed: `TestAWriteWhoseAnswerLacksItsDataIsOutcomeUnknown`].
- **A group JID has no agent or device.** `120363...:3@g.us` parses (types/jid.go:160-200), so the slice rejects an agent or device on a group with `INVALID_ARGUMENT` [observed: `TestAMalformedArgumentIsInvalidArgumentAndAsksNothing`].
- **The topic's read is the slice's.** `SetGroupTopic` reads the group itself for the topic it replaces and passes its id, so a failed read keeps its type and its reason.
  A group with no topic yet has no id to pass, so a first topic costs a second read inside whatsmeow, and that read's failure, a timeout, a refusal or a cancel, surfaces as `INTERNAL`; nothing was changed [observed: group.go:370-376, `TestAFirstTopicCostsWhatsmeowASecondReadWhoseFailureIsInternal`].
  The slice does not hand-roll the description IQ to avoid it: whatsmeow should wrap that error with `%w`, which is an upstream report.
- **PII.** Group JIDs are personal data too: an older group's JID carries its creator's phone number. The slice logs none, and its `classify` keeps whatsmeow's sentinel or a bare copy of its typed error, and for any other error only its Go type, never its text, so the `%v` errors above never reach a status or the error log. It stays local to the slice; #62 unifies the slices' classifiers.
- **.NET.** Flat members on `IWhatsAppClient` (ADR 0025): `GetGroupsAsync`, `GetGroupInfoAsync`, `CreateGroupAsync`, `AddParticipantsAsync`, `RemoveParticipantsAsync`, `PromoteParticipantsAsync`, `DemoteParticipantsAsync`, `SetGroupNameAsync`, `SetGroupTopicAsync`, `SetGroupAnnounceAsync`, `SetGroupLockedAsync`, `GetGroupInviteLinkAsync`, `ResetGroupInviteLinkAsync`, `JoinGroupWithLinkAsync` and `LeaveGroupAsync`; records in `Whatsmeow.Groups`, of which `GroupInfo`, `CreatedGroup` and `GroupInfoChanged` compare their `ImmutableArray` members by content, as `MediaRef` does (ADR 0026), so a group event sent again equals the first [observed: `GroupNotification_SentTwice_TheTwoEventsAreEqual`], and so does #9's `ReceiptReceived` with its message ids; `JoinGroupWithLinkAsync` takes the `Uri` that `GetGroupInviteLinkAsync` returns; `GroupInfoChanged`, with one `GroupChange` case per change and one per participant, and `GroupJoined`, both in the group's lane.
- **A join that needs approval** returns the group's JID like a join, as whatsmeow does; the docs say so, and there is no pending flag.
- **The fake WhatsApp** keeps groups on WhatsApp's side (`AddGroup`), records whatsmeow's ack of each notification, `Ack{class: notification, id, type: w:gp2}` in `WaitForSent`, only when every handler succeeded, as whatsmeow does (notification.go:493-506, receipt.go:115-128, 151-181), notifies the members of each change, the one who made it included [inferred: whatsmeow's note that the server echoes a change, send.go:665], and models WhatsApp's codes as [inferred]: 403 for a non-admin's change to settings, participants or a locked group's info, 401 for a non-admin's invite link, 409 for adding a member, 404 for a non-member otherwise, 410 for a reset code and, since #120, 400 `bad-request` for any other code it does not know, malformed or not [observed: #36 live run 2026-11-04 and #120 live run 2026-11-04, live sidecar.log], where it answered 406 [inferred] before; the creator gets no `JoinedGroup`, and the create answer lists a refused participant with its error. Its group clock is fixed, so tests know each time it answers.

## Consequences

- A group change can reach a handler twice, equal to the first; applying it again is harmless, and there is no id to dedupe on. When WhatsApp's redelivery comes is [unknown], so a consumer orders a group's changes by their time.
- A notification whatsmeow cannot parse is lost: whatsmeow acks it without dispatching.
- WhatsApp's codes for refusals, whether the create answer lists refused participants, whether the creator gets a `JoinedGroup`, and how WhatsApp answers a join that needs approval are [unknown] and modelled by the fake as [inferred]; the live checks are #10's.
- Communities, sub-groups, join-request approval, invite messages and group photos are not here.
- Tests: `internal/groups` (whatsmeow-free, over bufconn through the real `server.Server`), `internal/fakewhatsapp/groups_test.go`, `internal/server/reasons_test.go`, `Groups/GroupTests.cs` on the shared fixture, and the kill-either-process tests `EventCrashTests.GroupEvents_...` and `EventProcessTests.GroupEvents_...`.
