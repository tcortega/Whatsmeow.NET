# 0033. Media messages: one SendMedia from a MediaRef and the caller's metadata, typed content mapped in .NET

- Status: Accepted
- Date: 2026-11-02
- Evidence: issue #29, its plan review, ARCHITECTURE §14 "Measured for Media messages (#29)", and the tests named below
- Uses: [ADR 0025](0025-messaging-and-receipts.md) (the send queue and records), [ADR 0026](0026-media-transfer-and-media-retry.md) (`MediaRef`, upload and download)

## Context

Issue #29 sends and receives image, video, audio, voice note, document and sticker messages, shows view-once media, and marks the "unavailable" placeholder a companion device gets for a view-once message.
#28 already moves the bytes and returns a `MediaRef`; the message carries metadata whatsmeow never makes (§1, §9 Media send).
Reading whatsmeow at the pin and mautrix-whatsapp `5dd621f` for it showed facts the design had to follow [observed: ARCHITECTURE §14 "Measured for Media messages (#29)"]:

- The five waE2E media messages have no proto2 required field, so a send needs no `CheckInitialized`.
- whatsmeow derives the keys from the message's type, a sticker's from the image keys (download.go:95-106): a `MediaRef` of another type cannot be decrypted by the recipient [inferred].
- whatsmeow dispatches each decrypted message through `UnwrapRaw`, which removes the view-once and document-with-caption wrappers and keeps only flags (types/events/events.go:377-425; message.go:1172-1178). The contract dropped the flags.
- An `<unavailable>` stanza is acked, asked of the phone, then dispatched as `UndecryptableMessage{IsUnavailable, UnavailableType}`, `"view_once"` or none (message.go:324-333); an skmsg without its sender key is unavailable too, without a type (message.go:419-436).
- mautrix builds the messages from the upload and the metadata (pkg/msgconv/from-matrix.go:220-340), always sets `PTT`, wraps a captioned document in `DocumentWithCaptionMessage`, and sets neither `StreamingSidecar` nor `MediaKeyTimestamp`; it reads view-once from the wrapper flag or the message's own `viewOnce` (pkg/connector/events.go:131-132, pkg/msgconv/wa-media.go:52-53), and shows an unavailable view-once as final (pkg/connector/events.go:417-423).

## Decision

- **One RPC.** `MessageService.SendMedia` takes the `MediaRef` and one metadata case (`ImageMetadata`, `VideoMetadata`, `AudioMetadata`, `DocumentMetadata`, `StickerMetadata`) and returns what `SendText` returns.
  The sidecar builds the waE2E message, as it does for text, so a whatsmeow proto change stays in Go (§1); it then goes through the same `send`: the same queue, flight, `sent_message` record and classification, so one id is one message whatever its kind (ADR 0025). No schema step: the record does not keep the kind.
- **The shapes are mautrix's**, with the caller's metadata unchanged and what is empty left out: `PTT` always set, false too [inferred: whether a receiver tells an unset `PTT` from false is unknown]; a document with a caption wrapped in a `DocumentWithCaptionMessage`; no `MediaKeyTimestamp`.
  Unlike mautrix, the `MediaRef`'s streaming sidecar goes into a video, as the issue asks and RESEARCH "Media should travel" says, and into an audio that carries one (a received one forwarded); whatsmeow makes one for video only (upload.go:141-151).
  A duration is sent in whole seconds, the fraction dropped as mautrix drops it (from-matrix.go:285).
  GIF playback, animated stickers, a document's title and page count are left out; Raw (#30) reaches them.
- **Validation.** A `MediaRef` needs what a download needs (a direct path from "/", a 32-byte key and hashes) and the type its kind takes (IMAGE for an image or a sticker, VIDEO, AUDIO, DOCUMENT), else `INVALID_ARGUMENT` before anything is recorded; so are no metadata case and a duration outside 0 to 2^32-1 seconds.
  Nothing else is checked, and nothing is decoded (§1): a voice note goes out whatever its mimetype.
  The check is a copy of the media slice's `checkRef` in messaging, because no slice imports another.
- **Receive in .NET.** `MessageEvent` keeps carrying the unwrapped message and gains `view_once`, whatsmeow's `IsViewOnce`; .NET maps the message to `ImageContent`, `VideoContent`, `AudioContent`, `DocumentContent` or `StickerContent`, each a `MediaRef` built from the message's fields and its metadata record, and the first three with `IsViewOnce`, the wrapper flag or the message's own, as mautrix reads it.
  Other kinds stay `UnknownContent`. A missing field is null or empty, never an error, so the content arrives.
- **Unavailable.** `UndecryptableMessageEvent` gains `unavailable` and `unavailable_type` (`UNAVAILABLE_TYPE_VIEW_ONCE`, else unspecified); .NET's `UndecryptableMessageReceived.Unavailable` is null for a ciphertext that did not decrypt and `UnavailableType.ViewOnce` or `Unknown` otherwise.
  It is not a required member, so a record built before it still compiles.
  The docs say a copy of an unavailable view-once may or may not come: whatsmeow asks the phone for it (message.go:327-330) and dispatches an answer as a normal message (message.go:870-877) [observed: whatsmeow at the pin], mautrix shows it as final [observed: pkg/connector/events.go:417-423], and whether the phone answers for a view-once is [unknown], #36 checks it live.
- **.NET API.** Flat members (ADR 0025): `SendImageAsync`, `SendVideoAsync`, `SendAudioAsync` (a voice note is `AudioMetadata.IsVoiceNote`), `SendDocumentAsync` and `SendStickerAsync`, each `(Jid chat, MediaRef, <Kind>Metadata, MessageId? id, CancellationToken)`.
  The metadata records are in `Whatsmeow.Messaging`, shared by the sends and the content, so forwarding is `SendImageAsync(chat, content.Media, content.Metadata)`; every member is optional, and the byte members (`JpegThumbnail`, `PngThumbnail`, `Waveform`) compare by content, as `MediaRef` does (tests.md).
  A negative width, height or `MediaRef.FileLength`, which the contract cannot carry, and a `MediaType` that is not one of its four, are `ArgumentOutOfRangeException` in .NET.
  Every argument failure is in the returned task, as for `SendTextAsync`.
- **The fake WhatsApp** delivers a raw message (`DeliverMessageRequest.message`) through whatsmeow's own `UnwrapRaw`, an unavailable one with its type, and makes a video's streaming sidecar as whatsmeow does (upload.go:156-181).

## Consequences

- Whether phones render the metadata as sent (the waveform range, a voice note's mimetype, a missing streaming sidecar or `MediaKeyTimestamp`), whether a re-sent `MediaRef` with an expired direct path is accepted, and the size limits are [unknown] (RESEARCH items 12-14); the Phase 2 exit live run (#36) records them.
- #30 adds the other unwrap flags to `MessageEvent` from field 4, and the `Raw` hatch.
- Tests: `internal/messaging/media_test.go` (each kind's message, validation, one queue with text, a repeat across kinds), `internal/events` (view-once and unavailable), `internal/fakewhatsapp` (the raw unwrap, unavailable, the streaming sidecar), and `Whatsmeow.IntegrationTests/Messaging/MediaMessageTests.cs` (the send and receive FullCycles, forwarding without an upload, view-once, unavailable, a repeat as text, validation).
