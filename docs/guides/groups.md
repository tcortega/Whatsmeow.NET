# Groups

Every group call is a member of `IWhatsAppClient`, and a group is a `Jid` on `g.us` ([ADR 0030](../adr/0030-groups-participants-settings-invites-and-group-events.md), [ADR 0051](../adr/0051-member-add-mode-link-info-and-disappearing-timers.md)).

* Read: `GetGroupsAsync` (the groups the account is in) and `GetGroupInfoAsync`.
* Create: `CreateGroupAsync(name, participants)`, and `LeaveGroupAsync`.
* Members: `AddParticipantsAsync`, `RemoveParticipantsAsync`, `PromoteParticipantsAsync`, `DemoteParticipantsAsync`.
* Settings: `SetGroupNameAsync`, `SetGroupTopicAsync`, `SetGroupAnnounceAsync` (only admins send), `SetGroupLockedAsync` (only admins edit the info), `SetGroupMemberAddModeAsync`, and `SetDisappearingTimerAsync` ([Messaging](messaging.md)).
* Invite links: `GetGroupInviteLinkAsync`, `ResetGroupInviteLinkAsync`, `GetGroupInfoFromLinkAsync` and `JoinGroupWithLinkAsync`.

## Some participants can fail

WhatsApp answers a change of several members one participant at a time, so the member calls and `CreateGroupAsync` return a `ParticipantResult` for each one, `Done` or `Refused` with WhatsApp's code, instead of throwing when only some failed.
When WhatsApp offers it, a refused add carries an invite to send that user instead.

## LIDs and phone numbers

In a group addressed by LID a participant's `Jid` is its LID and `JidAlt` its phone number; in any other group it is the other way round (§14 Groups).
Compare members by both forms, or resolve them ([Chats and contacts](chats-and-contacts.md)).

## Group events

`GroupInfoChanged` carries the group, who changed it, and a list of `GroupChange`s: renamed, topic, announce, locked, member add mode, disappearing timer, and participants joined, left, promoted or demoted.
`GroupJoined` says the account was added to a group.

Both are at least once ([Delivery](delivery.md)), and neither has a message id: a repeat is the same change again, so apply it in a way that is safe to repeat.

## Errors

A failure WhatsApp reports is `WhatsAppErrorException` with its numeric `Code`, such as 403 when the account is not an admin; a link WhatsApp does not accept is a `WhatsmeowException` with `InviteLinkInvalid` ([Errors](errors-and-telemetry.md)).
What WhatsApp answers a non-admin for each call, whether the creator also gets `GroupJoined`, and the codes for adding a member twice or removing a non-member are [unknown]; the fake WhatsApp the tests use models them as [inferred] (§14 Groups).
Which participants WhatsApp lists in `GetGroupInfoFromLinkAsync` for someone who is not a member yet is [unknown] too.

A group's JID is personal data: an older group's carries its creator's phone number, so the library logs none (§7.8).
