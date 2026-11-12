# Groups

Every group call is a member of `IWhatsAppClient`, and a group is a `Jid` on `g.us` ([ADR 0030](../adr/0030-groups-participants-settings-invites-and-group-events.md), [ADR 0051](../adr/0051-member-add-mode-link-info-and-disappearing-timers.md), [ADR 0070](../adr/0070-group-photos.md)).

* Read: `GetGroupsAsync` (the groups the account is in) and `GetGroupInfoAsync`.
* Create: `CreateGroupAsync(name, participants)`, and `LeaveGroupAsync`.
* Members: `AddParticipantsAsync`, `RemoveParticipantsAsync`, `PromoteParticipantsAsync`, `DemoteParticipantsAsync`.
* Settings: `SetGroupNameAsync`, `SetGroupTopicAsync`, `SetGroupAnnounceAsync` (only admins send), `SetGroupLockedAsync` (only admins edit the info), `SetGroupMemberAddModeAsync`, and `SetDisappearingTimerAsync` ([Messaging](messaging.md)).
* Photo: `SetGroupPhotoAsync(group, stream)` and `RemoveGroupPhotoAsync`; read it with `GetProfilePictureAsync` ([Chats and contacts](chats-and-contacts.md)).
* Invite links: `GetGroupInviteLinkAsync`, `ResetGroupInviteLinkAsync`, `GetGroupInfoFromLinkAsync` and `JoinGroupWithLinkAsync`.

## Some participants can fail

WhatsApp answers a change of several members one participant at a time, so the member calls and `CreateGroupAsync` return a `ParticipantResult` for each one, `Done` or `Refused` with WhatsApp's code, instead of throwing when only some failed.
When WhatsApp offers it, a refused add carries an invite to send that user instead.

## LIDs and phone numbers

In a group addressed by LID a participant's `Jid` is its LID and `JidAlt` its phone number; in any other group it is the other way round (§14 Groups).
Compare members by both forms, or resolve them ([Chats and contacts](chats-and-contacts.md)).

## Photos

`SetGroupPhotoAsync` reads the stream to its end, sends it to the sidecar in 64 KiB chunks, and returns WhatsApp's id of the new photo; it does not dispose the stream [observed: `SessionClient.SetGroupPhotoAsync`, `Core/Transport/ChunkedCall.cs`].
Chunks work in every mode, External without a shared media root included, and there is no path overload: open a file with `File.OpenRead` and pass the stream [observed: ADR 0070].
The members get `ContactUpdated` with a `ContactChange.Picture` carrying that id, and `RemoveGroupPhotoAsync` brings one with no id [observed: `Groups/GroupPhotoTests.cs` against the fake WhatsApp; live with the same whatsmeow call, #36's picture branches, §14].
In a locked group only an admin may change the photo [inferred: as its other info changes; the code WhatsApp answers a member with is unknown].

The library neither decodes nor resizes the image.
whatsmeow says only that the photo should be a JPEG, and which formats, sizes and dimensions WhatsApp accepts is [unknown] (§14 group photos).
An image WhatsApp refuses with 406 is a `WhatsmeowValidationException`, and nothing changed [observed: `internal/groups` `TestAPhotosErrorsBecomeTheirReasons`].
An empty stream is a `WhatsmeowValidationException` too, never a removal [observed: `Groups/GroupPhotoTests.cs`].

A photo over 15 MiB is refused with `WhatsmeowValidationException` before WhatsApp is asked [observed: `internal/groups` `TestAPhotoOverFifteenMiBIsRefusedAsItsChunksArrive`].
That bound is not WhatsApp's limit, which is [unknown]: whatsmeow sends the photo in one frame and refuses a frame of 16 MiB, so the bound only keeps the call clear of that (§14 group photos).
The sidecar holds the whole photo in memory for the call [observed: ADR 0070].

An answer WhatsApp sends without the new photo's id is an `OutcomeUnknownException`: the photo may be set, so read it back with `GetProfilePictureAsync` [observed: `TestAPhotosErrorsBecomeTheirReasons`; inferred: that the photo may be set].
What WhatsApp answers a removal from a group with no photo is [unknown].

An external sidecar of protocol 1, older than the photo calls, still connects; both calls then throw a `WhatsmeowException` with `ErrorCode` `Unknown` and the message "The sidecar call failed: unknown method SetGroupPhoto for service whatsmeow.v1.GroupService", and every other call works [observed: a run against a protocol 1 sidecar, ADR 0070].
Upgrade the sidecar to use them ([External mode](external-mode.md)).

## Group events

`GroupInfoChanged` carries the group, who changed it, and a list of `GroupChange`s: renamed, topic, announce, locked, member add mode, disappearing timer, and participants joined, left, promoted or demoted.
`GroupJoined` says the account was added to a group.

Both are at least once ([Delivery](delivery.md)), and neither has a message id: a repeat is the same change again, so apply it in a way that is safe to repeat.

## Errors

A failure WhatsApp reports is `WhatsAppErrorException` with its numeric `Code`, such as 403 when the account is not an admin; a link WhatsApp does not accept is a `WhatsmeowException` with `InviteLinkInvalid` ([Errors](errors-and-telemetry.md)).
What WhatsApp answers a non-admin for each call, whether the creator also gets `GroupJoined`, and the codes for adding a member twice or removing a non-member are [unknown]; the fake WhatsApp the tests use models them as [inferred] (§14 Groups).
Which participants WhatsApp lists in `GetGroupInfoFromLinkAsync` for someone who is not a member yet is [unknown] too.

A group's JID is personal data: an older group's carries its creator's phone number, so the library logs none (§7.8).
