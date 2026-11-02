# 0037. Traces and metrics: one ActivitySource, a meter per sidecar from an IMeterFactory parameter, our own names

- Status: Accepted
- Date: 2026-11-02
- Evidence: issue #40, its plan review and the PR review; throwaway measurements at SDK 10.0.403 with Microsoft.Extensions.* 10.0.14, listed below; `Telemetry/SpanTests.cs`, `Telemetry/MeterTests.cs`, `Telemetry/TelemetryCrashTests.cs`, `Hosting/HostingTests.HostWithAMeterFactory_TheSidecarsMeterComesFromIt`, `DispatcherDepthTests`, `ErrorCounterTests`, `Whatsmeow.Testing.Tests/TelemetryTests.cs`
- Amends: issue #40's "an `IMeterFactory` from DI or the options", now from DI or a `StartAsync` parameter

## Context

ARCHITECTURE §8.8 asks for one static `ActivitySource("Whatsmeow.Client")` with spans for sends, media transfers and event processing, and one `Meter("Whatsmeow.Client")` through `IMeterFactory` for messages sent and received, handler duration, dispatcher queue depth, errors and restarts, with no JID or phone number in any tag.
What .NET and the conventions do decided the design:

- An `IMeterFactory?` property on an options class bound with `EnableConfigurationBindingGenerator` fails the library build with SYSLIB1100 ("Cannot create instance of type 'System.Diagnostics.Metrics.IMeterFactory'") and SYSLIB1101 ("Property 'MeterFactory' ... is not supported") as errors [observed: scratch library with `IsAotCompatible`, `TreatWarningsAsErrors` and `BindConfiguration`].
  `NoWarn` is not allowed here (WMN0003).
- `Host.CreateApplicationBuilder` and `Host.CreateEmptyApplicationBuilder` both register `DefaultMeterFactory`; a plain `ServiceCollection` has none until `AddMetrics()` [observed: scratch host].
- `IMeterFactory.Create` returns the same `Meter` for the same options, with `Scope` set to the factory, and the factory disposes its meters only with the container [observed: scratch host; the disposal from the .NET metrics docs].
  A meter's second `CreateCounter` of a name returns the same instrument; a second observable instrument of a name is a second instrument, and a `MetricCollector` follows the first one only [observed: same scratch].
- `StartActivity` returns null while no listener samples the source [observed: scratch].
  With `parentContext: default` it still takes `Activity.Current` as the parent; with `Activity.Current` set to null first it starts a root span [observed: scratch].
