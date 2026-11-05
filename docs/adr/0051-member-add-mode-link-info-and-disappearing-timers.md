# 0051. Who may add members, a group's info from its link, and disappearing timers

- Status: Accepted
- Date: 2026-11-02
- Evidence: issues #104, #105 and #106, their plan review, ARCHITECTURE §14 "Measured for #104, #105 and #106", and the tests named below with their mutations
- Amends: ADR 0030 (the group changes that are not recorded)

## Context

The #36 parity report lists three whatsmeow methods as excluded until they are built: `SetGroupMemberAddMode`, `GetGroupInfoFromLink` and `SetDisappearingTimer`.
Reading whatsmeow at the pin showed facts the design had to follow [observed: ARCHITECTURE §14 "Measured for #104, #105 and #106"]:

- `SetGroupMemberAddMode` sends a `member_add_mode` child whose content is `admin_add` or `all_member_add`, and a group's info reads the mode from the same child (group.go:785-787, 1084-1097).
  But `parseGroupChange` has no case for it: a notification's `member_add_mode` child lands in `UnknownChanges` as it came (group.go:1006-1007), and `events.GroupInfo` has no field for it (types/events/events.go:493-528).
  That WhatsApp's notification names and fills the child as the IQ does is [inferred].
- `GetGroupInfoFromLink` is an IQ get on the invite code, where the join is a set; it strips the link prefix and maps 410 and 406 as the join does (group.go:488-536). WhatsApp answers it 400 `bad-request` for an unknown or malformed code, as it does the join [observed: #120 live run 2026-11-04, live sidecar.log].
- `SetDisappearingTimer` is two methods in one (send.go:666-697): a group's timer is an IQ, a direct chat's is `SendMessage` of an `EPHEMERAL_SETTING` protocol message with no id, timestamped `time.Now()` when no time is given.
  A group's timer WhatsApp refuses with 400 is `ErrInvalidDisappearingTimer`, which unwraps to the `IQError` (errors.go:157-176, send.go:687-690).
- whatsmeow sets no timer on a later send: send.go writes no `Expiration` (only the protocol message's field, send.go:675).
- A received `EPHEMERAL_SETTING` is not consumed by `handleProtocolMessage` and is dispatched as `events.Message` (message.go:884-934, 1172-1178).
- Our send queue paces, records and makes idempotent only the sends it hands to whatsmeow's `SendMessage` with its id (ADR 0025, ADR 0042).

## Decision

- **Contract.** `GroupService` gains `SetGroupMemberAddMode`, `GetGroupInfoFromLink` and `SetGroupDisappearingTimer`; `Group` gains `member_add_mode` (10), a new `GroupMemberAddMode` enum, unspecified when WhatsApp did not say or said a mode the contract does not have; `GroupChange` gains `member_add_mode_changed` (9) and `disappearing_timer_changed` (10).
  `MessageService` gains `SendDisappearingTimer`, a send with its `client_message_id` like `SendRevoke`.
  No new reason.
- **A timer is one the phones offer.** 24 hours, 7 days, 90 days or zero (off), as whatsmeow's constants are (send.go:636-657); any other is `INVALID_ARGUMENT` (field `timer`) and nothing is sent, until a live run shows WhatsApp takes others.
  `server.DisappearingTimer` checks it for both RPCs.
- **#106 is split where whatsmeow splits it.** A group's timer is a group setting, through `SetDisappearingTimer` in the groups slice and its `classify`.
  A direct chat's is a message, built as whatsmeow builds it with the slice's clock and sent through the send queue, so it is paced, recorded and at most once per id like every other send; whatsmeow's own method would bypass all three.
  So the parity report credits `SetDisappearingTimer` to `GroupService/SetGroupDisappearingTimer`, and the direct chat's send to `SendMessage`.
  On .NET it is one member, `SetDisappearingTimerAsync(Jid chat, TimeSpan timer, MessageId? id = null)`, which sends a group JID to `GroupService` and any other to `MessageService` with `id`, a new one when null, so a retry with the same id after an unknown outcome sends the setting once, as every send's does; a group's setting has no id, so it is ignored there. It returns no `SendResult`, since a group's has none.
- **A member add mode change is read from `UnknownChanges`.** The groups mapper reads a `member_add_mode` child whose content is a mode the contract has; any other content is left out, so a notification with nothing else is not recorded.
  Upstream whatsmeow should parse it; the draft report goes through the maintainer, not this repo.
- **Disappearing changes are recorded.** ADR 0030 left them out; `GroupChange.DisappearingTimerChanged` carries the new timer, zero for `not_ephemeral`.
  A join approval change is still not recorded, and replaces the disappearing one as the tests' example of a change the contract has no case for.
- **A received direct chat timer is content.** `DisappearingTimerContent { Timer }` maps an `EPHEMERAL_SETTING` protocol message, in live events and history alike; no contract change, since the message is already in `MessageEvent`.
- **Errors.** `SetGroupMemberAddMode` and `SetGroupDisappearingTimer` are writes (`OUTCOME_UNKNOWN` on a timeout), `GetGroupInfoFromLink` a read (`WHATSAPP_TIMED_OUT`; an answer without its group is `INTERNAL`, as `GetGroup`'s is), and a link WhatsApp does not accept is `INVITE_LINK_INVALID` as for the join, a 400 included since #120 (ADR 0030 Reasons); the fake answers a code it does not know with 400 as for the join, as live (ADR 0030).
  A non-admin's refusal is `WHATSAPP_ERROR` with WhatsApp's code, which is [unknown] and modelled as 403 [inferred].
- **.NET.** `SetGroupMemberAddModeAsync(Jid group, GroupMemberAddMode mode)`, `GetGroupInfoFromLinkAsync(Uri link)` and `SetDisappearingTimerAsync`, in a `whatsmeow.send` span of type `disappearing_timer` for a direct chat (ADR 0037); `GroupMemberAddMode { AdminsOnly, AllMembers }`; `GroupInfo.MemberAddMode`, nullable and not `required`, so existing constructions still compile; `GroupChange.MemberAddModeChanged` and `GroupChange.DisappearingTimerChanged`.
  The Testing package's fake records the three members.
- **The fake WhatsApp** gives a created group the mode its create asked for, `all_member_add` unless told otherwise, as whatsmeow asks (group.go:59-63), though what WhatsApp answers after a create is [unknown], and a group `AddGroup` put there none until one is set; lets any member add when the mode is `all_member_add`, and only an admin otherwise or when none is set; lets only an admin set the mode (403 [inferred]); changes a timer as the group's info (only an admin of a locked group, 403 [inferred]), with 400 for one not offered; and answers a link's group with every member [inferred], joining nothing.
  `DeliverGroupNotification` gains `member_add_mode`, `disappearing_timer` and `membership_approval`; its `ephemeral` field is reserved.

## Consequences

- If WhatsApp's notification names the mode another way, a mode change by another admin is not recorded, and nothing fails; a live run checks it.
- A direct chat's timer is paced behind the session's other sends, and costs a send of its budget.
- The session's messages after a timer change do not disappear unless the caller sets their expiration (`SendRawAsync`); the docs say so.
- What WhatsApp shows a non-member of a link's group, its codes for a non-admin, and whether it accepts other timers are [unknown]; the live checks are #10's.
- Tests: `internal/groups` (`TestTheMemberAddModeTheTimerAndALinksInfoReachWhatsApp`, `TestAGroupsMemberAddModeIsTheContractsOrUnspecified`, `TestAMalformedModeTimerOrLinkIsInvalidArgumentAndAsksNothing`, `TestTheNewCallsErrorsBecomeTheirReasons`, `TestMemberAddModeAndTimerNotificationsMapToTheirChanges`), `internal/messaging/timer_test.go`, `internal/fakewhatsapp/groups_test.go`, `Groups/GroupModeLinkTimerTests.cs`, `Messaging/DisappearingTimerTests.cs` and the Testing package's `CallTests`.
