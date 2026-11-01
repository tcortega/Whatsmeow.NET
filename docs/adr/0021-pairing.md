# 0021. Pairing

- Status: Accepted
- Date: 2026-11-01
- Evidence: issue #7, its orchestrator notes and plan review, whatsmeow at the pin, and the tests named below
- Amends: [ADR 0012](0012-iwhatsappclient-interface-exception.md) (the feature groups)

## Context

Issue #7 pairs a session by QR code or by phone code, and calls pairing done only once the session is usable.
ARCHITECTURE §9 sketched the flow; building it against whatsmeow at the pin showed these facts:

- `GetQRChannel` must come before connecting, and refuses a paired device (qrchan.go:221-228).
  Its channel ends with `success` on `PairSuccess`, with `timeout` when the codes run out (it disconnects, with no `Disconnected` event, qrchan.go:77-84) and also on `*events.Disconnected` (qrchan.go:196-197), and with `err-client-outdated`, `err-unexpected-state`, `err-scanned-without-multidevice` or `error` (qrchan.go:154-199).
  `passkey-request` and `passkey-confirmation` are items too (qrchan.go:158-180).
  When its context ends, it closes with no last item and disconnects (qrchan.go:125-131).
- Handlers run in the order they were added (client.go:820-826, 1008-1012).
- An unpaired client whose connect fails with a retryable error dispatches `Disconnected` and does not reconnect, because it has no ID (client.go:550-554, 648).
- whatsmeow saves the device, then dispatches `PairSuccess` (pair.go:148-158, 222-226), and expects the disconnect that follows (pair.go:240).
  WhatsApp then sends a 515, after which whatsmeow reconnects; a failed reconnect is only logged (connectionevents.go:26-38).
- On the login, whatsmeow counts and uploads the pre-keys, then sends the passive IQ, and only then dispatches `Connected`, all on one goroutine (connectionevents.go:187-204).
  A failed upload is only logged (prekeys.go:84-87); `PreKeysUploaded` is set after a successful one (prekeys.go:95) and never cleared.
- `PairPhone` wants the first QR event first (pair-code.go:78-81), checks the number (pair-code.go:97-102), and takes a client type and a display name that WhatsApp validates as "Browser (OS)" (pair-code.go:87-89).
  The phone code lives as long as the QR codes do, about 160 seconds (pair-code.go:83-85).
- A logged-out device is deleted, and connecting it fails with `ErrDeviceDeleted` (store/store.go:286-299, client.go:567-568).
- mautrix-whatsapp makes a new device and client for each login and passes `PairClientChrome, "Chrome (Linux)"` with the push notification on (mautrix/whatsapp@5dd621f, pkg/connector/login.go:157-158, 223).

## Decision

- **API on the client.** `IWhatsAppClient.PairWithQrCodeAsync` and `PairWithPhoneNumberAsync` return `IAsyncEnumerable<PairingUpdate>`: `QrCode`, `PairingCode`, then `Paired`, the last.
  This amends ADR 0012: there is no `Pairing` feature group, because a group would need a second public interface or a class `FakeWhatsAppClient` cannot substitute; whether later slices get groups is decided with Messages.
  `Paired` has no fields until `Jid` exists. After `Paired` the client connects the session again after a sidecar restart, as after `ConnectAsync`.
  Errors: `PairingException` (`AlreadyPaired`, `PairingInProgress`, `PairingTimedOut`, `PairingFailed`), `NotConnectedException` (`ConnectionFailed`), `WhatsmeowValidationException`, `RateLimitedException`, and the session and sidecar exceptions.
- **A new device and client for each Pair call**, as mautrix does: a logged-out device is deleted, and an old client's QR channel would still react to events. The session ignores the events of a client it replaced.
- **The sidecar's lifetime, never the call's**, goes to `GetQRChannel` and to the connect, so the QR channel's own disconnect on cancel cannot race the 515 reconnect.
- **Before the link** the Pair call streams what the QR channel sends.
  `timeout` is `CONNECTION_FAILED` when the session's handler, which runs before the QR channel's, saw `Disconnected`; otherwise it is `PAIRING_TIMED_OUT`.
  Every other last item, passkey ones included, is `PAIRING_FAILED`, logged at warn with the item's event. How often WhatsApp asks for a passkey is [unknown]; the live test measures it, and a two-way pair RPC is additive work if it shows up.
  A `PermanentDisconnect` before the link does not move the state: there is no account yet, and the QR channel ends the pairing.
  Ending before the link records the previous state first, then disconnects, so a `PairSuccess` that still comes records the saved device as `Disconnected`.