- The session's event loops start inside the application's first `ConnectAsync` (or its pairing's `Paired`, [ADR 0052](0052-events-start-when-pairing-yields-paired.md)), so they inherit its `ExecutionContext`: a handler saw the application's span as `Activity.Current` while nothing listened to the library's source [observed: `Event_WithOnlyTheApplicationListening_AHandlerRunsOutsideTheSpanItsSessionConnectedIn` red before the loops started with no flowed context].
- The OpenTelemetry messaging conventions are at Development stability, and their spans require `messaging.destination.name`, which here is the chat JID [observed: opentelemetry.io semconv messaging spans and metrics pages].
  `error.type` is stable: set only on failure, low cardinality, an exception's canonical type name when the value is a type.
  On a failure the span status should be `Error`; repeating the status code or `error.type` in the status description is not recommended, and with an exception the description should be its message [observed: semantic-conventions `docs/general/recording-errors.md`].

## Decision

- **Names.** `WhatsmeowTelemetry.ActivitySourceName` and `WhatsmeowTelemetry.MeterName`, both `"Whatsmeow.Client"`, are public constants in namespace `Whatsmeow` (ADR 0018); the class's docs list every span and instrument with its unit and meaning.
  Spans and instruments use our own `whatsmeow.*` names and tags, not the messaging conventions, because those are not stable and would need the JID; the one convention reused is `error.type`.
  `error.type` is the `WhatsmeowErrorCode` name of a `WhatsmeowException`, and the full type name of any other exception; never a message.
  `whatsmeow.session.id` is the application's own session id, allowed by the issue; no tag the library makes carries a JID or a phone number.
  Its cardinality is the number of sessions, on every span and every instrument but the error and restart counts; a session id that is a phone number puts that number in all of them, which the `WhatsmeowTelemetry` docs say.
- **The meter.** One per `WhatsmeowSidecar`, never a static: from the `IMeterFactory` the sidecar was started with, which owns it, or else a `Meter` of the same name the sidecar creates and disposes.
  The factory is a parameter, `StartAsync(options, loggerFactory, meterFactory, ct)`, as the logger factory is, because an options property cannot build; the hosting package passes the container's.
- **The source.** One static `ActivitySource` with the client's version.
  Each span's tags are set only when `StartActivity` returned one, so no listener means no activity and no tag work.
- **Spans.** `whatsmeow.send` (client kind: no context crosses the socket) around each of the ten sends (text, the five media kinds, raw, and since #30 reaction, edit and revoke), with the session id, the message id and the message type; `whatsmeow.media.upload` and `whatsmeow.media.download` (client) around each transfer, with the session id, the media type and `whatsmeow.media.size`, the plaintext's length, known up front for a download and from the result for an upload.
  A failure sets `error.type` and the `Error` status with no description; a success sets neither.
  The description is left empty on purpose: the conventions advise against repeating `error.type` there, and the exception's message they suggest instead is left out for PII reasons, because no message is ours to vouch for: a handler's exception says whatever the application wrote, and a sidecar status message is redacted only by its runs of seven or more digits, which keeps the rest of a JID [observed: ADR 0039, Decision; `presence.go:147` still puts the value it refused in an `InvalidArgument` message].
  Media retry requests are not transfers and get no span.
  `whatsmeow.process` (consumer) around the handlers of each event, durable or ephemeral, with the session id and `whatsmeow.event.type`, the record's type name, and `error.type` when a handler threw.
  It starts with the first of the application's handlers that takes the event, so an event none takes makes no span; `WaitForAsync`'s own registration is no handler of the application's, and makes no span and records no duration.
  It is a root: no context crosses the socket, and `EventPipeline.Start` starts the readers and dispatchers under `ExecutionContext.SuppressFlow`, because the first `ConnectAsync`, or the pairing that yields `Paired`, runs inside the application's code and every later event would otherwise inherit that call's `Activity.Current`, log scopes and other async locals, whether or not anything listens to the library.
  So a handler never sees its caller's `ExecutionContext`: no `Activity.Current`, culture, log scope, xUnit `TestContext.Current` or ASP.NET Core `HttpContext` of the code that connected the session or raised the event; `FakeWhatsAppClient` runs on the same pipeline, and keeps this for parity [observed: `Whatsmeow.Testing.Tests` `EventTests.Handler_RunsOutsideTheContextOfTheCodeThatMadeTheFakeAndRaisedTheEvent`, for an async local, the activity and the culture]; log scopes, `TestContext.Current` and `HttpContext` live in async locals too, so the same holds for them [inferred].
  Each handler runs inside it, so a handler's own spans are its children; a handler gets no span of its own.
- **A cancellation the caller caused is not an error**, one rule for every span and instrument (`Tracing.IsCancellation`): an `OperationCanceledException`, a cancelled task or a `Cancelled` status, when the caller's token is cancelled, ends its span with the `Unset` status and no `error.type`, and is not counted in `whatsmeow.errors`.
  The caller is the call's own token for a send, a transfer and the error count (`CallOptions.CancellationToken`, and a stream read's token), and the stop's token for a handler.
  Any other cancellation is a failure, because nobody asked for it: a call cut by a sidecar that died ends with a `TaskCanceledException` the caller did not cause [observed: `TelemetryCrashTests.Download_TheSidecarDiesMidway_...`], and a handler's own `OperationCanceledException`, as an HTTP timeout inside it, records its duration with its `error.type` and marks the process span.
  Only the stop's cancellation of a handler stays out: no duration, no error on the span.
  The error count tags a cancellation by its exception's type, as a span does, and a status by its error code.
  A handler's cancellation is still logged and raised on `OnHandlerError`, as before: that is the handler contract, not telemetry.
- **Instruments.** `whatsmeow.messages.sent` (counter, `{message}`, session id) once WhatsApp accepted a send; `whatsmeow.errors` (counter, `{error}`, `error.type`); `whatsmeow.sidecar.restarts` (counter, `{restart}`, no tag) once a sidecar started again after one exited on its own.
  `whatsmeow.messages.received` (counter, `{message}`, session id) as each `MessageReceived` arrives, before its handlers, so a redelivery counts again.
  `whatsmeow.handler.duration` (histogram, `s`, session id, event type, and `error.type`, the exception's type name, when it threw) once per handler whose event type matched, a throw included, on the sidecar's `TimeProvider`, with ASP.NET Core's request duration buckets as advice, because the default buckets are for milliseconds.
  `whatsmeow.dispatcher.queue.length` (up/down counter, `{event}`, session id): +1 as a dispatcher receives an event, -1 as it finishes, and minus what a lane the stop ended leaves unrun, so a handler the stop abandoned counts until it returns.
  It excludes the up to 64 events waiting in the bounded channel between a reader and its dispatcher (ARCHITECTURE §8.4), which the dispatcher has not received yet.
  It is not observable: a callback on a factory's meter would outlive its sidecar, and a second sidecar on the same meter would add a second instrument of the name.
- **Errors at the channel.** A gRPC interceptor on the sidecar's one channel counts every failed call by `RpcErrors.Code`: unary calls, client streams and server streams, the library's own calls (acks, reconnects, stream reopens) included.
  A unary or client stream call is returned as it came, with its own response task; a synchronous `OnlyOnFaulted` continuation counts the failure before the caller's await resumes.
  A server stream counts its failure on the first read that throws, once, however often it is read after.
  A cancelled call is not counted: the caller's cancellation surfaces as `OperationCanceledException`, not `RpcException`, and a `Cancelled` status is skipped.
  The interceptor cannot read the session id without descriptor reflection, so errors carry no session id.
  Handler failures are not counted here: they have no error code; the handler duration counts them.
- **The fake publishes nothing.** `FakeWhatsAppClient` runs on the same `EventPipeline` without metrics, which also turns the process span off: it has no sidecar and no meter, and an application's test should not see library spans the real path would not make.
- **Not done here.** Tracing in the Go sidecar or across the socket, an OpenTelemetry package dependency, and log changes (the issue's non-goals); spans for other calls; message types on the counters.

## Consequences

- An application sees the library with OpenTelemetry's `AddSource` and `AddMeter` of the two constants and its own exporters; a generic host's meter factory gives the sidecar's meter without any setup.
- Two sidecars on one meter factory share its meter and its counters; their sends stay apart by session id, and their errors and restarts add up.
- A sidecar's own meter is gone with it; a factory's meter outlives a disposed sidecar, holding only counters.
- Tests: a `MetricCollector` on the shared fixture's own factory sees that sidecar only, and tests tell their sessions apart by the session id; span tests, and the checks that a cancellation counts no error, which have no session id, run in the `TelemetryListeners` collection, alone, because an `ActivityListener` is process-wide; the error counts by code run on a sidecar and meter factory of their own; the test without a factory listens to every unscoped meter of the name, because a `MetricCollector` would follow another test's sidecar.
