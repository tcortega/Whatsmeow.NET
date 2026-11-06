# Chats and contacts

Presence and typing, the state of a chat (muted, pinned, archived, read, starred), and the people behind the JIDs.

## Presence and typing

* `SendPresenceAsync(PresenceState.Available)` shows the account online, `Unavailable` offline.
* `SubscribePresenceAsync(user)` asks WhatsApp for a user's presence, which then arrives as `PresenceChanged`, with `LastSeen` when the user shares it.
* `SendChatPresenceAsync(chat, ChatPresenceState.Typing)` shows typing, `Recording` a voice note, `Paused` stops it; others' arrive as `ChatPresenceChanged`.

`SendPresenceAsync` fails with `NoPushName` until the account's push name has arrived from the phone in the app state (§14 Presence).
`Available` also turns on active delivery receipts, and `Unavailable` turns them off (§14 Presence).

Presence and typing are ephemeral: no ack, never repeated, lost on a restart ([Delivery](delivery.md#presence-and-typing)).

## Chat state

`MuteChatAsync` (until a time, or for ever), `PinChatAsync`, `ArchiveChatAsync`, `MarkChatReadAsync`, `StarMessageAsync`, and their opposites, change what all the account's devices show ([ADR 0031](../adr/0031-chat-state-and-app-state-recovery.md)).
They write WhatsApp's app state, which needs its keys from the phone first: right after pairing, a write can fail with `NotConnected` until they arrive.
Archiving a chat also unpins it: whatsmeow builds the change that way (§14 Chat state).
A write can fail with `RateLimited` although WhatsApp took it, when the sync after it got the 429; while the backoff WhatsApp sets with it lasts, later writes succeed with that sync held back, and when their own change arrives as `ChatStateChanged` is [unknown] (§14 Chat state).

Changes from the account's other devices arrive as `ChatStateChanged` with a `ChatStateChange`: mute, pin, archive, read or star.
`FromFullSync` is set for the whole state that arrives after pairing or after the sidecar recovered from a sync error, which it does by itself, waiting an hour after a recovery that failed, one WhatsApp held back after a 429 included.
A write WhatsApp rejected because the session's copy was out of step fails with `RateLimited` when the full sync it needs is held back that way; it did not take effect (§14 Chat state).
These events are best effort and have no message id: apply each as a value to set.
That other clients write a mute's end in milliseconds, as whatsmeow does, is [inferred] (§14 Chat state).

## Contacts

* `IsOnWhatsAppAsync(phoneNumbers)` checks numbers, with each one's JID and LID when it is on WhatsApp.
* `GetUserInfoAsync(users)` gives the status text, picture id, verified business name and devices; `GetProfilePictureAsync` the picture.
* `GetContactsAsync` lists the contacts the account knows, with the name it saved, their push name and business name.
* `GetBlocklistAsync`, `BlockAsync` and `UnblockAsync` manage the blocklist; what an inactive blocklist entry means is [unknown].

`ContactUpdated` tells you a contact's push name, business name, picture or about text changed.
It is best effort ([ADR 0028](../adr/0028-contacts-blocklist-and-lid-resolution.md)).

## LID and phone numbers

WhatsApp is moving users from phone number JIDs (`@s.whatsapp.net`) to LIDs (`@lid`), so one person can show up under both.
Events carry both forms where WhatsApp gave them: `MessageSource` has `Sender` and `SenderAlt`, and `Chat` and `RecipientAlt`.
A message you send to a phone number goes to its LID, and `SendResult.Chat` says so ([Messaging](messaging.md)).

`ResolveJidsAsync(jids)` gives each JID's other form from what the sidecar has stored, without asking WhatsApp; a pair it never saw comes back without one.
The store learns pairs from messages, contact queries and groups, and the map is shared by every session of the sidecar (§14 Contacts).
Key your own data by one form and resolve the other, or store both.
