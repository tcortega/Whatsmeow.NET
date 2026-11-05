# 0057. An event the sidecar does not record is answered without being in the outbox

- Status: Accepted
- Date: 2026-11-05
- Evidence: issues #119 and #122, PRs #121 and #131, and the whatsmeow paths in ARCHITECTURE §14 ("Measured for #122")
- Amends: ARCHITECTURE §13 invariant 2, [ADR 0022](0022-outbox-event-stream-and-dispatcher.md)

## Context

§13 invariant 2 said a gated stanza is answered only after it is durably in the outbox.
The Events handler already answered gated events it maps to nothing: a sender-key-only part, whose content comes as its own message, and a history sync notification, which goes to the history queue.
#119 found a third kind: whatsmeow dispatches a grouped receipt covering the empty message id (receipt.go:45-62 at the pin, tulir/whatsmeow#1270), which covers no message.
#122 found more: whatsmeow can hand a mapper an empty or malformed JID or id (§14), and the client refuses such an event as the library's fault, then skips and acks it (§8.7), so recording it loses it anyway, after a round trip and an error log.

## Decision

- An event the sidecar does not record is answered, as one it maps to nothing is: the handler returns true, and whatsmeow answers the stanza. That covers a receipt left with no message id (#119) and an event the client cannot read (#122).
- The Events slice's handler is the gate: after its own mapping and each slice's mapper, it records an event only when `events.Unreadable` passes it, the client's reads restated, and logs one warning per event it drops, naming its kind and field, never a value ([ADR 0039](0039-pii-redaction-of-stderr-and-status-messages.md)). The same check keeps an ephemeral event the client cannot read out of the ring. A mapper only leaves out a list member it cannot fill.
- `cmd/whatsmeow-net/readable_test.go` feeds every mapper `main` wires what whatsmeow can hand it, and a payload case the gate does not know is refused, so a new mapper cannot bypass it.

## Consequences

- WhatsApp does not send such an event again: it is lost, as the client's skip lost it, but visibly in the sidecar's log, and the stream never carries an event the client must skip.
- The client keeps its strict reads and its skip and ack (§8.7); the fake's `RecordEvent` is the one way to exercise them.
- A new strict read on the client adds its field to `events.Unreadable`, and a new payload case joins it before any mapper records one.
