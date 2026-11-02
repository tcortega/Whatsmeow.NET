# 0038. Message actions: reactions, edits and revokes as sends, replies and mentions as send options, and what the unwrapping said

- Status: Accepted
- Date: 2026-11-02
- Evidence: issue #30, its plan review, ARCHITECTURE §14 "Measured for Message actions (#30)", and the tests named below
- Uses: [ADR 0025](0025-messaging-and-receipts.md) (the send queue and records), [ADR 0033](0033-media-messages.md) (the media sends and the content mapping), [ADR 0036](0036-testing-package-and-fake-client.md) (the fake client)

## Context

Issue #30 sends reactions, edits, revokes, quoted replies and mentions, delivers the ones other people send, and stops dropping what whatsmeow's unwrapping of a received message said: ephemeral, the chat a message from the account's own phone went to, and the message as it was received.
A group `READ_SELF` receipt also lacked the sender of the messages it covers.
Reading whatsmeow at the pin and mautrix-whatsapp `5dd621f` for it showed facts the design had to follow [observed: ARCHITECTURE §14 "Measured for Message actions (#30)"]:

- whatsmeow builds a reaction, a revoke and an edit itself (send.go:536-635), and names the target with `BuildMessageKey`, which needs the account's own JID and LID: an empty sender or the account's own is the account's message; another's is someone else's, with the participant outside a direct chat.
- A received reaction, edit or revoke is an ordinary gated `events.Message` (message.go:1172-1178); an edit comes unwrapped from its `EditedMessage` as a `ProtocolMessage` of type `MESSAGE_EDIT`, a revoke as one of type `REVOKE`, which is the type's zero value.
- `UnwrapRaw` removes the device-sent, ephemeral, view-once, edit and other wrappers and keeps the raw message beside the unwrapped one, the same pointer when it removed none (types/events/events.go:377-425).
- mautrix replies with `StanzaID`, `Participant`, a `QuotedMessage` that is the quoted text as a conversation (empty when it has none) and `QuotedType EXPLICIT`, mentions in `MentionedJID`, and reads a message as disappearing from its `ContextInfo.Expiration` (pkg/msgconv/from-matrix.go:56-126, 344-375; from-whatsapp.go:257-260).
- A receipt's `MessageSender` is its `recipient` attribute (receipt.go:77).

## Decision

- **Three RPCs, sends like the others.** `SendReaction`, `SendEdit` and `SendRevoke` go through the same `send`: one per-session queue, the same pacing, flight, `sent_message` record and classification (ADR 0025). The `client_message_id` is the action's own id, so a repeat returns its first result whatever kind either send was, and an unknown outcome is never sent again (invariant 10).
  The message is whatsmeow's own: the slice's small interface gains `BuildReaction`, `BuildRevoke` and `BuildEdit`, called once the session is online, so a whatsmeow change to them stays inside whatsmeow.
  The fake WhatsApp copies the three; a test compares its builds with whatsmeow's own on the same device, timestamps aside.
- **The target.** A target is `MessageKey { Id, Sender? }`: a null sender, or the account's own JID, is the account's own message, as `BuildMessageKey` reads it; another member's in a group is an admin revoke. WhatsApp's refusal of a revoke is `WHATSAPP_ERROR`, `WhatsAppErrorException` with its `Code`, recorded and repeated.
  An edit is text only, of the account's own message; nothing checks an edit window, because the sidecar keeps no messages (§1), and whatsmeow's `EditWindow` is read nowhere.
- **Replies and mentions are send options.** `SendTextRequest` and `SendMediaRequest` gain `reply_to` (`MessageReply { id, sender, quoted_text }`) and `mentions`; the sidecar builds the context as mautrix does, on an extended text for a text (a plain conversation without either) and on the media message for media, inside a captioned document's wrapper.
  A reply without a sender quotes the account's own message: the sidecar names the account by its device's JID without the device (`session.(*Service).Device`); whether WhatsApp needs a participant at all is [unknown].
  In .NET each typed send has one overload with a required `SendOptions` after its content; the overloads without options keep their shape, so every existing call binds as before. PublicApiAnalyzers' RS0026 (no two overloads with optional parameters) is suppressed on them with that justification: the options are required, so no call binds to both.
