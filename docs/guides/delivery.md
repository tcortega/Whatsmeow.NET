# Delivery

What reaches your handlers, how often, and in which order.

## At least once, or best effort

WhatsApp waits for a device to answer each message, receipt or notification it sends, and sends it again when no answer comes.
The sidecar records each event in its SQLite database and only then lets whatsmeow answer, so a crash of either process loses nothing.
That only works for the kinds whose answer whatsmeow lets a handler hold back; it answers the rest itself, recorded or not (§1, §14 question 4, read in whatsmeow's source).

At least once, through a crash of either process:

* `MessageReceived`: every decrypted message, reactions, edits, revokes, polls, votes and media included.
* `ReceiptReceived`: every receipt but a retry receipt.
* `GroupInfoChanged` and `GroupJoined`: group notifications.
* History sync: the notification is queued before it is answered, and its messages come as `HistoryReceived` ([History](history.md)).
* `SessionStateChanged`: the sidecar writes it itself.

Measured against real WhatsApp: with the sidecar killed in the middle of a burst of 20 messages, each one reached the handlers exactly once, in order, with its id, and with the .NET process killed while holding a message, the next process got that message and the ones after it again, each once and with its id [observed: §14, Phase 1 exit, run 3].

Best effort, because whatsmeow answers them before or whatever the handler does, or because WhatsApp sending them again is not measured:

* `UndecryptableMessageReceived`.
  whatsmeow acks one only after the sidecar recorded it [observed: source, §14 Question 4], but that WhatsApp sends it again after a failed recording is [inferred], so it stays best effort until a live run measures it (ADR 0066).
  The content is recovered by WhatsApp's retry protocol: the sender's resend comes later as a normal, at least once `MessageReceived`.
  It has the same id when the sender is whatsmeow [observed: source] and, for other senders, [inferred].
  The copy the account's own phone sends back holds back its delivery receipt until the sidecar recorded it, but its `peer_msg` receipt goes out regardless [observed: source, §7.2], so it is best effort too.
* `ReceiptReceived` of type `Retry`.
* `ContactUpdated`, `ChatStateChanged` and `MediaRetryReceived`.
* `PresenceChanged`, `ChatPresenceChanged` (typing) and `EphemeralEventsDropped`, below.

Best effort means that a crash at the wrong moment can lose one: once an event is recorded, it reaches your handlers at least once like any other.
Calls and status updates are not delivered at all (§1).

## Handlers must be idempotent

At least once means an event can come again: after your process stops before it acknowledged the event to the sidecar (an ack), and, for a message, even after you acked it, when the sidecar crashes before whatsmeow cleared its buffer of decrypted messages ([ADR 0049](../adr/0049-the-hosted-sample-and-its-fake-mode-check.md)).
For messages, `SessionId` plus `MessageId` is the key to dedupe on (§8.4).
Events without a message id, such as a group change or a session state, need an effect that is safe to repeat: set a value rather than add to it.

A send is idempotent by the same id: the sample echoes with a `MessageId` derived from the message it answers, so a redelivered message is never answered twice ([Messaging](messaging.md)).

## Order and concurrency

* Events of one chat run in order, one at a time.
* Different chats run at the same time, up to 16 at once.
* A session's state changes run in a queue of their own, and so does its history, so history never holds up a live message.
* Presence and typing run on a pipeline of their own that never acks (below).

Nothing is dropped when your handlers are slow: the sidecar keeps every event in its database and sends more as your handlers finish (§8.4).

## When a handler throws

The exception is logged, counted in `whatsmeow.handler.duration` with its `error.type`, and passed to `client.OnHandlerError`.
The event still counts as handled and is not retried: one bad event never blocks the chat.
If you need retries, write them inside the handler.

## Acks and stopping

The library acks every second up to the highest event whose handlers all finished, and again at shutdown.
Disposing the sidecar cancels the handlers' token and waits for them; an event whose handler ended because of that cancel was not handled, and comes again next time.

The sidecar deletes an event once every consumer of its session acked it.
A session that nobody consumes keeps all its events, and in [External mode](external-mode.md) a consumer name that nobody uses anymore keeps its sessions' events: the database grows until you delete the session.

## Presence and typing

Presence and typing are ephemeral: the sidecar never writes them to its database, they are never acknowledged, and never repeated.
The sidecar keeps each session's newest 256 in memory and loses them on restart; a stream that falls behind gets an `EphemeralEventsDropped` with how many it missed ([ADR 0027](../adr/0027-ephemeral-events-presence-and-typing.md)).

## When the sidecar cannot record an event

If writing an event fails, whatsmeow holds back its answer, and WhatsApp sends it again at the session's next login.
In live runs it came again within 1.4 s of each of four logins, and never on a connection that stayed up 9 to 10 minutes [observed: §7.3]; that only a new connection brings it back is [inferred], and whether a connection that stays up longer ever does is [unknown].
So the sidecar resets the connection 5 s after an answer was held back, at most once a minute, backing off to 30 minutes ([ADR 0044](../adr/0044-a-reset-after-a-withheld-answer.md)).
In one live run the session was offline for 1.5 s and the message came again 6.9 s after it was held back [observed: §7.3, one run].
A reset shows up as `Reconnecting`, `Connected` and `CaughtUp`, fails the sends still queued with `NotConnected`, and can leave a send in flight `OutcomeUnknown`.

## Durability

The database runs SQLite in WAL mode with `synchronous(FULL)`, so that a recorded event survives a power loss, not only a crash of the process.
That it does is [inferred: from SQLite's documented WAL durability, not measured here] (§14 question 3).
It costs about 2 to 5 times the commit time of `synchronous(NORMAL)` [observed: §14 question 3], and on a slow disk a commit can take seconds [observed: Windows CI runners, §14 writes under load].
Keep the database on a local disk; the [Security](security.md) guide covers backups.
