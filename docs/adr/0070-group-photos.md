# 0070. A group's photo is set from a stream of chunks and removed by its own call

- Status: Proposed
- Date: 2026-11-11
- Evidence: whatsmeow at the pin `c88388f983b4` (lines below), the live tests' control (`sidecar/internal/groups/livetest_on.go`), #36's live runs at that pin (ARCHITECTURE §14), and the media slice (`proto/whatsmeow/v1/media.proto`, `sidecar/internal/media/upload.go`)
- Would amend: [ADR 0030](0030-groups-participants-settings-invites-and-group-events.md) (group photos join the groups slice), [ADR 0060](0060-a-live-test-control-and-a-fresh-companion.md) (the live tests set the photo through the contract, and the control's `SetGroupPhoto` goes), `sidecar/parity.excluded` (its `SetGroupPhoto` line goes)

## Context

An app can read a group's photo with `GetProfilePictureAsync` and has no call to set one [observed: `IWhatsAppClient.cs`, `contact.proto`, `group.proto`].
ADR 0030 left group photos out of the groups slice, and `sidecar/parity.excluded` lists `SetGroupPhoto` as called only by the live tests' control [observed: both].

Reading whatsmeow at the pin and the sidecar for it showed facts the design has to follow:

- `SetGroupPhoto(ctx, jid, avatar []byte)` puts the bytes themselves in a `picture` node of a `w:profile:picture` IQ set, and returns the picture id from the answer (group.go:325-354) [observed: source].
  Nothing in it uploads to WhatsApp's media servers, and the IQ has no field that names an upload [observed: group.go:326-340].
- nil bytes remove the photo, and whatsmeow then answers `"remove"`, not an id (group.go:345-347) [observed: source].
- A 406 answer is `wrapIQError(ErrInvalidImageFormat, err)` (group.go:341-342), so both `errors.Is(err, ErrInvalidImageFormat)` and `errors.As(err, &iqErr)` hold (errors.go:158-177) [observed: source].
  The groups slice's `classify` would make it `WHATSAPP_ERROR` with code 406 today [observed: groups.go:640-647].
- whatsmeow checks neither the format nor the size; its comment says only "The avatar should be a JPEG photo, other formats may be rejected with ErrInvalidImageFormat" (group.go:322-324) [observed: source].
  Which images WhatsApp refuses (format, size, dimensions), and with which code, is [unknown].
- A success answer without a picture id is an untyped `fmt.Errorf` (group.go:349-352) [observed: source]; the photo may have been set by then [inferred].
- whatsmeow's frame socket refuses a frame of `1 << 24` bytes or more (socket/constants.go:35, framesocket.go:130-131), and the noise socket advanced its write counter before that check (noisesocket.go:97-101) [observed: source].
  So a photo near 16 MiB would fail in whatsmeow and could leave the connection's encryption out of step [inferred: not measured].
- The live control already calls it: it reads a JPEG at an absolute path on the sidecar's machine, calls `SetGroupPhoto`, classifies its error as a write (`classify(..., true)`), and answers a removal with no id (`livetest_on.go:40-73`) [observed: source].
  In #36's Run B and its rerun at this pin, the set and the removal both passed, and the branch waits for each picture change on the member's device [observed: ARCHITECTURE §14 "Branches 3-4 (picture) passed", `LiveGroupPhoto.cs`].
- A member learns of the change from a `picture` notification, which the contacts slice maps to `ContactEvent.picture`, an ungated event (contacts/event.go:15-37; ARCHITECTURE §14) [observed: source].
- ARCHITECTURE §13 invariant 8: "No whole-file media in a single gRPC message; use paths or chunks."
  The gRPC message cap is 16 MiB on both sides (`server.go:32`, `LocalChannel.cs:20`) [observed: source], so a photo would often fit in one message, which the invariant forbids anyway.
- `UploadMedia` takes a header, then either a path inside the media root or chunks of at most 65536 bytes (media.proto) [observed: source].
  The root is the media slice's own `*os.Root` (media.go:57-93), and no slice imports another (ADR 0033) [observed: source].
  In External mode a path works only when the root is mounted at the same absolute path on both sides (`docs/guides/external-mode.md`); chunks always work [observed: guide; `Media_NoSharedRoot_TravelsInChunksEvenFromTheSidecarsRoot`].
- `v1.0.0-rc.1` and `v1.0.0-rc.2` are tagged, and `Protocol` is still 1 (`sidecar.go:15`) [observed: `git tag`, source], so under ADR 0015 an additive change now increments it.
  The client accepts a sidecar whose protocol is in `[MinProtocol, MaxProtocol]`, both 1 (`WhatsmeowSidecar.cs:25-27`, ADR 0046) [observed: source].
- A new `IWhatsAppClient` member goes to `PublicAPI.Unshipped.txt`; from v1.0.0 a minor version that adds one also adds its CP0006 suppression (ADR 0055) [observed: ADR 0055].

## Options considered

1. **Upload first, then pass the `MediaRef`.**
   Rejected: the facts above rule it out.
   The IQ carries the plaintext and names no upload, so the sidecar would have to download and decrypt the `MediaRef` back into the bytes the client already had.
   That is an upload to WhatsApp's media servers that nothing references, a download, and their failure modes, for no gain.
2. **A unary call with a path, as the live control does.**
   Rejected: in External mode without a shared root the sidecar cannot read the client's file, and a path outside the media root would let a client make the sidecar read any file its user can, which the media slice refuses [observed: `inRoot`, media.go:126-140].
3. **A unary call with the bytes.**
   Rejected: it breaks invariant 8, which needs an ADR of its own first, and buys nothing chunks do not.
4. **A client stream: a header, then chunks of at most 65536 bytes, the chunk half of `UploadMedia`.**
   Chosen.
   It works in every mode, keeps invariant 8, and reuses the upload's wire shape and the .NET chunk writer.
5. **As 4, plus `UploadMedia`'s path half (a path inside the media root).**
   Not now.
   whatsmeow takes the photo as one `[]byte`, so the sidecar holds it whole in memory however it arrives [observed: group.go:325], and a path saves only one copy over a local socket.
   It would also need the media slice's root handed to the groups slice.
   A `path` field in the header is additive later, when a real need shows.
6. **The call in `MediaService`.**
   Rejected: it is a group write.
   Its errors (an admin-only change, `OUTCOME_UNKNOWN` when the answer is lost) are the groups slice's `classify`.
7. **Removal as an empty stream.**
   Rejected: an empty `Stream`, a caller's bug, would silently remove the photo.
   Removal is its own unary call, and an empty photo is `INVALID_ARGUMENT`, as the live control refuses an empty file today (`livetest_on.go:53-55`) [observed: source].

## Decision

- **Contract.** In `group.proto`, two RPCs on `GroupService`:

  ```proto
  // SetGroupPhoto sets the group's photo. The first message is the header; the photo follows in chunks and the
  // client ends the stream. Members get a ContactEvent with the picture.
  // Errors: as CreateGroup; INVALID_ARGUMENT (a missing or repeated header, no chunk, a chunk over 65536 bytes, a
  // photo over the cap, field "chunk"; field "group"; WhatsApp refused the image, field "photo").
  rpc SetGroupPhoto(stream SetGroupPhotoRequest) returns (SetGroupPhotoResponse);

  // RemoveGroupPhoto removes the group's photo.
  // Errors: as CreateGroup; INVALID_ARGUMENT (field "group").
  rpc RemoveGroupPhoto(RemoveGroupPhotoRequest) returns (RemoveGroupPhotoResponse);

  message SetGroupPhotoRequest {
    message Header {
      string session_id = 1;
      string group = 2;
    }

    oneof part {
      Header header = 1;
      bytes chunk = 2;
    }
  }

  message SetGroupPhotoResponse {
    // The new photo's id, as ContactEvent.picture carries it.
    string picture_id = 1;
  }

  message RemoveGroupPhotoRequest {
    string session_id = 1;
    string group = 2;
  }

  message RemoveGroupPhotoResponse {}
  ```

  The comments above are the draft's shape; the implementation words them by `docs/guidelines/proto.md`.
- **`protocol` becomes 2.** Both RPCs land in one change, so one increment (ADR 0015); `buf breaking` passes, since nothing existing changes.
  `MaxProtocol` becomes 2.
  Whether `MinProtocol` stays 1 is the maintainer's call:
  at 1, a client in External mode still accepts a protocol 1 sidecar, whose two photo calls then fail as any call it does not serve (`UNIMPLEMENTED`, with no `ErrorInfo`) [inferred: how `RpcErrors` reports that was not read for this draft];
  at 2, it refuses that sidecar at start with `SidecarVersionMismatchException` (ADR 0046), for every app, photos or not.
  The draft recommends 1: no app that never sets a photo is made to upgrade its sidecar.
- **The cap.** The sidecar refuses a photo over 15 MiB as the chunks arrive, `INVALID_ARGUMENT` field `"chunk"`, before whatsmeow sees it.
  The number only keeps the IQ under whatsmeow's 16 MiB frame, with room for the IQ's own nodes and the cipher's tag [inferred: the overhead is tens of bytes, not measured].
  WhatsApp's own limit is [unknown]; a lower cap waits for a measurement.
- **The sidecar.** `SetGroupPhoto` joins the groups slice's `Client` interface, and the live control's `photos` interface goes with the control's RPC (below).
  The chunks gather in memory up to the cap, with no spool file, since whatsmeow needs one `[]byte`.
  The chunk size, 65536, is a copy of the media slice's constant, as ADR 0033 copied `checkRef`, because no slice imports another.
  Removal calls `SetGroupPhoto` with nil and answers no id, as the live control does.
- **Errors.** As every group write (ADR 0030), through `classify(..., true)`, with two cases ahead of it:
  - `ErrInvalidImageFormat` is `INVALID_ARGUMENT`, field `"photo"`, checked before the `IQError` case it also unwraps to.
    The fix is the caller's input, and whatsmeow names the error as one about the input; keeping `WHATSAPP_ERROR` 406 was the option rejected.
    Unlike the other `INVALID_ARGUMENT` cases, WhatsApp was asked; it changed nothing [inferred: a 406 is a refusal].
  - An error `classify` leaves untyped is `OUTCOME_UNKNOWN` here, not `INTERNAL`.
    At the pin, the only untyped error after WhatsApp answered is the missing picture id (group.go:349-352), after which the photo may be set; the untyped ones before the send (a marshal failure, client.go:987-990, a socket error) are rare [inferred].
    A caller told `OUTCOME_UNKNOWN` reads the photo back; one told `INTERNAL` would wrongly believe nothing changed.
    The status keeps only the error's Go type, as `classify` does today.
  - Which code WhatsApp gives a member who is not an admin of a locked group, and what it answers a removal from a group with no photo, are [unknown]; they reach the caller as `WHATSAPP_ERROR` with the code, as the other info changes do.
- **.NET.** Two flat members on `IWhatsAppClient` (ADR 0025):
  - `Task<string> SetGroupPhotoAsync(Jid group, Stream photo, CancellationToken cancellationToken = default)` reads the stream to its end in 64 KiB chunks with the media upload's chunk writer, does not dispose it, and returns WhatsApp's picture id, the one `ContactChange.Picture` and `ProfilePicture.Set` carry.
  - `Task RemoveGroupPhotoAsync(Jid group, CancellationToken cancellationToken = default)`.
  - No path overload: with no path in the contract it would only open the file, which `File.OpenRead` does.
  - Exceptions as for `SetGroupNameAsync`, plus `WhatsmeowValidationException` for an empty, oversize or refused photo.
  - The members go to `PublicAPI.Unshipped.txt`; if this lands after v1.0.0, each adds its CP0006 suppression (ADR 0055).
  - `FakeWhatsAppClient` implements both, recording the photo's bytes as `UploadMediaAsync` records its content, and answering a picture id.
- **The fake WhatsApp.** Its `Client` gains `SetGroupPhoto`: it refuses a non-admin of a locked group as its other info changes do, refuses bytes that do not start with the JPEG marker `FF D8 FF` with a 406 wrapped as whatsmeow wraps it [inferred: models whatsmeow's comment; WhatsApp's own rule is unknown], stores the picture so `GetProfilePictureInfo` answers it, sends each member a `picture` notification as `DeliverContactNotification` does, and answers a removal with `"remove"`.
- **Tests.**
  - Go: `internal/groups` over bufconn through the real `server.Server` (the header and chunk rules, the cap, the 406 as `INVALID_ARGUMENT` `"photo"`, the untyped error as `OUTCOME_UNKNOWN`, a removal with no id); `internal/fakewhatsapp/groups_test.go`; `internal/server/reasons_test.go` if a reason's mapping changes.
  - FullCycle on the shared `FakeSidecar`, in `Groups/`: set from a stream, the member's `ContactUpdated` picture carries the returned id, `GetProfilePictureAsync` reads it, the removal brings a picture with no id; a non-JPEG is `WhatsmeowValidationException` and changes nothing.
  - Live: `LiveGroupPhoto` keeps its assertions and calls `SetGroupPhotoAsync` and `RemoveGroupPhotoAsync` instead of the control, from the same `group-photo.jpg`.
- **What it amends.**
  - ADR 0030: group photos leave its "not here" list.
  - ADR 0060: the live tests set the photo through the contract, so `LiveTestService`, whose one RPC is `SetGroupPhoto` [observed: `livetest.proto`], goes with its messages and the groups slice's `livetest_*` files; `LiveContactService` (ADR 0063) stays.
    `tools/check-repo`'s allowance of the `livetest` tag in the groups directory goes too, unless the implementation finds another use for it [unknown: not checked for this draft].
  - `sidecar/parity.excluded`: the `SetGroupPhoto` line goes, so `parity-report` lists it as covered.

## Consequences

- An app sets and removes a group's photo in every mode, External without a shared root included.
- Every photo crosses the socket once and is held whole in the sidecar's memory for the call, up to 15 MiB per call in flight.
- The library never decodes, resizes or crops the image, as it never decodes media (`docs/guides/media.md`, §7.5); WhatsApp's accepted formats, sizes and dimensions are [unknown], and the guide says so.
- Two upstream reports would help: a typed error for the missing picture id, and a frame size check before the noise socket's counter moves [inferred].
- Implementation waits for the maintainer to accept this ADR; join-request approval, also left out by ADR 0030, is a separate, later decision.