- **Phone mode** calls `PairPhone(digits, true, PairClientChrome, "Chrome (Linux)")` on the first QR code, sends the code once and streams no QR codes.
  The QR channel goes on rotating its codes unseen, and its `timeout`, about 160 seconds after the first code, ends the pairing with `PAIRING_TIMED_OUT`: the phone code is usable until then [inferred: pair-code.go:83-85 says the codes and the login socket set the limit; the code's own expiry is unknown]. The live test checks it.
  The number is checked as whatsmeow does before anything connects; an IQ 429 is `RATE_LIMITED`, another IQ error `WHATSAPP_ERROR` with its code.
- **"Usable" is checked at `Connected` after the link**: `PreKeysUploaded` is final by then, so pairing waits for nothing else.
  Without the pre-keys the session is disconnected and `Disconnected` (paired), and Pair fails with `PAIRING_FAILED`.
  A login that does not come within 60 s of the link (`linkTimeout`) ends the same way; mautrix waits 10 s for the 515 and 5 s for the pre-keys (login.go:398, 432), and the live test measures the real time.
  A stopping state after the link (`LoggedOut`, `TempBanned`, ...) stays the session's state (ARCHITECTURE §13 invariant 4), and Pair fails with `PAIRING_FAILED`.
- **After the link, a cancel never disconnects**: the wait for the login moves to a goroutine the Service owns and `Close` waits for, and the session ends `Connected` or `Disconnected` without the call.
- **The fake WhatsApp** gains `GetQRChannel`, `PairPhone` and `HasUploadedPreKeys`, and the controls `RotateQrCode`, `ExpireQrCodes`, `DropConnection`, `FailNextPreKeyUpload` and `FailNextPairPhone`; `LinkDevice` while pairing also ends the QR channel with `success` and logs in after the 515.
  Its `FailNext*` scripts belong to the session, not its client, so a script set before a Pair call applies to the client that call makes.
  `PreKeysUploaded` is a field of whatsmeow's client, so the slice reads it there and asks other clients (the fake, the tests' stub) through `HasUploadedPreKeys`.
- **`Whatsmeow.LiveTests`** pairs a real phone with the fake-mode tests' own checks (`IntegrationTests/Pairing/PairingChecks.cs`, through a project reference).
  Its tests are `[LiveFact]`, explicit unless `WHATSMEOW_LIVE_PHONE_NUMBER` is set, so without it nothing starts and the run passes with them not run [observed: xunit.v3 4.0.2 on SDK 10.0.403 reports an explicit test as not run and exits 0].
  `build/ci.sh` runs `dotnet test` without that variable, and fails when `LiveFact` or `Explicit` appear outside the project or a shipped project depends on QRCoder.
  The number is a secret: it lives in `~/secrets.env`, and the live sidecar log redacts it, because whatsmeow logs every node it sends at debug level (client.go:990) and PairPhone's carries it.

## Consequences

- A sidecar restart in the middle of pairing finds a never-linked session `Created`, even one that was `LoggedOut` before, because a pairing session's stop state is not stored; a linked one comes back `Disconnected`.
- Two races stay [inferred: from the code paths, not reproduced]: a cancel between WhatsApp's pair success and whatsmeow's `PairSuccess` leaves the device linked and the session `Disconnected`, and a 515 reconnect that completes only after the 60 s bound leaves a socket open under `Disconnected`.
- A live run with nothing scanned confirmed the QR side: 6 codes (60 s, then 20 s each), `PAIRING_TIMED_OUT` at 160 s, and real codes in the format the shared checks expect [observed: ARCHITECTURE §14].
  Which display names WhatsApp accepts, the time from the link to `Connected`, whether passkeys show up, and the phone code's real lifetime need the phone, and are recorded in ARCHITECTURE §14 once measured.
