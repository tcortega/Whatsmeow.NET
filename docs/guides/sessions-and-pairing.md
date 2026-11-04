# Sessions and pairing

A session is one WhatsApp account, linked as a companion device the way WhatsApp Web is.
One sidecar holds as many sessions as you like, all in one database.

## Sessions

You choose each session's id, and it stays the same across pairings:

```csharp
var client = await sidecar.Sessions.GetOrCreateAsync(SessionId.From("support"), cancellationToken);
```

The `IWhatsAppClient` you get back is the whole API for that account: pairing, connecting, events, sending, and the rest.
`sidecar.Sessions.ListAsync` lists the sessions with their state and, once paired, their own JID and LID; `DeleteAsync` deletes one with its keys.

Do not use a phone number as a session id.
The id is your own name and is never redacted: it appears as given in log lines and on every span and metric ([Errors and telemetry](errors-and-telemetry.md)).

## States

`client.WatchStateAsync()` yields the current state first, then every change; the same changes also arrive as `SessionStateChanged` events.

| State | Means |
|---|---|
| `Created` | never paired |
| `Pairing` | a pairing call is open |
| `Connecting`, `Connected`, `CaughtUp` | logging in, logged in, logged in with the offline messages received |
| `Reconnecting` | the connection dropped and whatsmeow is reconnecting by itself |
| `Disconnected` | paired but offline: after `DisconnectAsync`, or after the sidecar restarted |
| `LoggedOut` | unlinked; `Code` is 0 after your own `LogoutAsync`, else WhatsApp's reason, such as 401 |
| `Replaced` | another client logged in with the same device |
| `TemporarilyBanned` | temporarily banned, until `Until` when WhatsApp said |
| `Outdated` | WhatsApp refused this client version |
| `Error` | any other permanent failure, with `Code` and `Message` |

The last five stop the session and stay as they are across restarts (§7.4).
`LoggedOut` can be paired again; the others need you to look at what happened.

The sidecar never connects a session by itself.
`ConnectAsync` connects a paired session, and from then on the library keeps it connected: if the sidecar crashes, the client starts a new one and connects again every session you had connected that came back `Disconnected` (§8.5).
When your own process starts again, it connects them again: call `ConnectAsync`, or let the host do it ([Hosting](hosting.md)).
`DisconnectAsync` takes a session offline and keeps it paired; `LogoutAsync` unlinks the device and deletes its keys.

## Pairing

Pairing works from `Created` or `LoggedOut`; any other state fails with `AlreadyPaired`, and a second call while one is open fails with `PairingInProgress`.

By QR code, as in the [quick start](../../README.md#quick-start):

```csharp
await foreach (var update in client.PairWithQrCodeAsync(cancellationToken))
{
	if (update is PairingUpdate.QrCode code)
		ShowAsQrCode(code.Code);
}
```

The phone scans it from WhatsApp, Linked devices, Link a device.
Against real WhatsApp with nothing scanned, whatsmeow showed 6 codes, the first for 60 s and then one every 20 s, and the call ended with `PairingTimedOut` at 160 s [observed: §14 Pairing, 2026-11-01].

Never write a code's text to a log: it carries the new device's secret.
Draw it, as the samples do with QRCoder.

By phone number, the phone types a code instead:

```csharp
await foreach (var update in client.PairWithPhoneNumberAsync("+55 11 99999 9999", cancellationToken))
{
	if (update is PairingUpdate.PairingCode code)
		Show(code.Code);
}
```

The code comes once.
Pairing by phone number passes against the fake WhatsApp in CI, but no live run has paired a real phone this way yet, and how long the code lives is [unknown] (§14 Pairing).

Both end with `PairingUpdate.Paired`, once the device is linked and logged in.
At that point the session is `Connected` and its events are flowing: there is no `ConnectAsync` to call after pairing ([ADR 0052](../adr/0052-events-start-when-pairing-yields-paired.md)).
Register your handlers before you pair, so they see every event.

After the first pairing, keep the session online until it is `CaughtUp`.
A device taken offline about a second after pairing stayed "connecting" on the phone, and one held online was `CaughtUp` 5 s after `Connected` [observed: §9, 2026-11-01]; that a new device has to stay online through its first sync is [inferred].

Cancelling before the phone links the device puts the previous state back.
After the link, a cancel does not stop the login, which goes on without your call; `DisconnectAsync` stops it, and its events then start at your next `ConnectAsync`.
Failures are `PairingException` with `PairingTimedOut`, `PairingFailed` or `ConnectionFailed` ([Errors](errors-and-telemetry.md)).
