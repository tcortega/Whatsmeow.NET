# Media

Images, videos, audio, voice notes, documents and stickers.
Sending one is two steps: upload the file, which gives a `MediaRef`, then send a message with it.

```csharp
var image = await client.UploadMediaFileAsync("cat.jpg", MediaType.Image, cancellationToken);
_ = await client.SendImageAsync(chat, image, new ImageMetadata { MimeType = "image/jpeg", Caption = "a cat" }, cancellationToken: cancellationToken);
```

## Uploads and downloads

`UploadMediaFileAsync` takes a path and `UploadMediaAsync` a `Stream`.
Downloads mirror them: `DownloadMediaFileAsync(media, path)` and `DownloadMediaAsync(media, stream)`.

Set `WhatsmeowSidecarOptions.MediaRoot` to a directory both your app and the sidecar can use, and a path under it is handed to the sidecar as a path, so no file bytes cross the socket.
Any other path, and every stream, travels in 64 KiB chunks (§7.6).
The sidecar refuses a path that leaves the root through `..` or a symlink.

A download is checked as a whole before any byte of it reaches you, and a download to a path is written next to it and renamed into place only once checked: the file holds the verified media or what it held before (§7.6).
A `MediaRef` is enough to download later, after a restart too, or to send the same file again without uploading it.
Whether WhatsApp accepts a `MediaRef` whose upload expired is [unknown] (ADR 0033).

An upload is tried 3 times and a download tries each media host with up to 5 retries, but neither has a total timeout: bound them with your cancellation token.
Media calls need the session online.
WhatsApp's size limits are [unknown]; whatsmeow enforces none (§14 Media).

## The kinds

| Kind | Upload as | Send with |
|---|---|---|
| image | `MediaType.Image` | `SendImageAsync`, `ImageMetadata` |
| video | `MediaType.Video` | `SendVideoAsync`, `VideoMetadata` |
| audio or voice note | `MediaType.Audio` | `SendAudioAsync`, `AudioMetadata` |
| document | `MediaType.Document` | `SendDocumentAsync`, `DocumentMetadata` |
| sticker | `MediaType.Image` | `SendStickerAsync`, `StickerMetadata` |

The metadata goes out exactly as you give it: the library never decodes a file, so a thumbnail, the dimensions, a duration or a waveform are yours to compute, and nothing checks them against the file (§7.5).
Whether phones show metadata that does not match the file is [unknown] (ADR 0033).

## Voice notes

A voice note is an audio message with `AudioMetadata.IsVoiceNote` set, which sets the message's push to talk flag (`PTT`).
Encode it as Ogg Opus and say so: `MimeType = "audio/ogg; codecs=opus"`.
That phones need that format is [inferred], not measured here: a voice note in another format is reported to play on WhatsApp Web and stay silent on phones.
The library does not check the format.
Which range phones expect for a `Waveform` is [unknown]; 64 values peaking near 100 is the common choice.

## Receiving media

A received media message is a `MessageReceived` whose content is `ImageContent`, `VideoContent`, `AudioContent`, `DocumentContent` or `StickerContent`, each with its `Media` and its metadata.
Images, videos and audio also have `IsViewOnce`.
A companion device can get a view once message without its content: it arrives as `UndecryptableMessageReceived` with `Unavailable` set to `ViewOnce`, and whether the phone ever sends it is [unknown] (§14 Media messages).

## Expired media

WhatsApp deletes media from its servers after a while, and a download then fails with `MediaExpiredException`.
`RequestMediaRetryAsync(id, source, media)` asks the sender's phone to upload it again, and the answer comes as a `MediaRetryReceived` event: `Reuploaded` with a `MediaRef` to download, or `Failed` with the reason.
It is best effort: the phone may never answer, and whatsmeow acknowledges the answer whatever your handler does (§7.6).