- **Received actions are content.** `ReactionContent { Target, Emoji }` (empty is a removal), `EditContent { Target, Content }` and `RevokeContent { Target }`, whose remover is the event's source.
  A key's sender reads as mautrix reads it (pkg/msgconv/msgconv.go:97-132): its participant, else the actor when the key says the actor sent it, else in a direct chat the other side, the chat when the account acted and otherwise the account itself, null.
  A protocol message is a revoke or an edit only when its type is set.
- **What the unwrapping said.** `MessageEvent` gains `ephemeral` (4), `device_sent_destination` (5) and `raw_message` (6), the last only when whatsmeow removed a wrapper, so a plain message is not recorded twice. No edit flag: `EditContent` is the edit. No document-with-caption, Lottie, bot-invoke or V2 view-once flag: the content mapping already handles what they wrapped, and `Raw` shows the wrappers.
  `MessageReceived` gains `ReplyTo` (`QuotedMessage { Id, Sender?, Content? }`), `Mentions`, `IsEphemeral` (the wrapper, or an expiration above 0, as mautrix reads it), `DestinationChat`, and the `[Experimental("WMN1001")]` `Raw`, the message as received, or the message itself when nothing was unwrapped. None is required, so a record built before them still compiles.
  `ReceiptEvent` gains `message_sender` (5), `ReceiptReceived.MessageSender`; the receipt's natural key is unchanged.
- **`Raw` is outside the record's equality.** `MessageReceived` compares every stable member, the mentions by content, and leaves out `Raw`: its type is whatsmeow's, which changes with every bump, and the equality of a stable record must not depend on it. Every message comes with a `Raw`, so including it would make every record a test builds unequal to the one delivered; a test of `Raw` compares it on its own.
  The experimental attribute does raise WMN1001 inside its own assembly, so the mapping that fills it carries a justified pragma [observed: the build].
- **The fake WhatsApp** gains the three builders, `RefuseNextSend` (WhatsApp answers the next message node with an error code, send.go:468-470), and `DeliverReceiptRequest.message_sender`; received actions, replies, mentions and wrappers need no control, since `DeliverMessage` already unwraps a raw message with whatsmeow's own `UnwrapRaw`.
- **The consumer fake** implements the three members and the six overloads; an overload records its options after the content, and `FailNext` of a member fails both of its overloads.

## Consequences

- A key's chat is the chat given, while whatsmeow sends a phone number's message to its LID; whether WhatsApp matches a reaction, revoke or quote whose key names the phone number is [unknown], and so are the address form a LID-addressed group needs for a mention, what WhatsApp answers a non-admin revoke, and whether it refuses or drops a late edit. The Phase 2 exit live run (#36) records them.
- The History slice maps its messages with the same `events.MessageEvent` ([ADR 0035](0035-history-sync-that-never-delays-live-traffic.md)), so a history message's event carries the new fields too; `HistoryMessage` does not show them yet, and its content maps reactions, edits and revokes as a live message's does.
- `device_sent_destination` goes through `server.Text`, as every string from WhatsApp data does: it is a proto2 string, so whatsmeow passes invalid UTF-8 through.
- Polls stay #31, and a caption edit, a forwarded flag and a disappearing timer on send stay for `SendRawAsync`.
- Tests: `internal/messaging/action_test.go` (whatsmeow's own builds, validation, the context of a reply and mentions, the account's own reply, one queue and record with the other sends), `internal/events` (the flags and the raw message), `internal/messaging/receipt_test.go` (the messages' sender), `internal/fakewhatsapp/action_test.go` (the builders against whatsmeow's, `RefuseNextSend`, the receipt's sender), `Whatsmeow.IntegrationTests/Messaging/MessageActionTests.cs` and `Whatsmeow.Testing.Tests/MessageActionCallTests.cs`.
