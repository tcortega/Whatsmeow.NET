# Errors and telemetry

## Exceptions

Every failure from the sidecar or WhatsApp is a `WhatsmeowException`, never a gRPC `RpcException`, and its `ErrorCode` says which one (§8.7).
Switch on `ErrorCode` or catch the subtype; never parse the message.

| Exception | `ErrorCode` |
|---|---|
| `SidecarUnavailableException` | the sidecar cannot be reached, or `Unauthenticated`, `SidecarShuttingDown` |
| `SidecarVersionMismatchException` | the sidecar's version or protocol does not match the client's |
| `SessionNotFoundException` | `SessionNotFound` |
| `NotLoggedInException` | `NotLoggedIn`: never paired |
| `SessionLoggedOutException` | `LoggedOut` |
| `NotConnectedException` | `NotConnected`, `ConnectionFailed` |
| `PairingException` | `AlreadyPaired`, `PairingInProgress`, `PairingTimedOut`, `PairingFailed` |
| `TemporarilyBannedException` | `TemporarilyBanned`, with `Until` when WhatsApp said |
| `RecipientNotFoundException` | `RecipientNotOnWhatsApp` |
| `RateLimitedException` | `RateLimited` |
| `MediaExpiredException` | `MediaExpired` |
| `OutcomeUnknownException` | `OutcomeUnknown` |
| `WhatsAppErrorException` | `WhatsAppError`, with WhatsApp's numeric `Code` |
| `WhatsmeowValidationException` | `InvalidArgument`: a value of yours the library or the sidecar refused |
| `WhatsmeowException` | `Internal`, `ConsumerReplaced`, `WhatsAppTimedOut`, `NoPushName`, `InviteLinkInvalid`, `PollSecretNotFound`, `Unknown` |

* `OutcomeUnknown` is only for writes: the request may have reached WhatsApp and no answer came.
  It is never retried for you ([Messaging](messaging.md)).
* `WhatsAppTimedOut` is a read WhatsApp did not answer in time: nothing changed, so it is safe to try again.
* `Internal` is the library's own fault, such as a value from the sidecar it cannot read; please report it.
* `RateLimitedException` has no retry delay: whatsmeow reads none [observed: §8.7], and whether WhatsApp sends one is [unknown].

Messages never carry a phone number, a JID's user part, or a URL's query: the sidecar redacts them before they leave it ([ADR 0039](../adr/0039-pii-redaction-of-stderr-and-status-messages.md)).

## Cancellation and timeouts

Cancelling a call's token throws `OperationCanceledException`.
The library sets no deadline on any call, so a call that should not wait forever needs a token that you cancel, `CancelAfter` included.
A send cancelled while it waits for its paced turn sent nothing.
`TimeoutException` comes only from `WaitForAsync`'s own `timeout`.

## Logs

The library logs through `ILogger` (`ILoggerFactory` in `WhatsmeowSidecar.StartAsync`, the host's with DI).
The sidecar's own log lines are forwarded under the category `Whatsmeow.Sidecar`, at info level and above.
A sidecar restart logs `SidecarRestarted`.

JIDs and phone numbers appear only at `Debug` or below, or redacted (§8.8).
In the sidecar's lines, every run of seven or more digits, a phone number or a JID's user part, becomes a token, `~` and 8 hex digits, the same within one sidecar process [observed: `cmd/whatsmeow-net` tests, ADR 0039].
What the sidecar prints outside its logger, a crash's stack trace or a bad flag, is not redacted: a bad flag only echoes your own arguments [observed: ADR 0039], and that a crash's trace holds no WhatsApp data is [inferred].
Your session ids are your own names and appear as given: do not make them phone numbers.

## Traces and metrics

Listen to the names in `WhatsmeowTelemetry`, as the [EchoBot sample](../../dotnet/samples/EchoBot/Program.cs) does with OpenTelemetry:

```csharp
.WithTracing(tracing => tracing.AddSource(WhatsmeowTelemetry.ActivitySourceName))
.WithMetrics(metrics => metrics.AddMeter(WhatsmeowTelemetry.MeterName))
```

Spans ([ADR 0037](../adr/0037-traces-and-metrics.md)):

* `whatsmeow.send`: one message send.
* `whatsmeow.media.upload` and `whatsmeow.media.download`: one transfer.
* `whatsmeow.process`: the handlers of one event, each running inside it, so your handler's spans are its children.
  It is a root span: no context crosses the socket.

Instruments:

* `whatsmeow.messages.sent` and `whatsmeow.messages.received`, counters of messages; a redelivery counts again.
* `whatsmeow.handler.duration`, a histogram in seconds per handler and event type.
* `whatsmeow.dispatcher.queue.length`, the events received and not finished.
* `whatsmeow.errors`, failed calls by `error.type`, but not the ones you cancelled.
* `whatsmeow.sidecar.restarts`, sidecars started again after one exited on its own.

Every span and instrument but the last two carries `whatsmeow.session.id`; `error.type` is the `WhatsmeowErrorCode` name, or an exception's type name, never its message.
No span or metric carries a JID or a phone number.
`WhatsmeowTelemetry`'s docs list every attribute and unit.
