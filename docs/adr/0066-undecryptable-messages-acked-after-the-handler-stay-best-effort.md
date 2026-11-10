# 0066. Undecryptable messages are acked after the handler at whatsmeow 79dc2a8c33df, and stay best effort

- Status: Accepted
- Date: 2026-11-10
- Evidence: PR #169, whatsmeow `307a2edf117d` against `79dc2a8c33df` (commits `cf0548055a6b` and `f47dfe93c6d2`), ARCHITECTURE §14 Question 4 and "Measured for Events (#8)"
- Amends: [ADR 0022](0022-outbox-event-stream-and-dispatcher.md) ("The phone's copy is not gated"), ARCHITECTURE §1

## Context

Up to `307a2edf117d`, whatsmeow sent an undecryptable message's retry receipt and ack before it dispatched `UndecryptableMessage`, and an unavailable message's ack and phone request likewise, whatever the handlers returned.
A failed handler on the phone's copy of a message, which whatsmeow dispatches from inside the phone's protocol message, was turned into success.
ADR 0022 and ARCHITECTURE §1 built on that: undecryptable messages are answered early, and the phone's copy is not gated.

At `79dc2a8c33df`:

- whatsmeow dispatches `UndecryptableMessage` first, and a failed handler returns before the retry receipt, the ack or nack and the request to the phone, for an undecryptable, an unavailable and a missing-secret bot message [observed: message.go:324-337, 419-451].
  The sidecar's handler fails only when its outbox write fails (ARCHITECTURE §7.3), so whatsmeow acks such a message only after the write returned.
- A failed handler on a placeholder resend's message, the phone's copy, fails the phone's protocol message, which withholds that stanza's receipt, of type `sender`, or `peer_msg` for a stanza of that type [observed: message.go:938, 1194-1197; receipt.go:287-291].
  A stanza of category `peer` also gets a `peer_msg` receipt on a goroutine regardless [observed: message.go:948-955]; that the phone's answer is such a stanza is [inferred], and whether that receipt differs from the withheld one is [unknown].
- That WhatsApp delivers an unanswered stanza again at the next login is [observed] only for decrypted messages (ARCHITECTURE §14 Question 4, the #10 live runs).
  For an undecryptable message it is [inferred]: WhatsApp's server cannot tell whether a stanza was decryptable, so it should treat the missing ack the same way, but nobody has measured it.
  For the phone's copy it is [unknown], since one of its two receipts goes out anyway.

## Decision

- **The public guarantee does not change.** `UndecryptableMessageReceived` and the phone's copy stay best effort in ARCHITECTURE §1, the Client README and the delivery guide.
  Each of them states what is measured: the ack now waits for the outbox write, the redelivery after a failed write is [inferred], and the phone's copy sends `peer_msg` regardless.
- **The sidecar does not change.** Its handler already returns false on a failed write and wakes the session's redelivery ([ADR 0044](0044-a-reset-after-a-withheld-answer.md)), so a withheld undecryptable message is reset for like a withheld message.
- **The fake models the pin.** `DeliverUndecryptableMessage` answers only when every handler succeeded and otherwise delivers the message again at the next login, as `DeliverMessage` does; its comments tag that redelivery [inferred].

## Consequences

- An undecryptable message whose write failed is now probably delivered again instead of lost, and it may then decrypt and arrive as `MessageReceived`; the application must not count on either until a live run measures it.
- A live run that withholds the answer to an undecryptable message and sees it again at a login would let a later ADR widen the guarantee; the phone's copy would need its own measurement.
- whatsmeow's own buffer failing (`ErrEventBufferDB`, message.go:420-422) dispatches nothing, so no handler fails and the sidecar's redelivery is not woken: whatsmeow answers nothing, and the stanza waits until the next login [inferred]. It takes a disk failure, so nothing covers it.
- ADR 0022's "the phone's copy is not gated" holds for the guarantee, not for whatsmeow's delivery receipt at this pin.
