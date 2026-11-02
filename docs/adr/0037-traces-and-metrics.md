# 0037. Traces and metrics: one ActivitySource, a meter per sidecar from an IMeterFactory parameter, our own names

- Status: Accepted
- Date: 2026-11-02
- Evidence: issue #40 and its plan review; throwaway measurements at SDK 10.0.403 with Microsoft.Extensions.* 10.0.14, listed below; `Telemetry/SpanTests.cs`, `Telemetry/MeterTests.cs`, `Telemetry/TelemetryCrashTests.cs`, `Hosting/HostingTests.HostWithAMeterFactory_TheSidecarsMeterComesFromIt`, `DispatcherDepthTests`, `Whatsmeow.Testing.Tests/TelemetryTests.cs`
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
- The OpenTelemetry messaging conventions are at Development stability, and their spans require `messaging.destination.name`, which here is the chat JID [observed: opentelemetry.io semconv messaging spans and metrics pages].
  `error.type` is stable: set only on failure, low cardinality, an exception's canonical type name when the value is a type.

## Decision

- **Names.** `WhatsmeowTelemetry.ActivitySourceName` and `WhatsmeowTelemetry.MeterName`, both `"Whatsmeow.Client"`, are public constants in namespace `Whatsmeow` (ADR 0018); the class's docs list every span and instrument with its unit and meaning.
  Spans and instruments use our own `whatsmeow.*` names and tags, not the messaging conventions, because those are not stable and would need the JID; the one convention reused is `error.type`.
  `error.type` is the `WhatsmeowErrorCode` name of a `WhatsmeowException`, and the full type name of any other exception; never a message.
  `whatsmeow.session.id` is the application's own session id, allowed by the issue; no tag carries a JID or a phone number.
- **The meter.** One per `WhatsmeowSidecar`, never a static: from the `IMeterFactory` the sidecar was started with, which owns it, or else a `Meter` of the same name the sidecar creates and disposes.
  The factory is a parameter, `StartAsync(options, loggerFactory, meterFactory, ct)`, as the logger factory is, because an options property cannot build; the hosting package passes the container's.
- **The source.** One static `ActivitySource` with the client's version.
  Each span's tags are set only when `StartActivity` returned one, so no listener means no activity and no tag work.
- **Spans.** `whatsmeow.send` (client kind: no context crosses the socket) around each of the seven sends, with the session id, the message id and the message type; `whatsmeow.media.upload` and `whatsmeow.media.download` (client) around each transfer, with the session id, the media type and `whatsmeow.media.size`, the plaintext's length, known up front for a download and from the result for an upload.
  A failure sets `error.type` and the error status, with the same value as its description; a success sets neither, as the conventions ask.
  Media retry requests are not transfers and get no span.
  `whatsmeow.process` (consumer) around the handlers of each event, durable or ephemeral, with the session id and `whatsmeow.event.type`, the record's type name, and `error.type` when a handler threw.
  It is a root: it clears `Activity.Current` first, because the readers start inside the application's `ConnectAsync` and would otherwise make every later event a child of whatever span that call ran in, and no context crosses the socket.
  Each handler runs inside it, so a handler's own spans are its children; a handler gets no span of its own.
- **Instruments.** `whatsmeow.messages.sent` (counter, `{message}`, session id) once WhatsApp accepted a send; `whatsmeow.errors` (counter, `{error}`, `error.type`); `whatsmeow.sidecar.restarts` (counter, `{restart}`, no tag) once a sidecar started again after one exited on its own.
  `whatsmeow.messages.received` (counter, `{message}`, session id) as each `MessageReceived` arrives, before its handlers, so a redelivery counts again.
  `whatsmeow.handler.duration` (histogram, `s`, session id, event type, and `error.type`, the exception's type name, when it threw) once per handler whose event type matched, a throw included, on the sidecar's `TimeProvider`, with ASP.NET Core's request duration buckets as advice, because the default buckets are for milliseconds.
  `whatsmeow.dispatcher.queue.length` (up/down counter, `{event}`, session id): +1 as a dispatcher receives an event, -1 as it finishes, and minus what a lane the stop ended leaves unrun, so a handler the stop abandoned counts until it returns.
  It is not observable: a callback on a factory's meter would outlive its sidecar, and a second sidecar on the same meter would add a second instrument of the name.
- **Errors at the channel.** A gRPC interceptor on the sidecar's one channel counts every failed call by `RpcErrors.Code`: unary calls, client streams and server streams, the library's own calls (acks, reconnects, stream reopens) included.
  A cancelled call is not counted: the caller's cancellation surfaces as `OperationCanceledException`, not `RpcException`, and a `Cancelled` status is skipped.
  The interceptor cannot read the session id without descriptor reflection, so errors carry no session id.
  Handler failures are not counted here: they have no error code; the handler duration counts them.
- **The fake publishes nothing.** `FakeWhatsAppClient` runs on the same `EventPipeline` without metrics, which also turns the process span off: it has no sidecar and no meter, and an application's test should not see library spans the real path would not make.
- **Not done here.** Tracing in the Go sidecar or across the socket, an OpenTelemetry package dependency, and log changes (the issue's non-goals); spans for other calls; message types on the counters.

## Consequences

- An application sees the library with OpenTelemetry's `AddSource` and `AddMeter` of the two constants and its own exporters; a generic host's meter factory gives the sidecar's meter without any setup.
- Two sidecars on one meter factory share its meter and its counters; their sends stay apart by session id, and their errors and restarts add up.
- A sidecar's own meter is gone with it; a factory's meter outlives a disposed sidecar, holding only counters.
- Tests: a `MetricCollector` on the shared fixture's own factory sees that sidecar only, and tests tell their sessions apart by the session id; span tests and the error counts, which have no session id, run in the `TelemetryListeners` collection, alone, because an `ActivityListener` is process-wide; the test without a factory listens to every unscoped meter of the name, because a `MetricCollector` would follow another test's sidecar.
