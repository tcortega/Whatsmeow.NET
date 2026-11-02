# 0034. Hosting: DI, options, keyed sessions and a hosted sidecar

- Status: Accepted
- Date: 2026-11-02
- Evidence: issue #39, its plan review and the two PR #75 reviews; throwaway measurements at SDK 10.0.403 with Microsoft.Extensions.* 10.0.14 (Native AOT on osx-arm64 unless named), listed below; `Hosting/HostingTests.cs`; `DispatcherTests.Stop_OnceItsWaitIsCancelled_AcksWhatFinishedWithoutWaitingForAHandlerThatIgnoresItsToken`
- Amends: ARCHITECTURE §8.4, the DI handler's signature
- Amended by: [ADR 0036](0036-testing-package-and-fake-client.md) (a key replaced with `UseFakeClient` gets the handlers, and the host starts no sidecar)

## Context

Most applications are generic hosts.
`Whatsmeow.Extensions.Hosting` gives them `services.AddWhatsmeow()`: options from configuration, one sidecar for the host's lifetime, a keyed client per session, and handlers in DI, all AOT safe.
What the host and the binder do decided the design:

- The host builds every hosted service before it starts any of them: a service that takes `[FromKeyedServices("sales")] IWhatsAppClient` in its constructor gets it before the library's hosted service has started anything [observed: scratch host, a consumer registered before and one after the library's service].
- `IHostedLifecycleService.StartingAsync` runs before every hosted service's `StartAsync`, whatever the registration order, `StartedAsync` after every `StartAsync`, and `StoppedAsync` after every `StopAsync` [observed: same host].
- An exception from `StartingAsync` or `StartedAsync` reaches `host.StartAsync` unwrapped [observed: same host for `StartingAsync`; `ConnectFailsAtHostStart_TheHostStartFails_AndLeavesNoSidecar` for `StartedAsync`], and a host whose start failed calls none of the stop methods: it only disposes the container [observed: same host, a failing `StartingAsync`; `LaterServiceFailsToStart_NoEventIsHandled_AndThePendingOnesComeInTheNextHost` for a failing `StartAsync`].
- With `HostOptions.ShutdownTimeout` at 1 s, the stop token is cancelled at about 1 s, and the host still waits for a slow `StoppedAsync`: it returned after 3 s. At zero, the token is cancelled about 10 ms into `StoppedAsync`, not before it [observed: same host].
- A library with `IsAotCompatible` and without `EnableConfigurationBindingGenerator` fails `BindConfiguration` with IL2026 and IL3050; with it, it builds, and a Native AOT app that roots the library publishes with zero warnings and binds a `TimeSpan`, an enum and an environment variable [observed: scratch library and app; the ARCHITECTURE §14 spike saw the same at SDK 10.0.400].
- `ValidateOnStart` with a hand-written `IValidateOptions<T>` fails `host.StartAsync` with `OptionsValidationException` under Native AOT [observed: same app]. A value the binder cannot convert fails first, with the binder's `InvalidOperationException`, and a number binds to an enum even when no member has it [observed: `UnparsableOption_TheHostStartFailsWithTheBindersInvalidOperationException`, `Transport_FromConfiguration_ANumberNoTransportHasFailsValidation`].
- A synchronous `IHost.Dispose()` disposes `IAsyncDisposable` singletons, factory-made and keyed ones included, in reverse order [observed: scratch host, JIT].
- No `Meter` exists in the library yet [observed: no `Meter` or `IMeterFactory` in `Whatsmeow.Client`]; #40 adds telemetry.

## Decision

- **API**, in namespace `Whatsmeow.Extensions.Hosting`: `services.AddWhatsmeow()` returns a `WhatsmeowBuilder` with `Services`, `AddSession(string key)`, `AddHandler<TEvent, THandler>()` and `AddHandler<TEvent, THandler>(string key)`; handlers implement `IWhatsAppEventHandler<in TEvent>`.
  No overload takes options or a section: the section is `Whatsmeow`, and an application that needs more uses the options pattern itself (`services.Configure<WhatsmeowSidecarOptions>`). `Services` is the Microsoft builder convention, for extensions of the builder (#41, #42).
- **Options.** `BindConfiguration("Whatsmeow")` through the configuration binding generator, a `PostConfigure` step, `WhatsmeowSidecarOptionsValidator` and `ValidateOnStart`.
  The validator runs `WhatsmeowSidecarOptionsSetup.Failures`, the rules the no-DI path throws, so both paths share the rules (docs/guidelines/options.md).
  The step is the no-DI path's `Apply` with one deliberate difference: a relative `DatabasePath` or `MediaRoot`, and the default database file, are first put in `IHostEnvironment.ContentRootPath`. The current directory of a Windows service is `System32` [inferred: the Windows service control manager's default, not measured here], where the key database must not end up.
  A value the binder cannot convert fails with the binder's `InvalidOperationException`, not `OptionsValidationException`; the docs of `AddWhatsmeow` say so.
- **The sidecar is created unstarted.** `WhatsmeowSidecar` is a singleton the container builds with the internal `WhatsmeowSidecar.Create` (copy, setup step, rules); the hosted service starts it with `StartProcessAsync` (find the binary, socket directory, launch, supervisor). The no-DI `StartAsync` is the two in a row.
  So a keyed client is the session's real client, from the internal `WhatsmeowSessions.Client(id)`, the one `GetOrCreateAsync` returns, also before the host started; a call on it before then fails with `SidecarUnavailableException` [inferred: the channel has no endpoint until the first launch].
  A dispose while the first start runs cancels it (the dispose's token is linked into the launch) and waits for it, so no process or socket directory is left behind; the start then fails with `ObjectDisposedException`.
- **Lifecycle.** One internal `IHostedLifecycleService`.
  `StartingAsync` starts the sidecar and creates each declared session, so a service's `StartAsync` finds both, and can pair a session. An `IHostedLifecycleService` registered before `AddWhatsmeow` sees the sidecar not started in its own `StartingAsync`; the docs of `AddWhatsmeow` say so.
  `StartedAsync`, once every `StartAsync` succeeded, connects the declared sessions `ListAsync` reports `Disconnected` (paired); `Created` and `LoggedOut` wait for the application to pair them, and a stopping state stays, logged ([ADR 0020](0020-sessions-and-the-store.md): the application decides).
  Events flow only once a session is connected in the process ([ADR 0022](0022-outbox-event-stream-and-dispatcher.md)), so a host whose start fails, which only disposes the container, never ran handlers against a container being disposed: a handler's `CreateAsyncScope` on a disposed root provider throws `ObjectDisposedException`, which would count the event as handled and ack it.
  A window remains: a hosted service registered after `AddWhatsmeow` whose `StartedAsync` throws fails the host after the connect, and the events of that moment can be lost that way. So can the events of a session the application connected itself in its own `StartAsync` before a later service failed.
  It connects the sidecar's own client of the session, the one the handlers are on, never the client the container resolves for the key: an application that replaces the keyed registration (#41's fakes) gets no handlers on its replacement, and the real session is still connected, so #41 has to decide what the host does for a replaced key.
  Any failure, a refused connect included, fails the host's start, and the sidecar is disposed first, so a failed start leaves no process: fail fast, and the orchestrator's restart is the retry.
  `StoppedAsync` stops the sidecar: the handlers stop and what they finished is acked, then Shutdown, the 5 s grace, the kill.
  Once the host's shutdown token fires, it no longer waits for handlers that ignore their cancellation: each dispatcher stops waiting for its lanes and still acks the watermark, which never passes an unfinished event, so those come again. The process stop is never cut short, and the host waits for it, so the kill always runs and the sidecar never outlives the host (§13 invariant 9).
  Crashes in between are the supervisor's, unchanged (ADR 0017).
- **Handlers.** `IWhatsAppEventHandler<in TEvent>.HandleAsync(TEvent e, IWhatsAppClient client, CancellationToken cancellationToken)`.
  The client of the event's session is a parameter because a handler for every session has no key to resolve one by, and the library has no synchronous lookup by id; a non-keyed scoped client set per event was the alternative, and it throws wherever else it is resolved. ARCHITECTURE §8.4 had `(TEvent, CancellationToken)`.
  `TEvent` is contravariant, so a handler of `WhatsAppEvent` can be registered for any event type; made so before the first stable tag, when adding it is still not a break.
  Each handler is a scoped service, resolved in a scope of its own per event, inside a delegate given to `client.On<TEvent>`, so ordering, the watermark and the error rules are `On<T>`'s.
  `WhatsmeowSidecar.Create` takes a hook that runs once for each new client of its sessions, before anyone gets it; the hosting package attaches the handlers there, so they reach declared sessions and sessions created at runtime through `WhatsmeowSidecar.Sessions` alike, before the application can connect them. A handler registered with a key reaches that session only.
  Registration is explicit and generic, `THandler` with `DynamicallyAccessedMembers(PublicConstructors)`: the event type is named, because finding the handler interfaces a type implements needs reflection.
- **Sessions are declared in code only**, because keyed services are registered with their keys, before configuration is read. A declared session cannot be deleted: `WhatsmeowSessions.DeleteAsync` throws `InvalidOperationException` for it, since its keyed client would stay behind as a client of a deleted session.
- **Idempotent.** A second `AddWhatsmeow` adds nothing but returns a builder; a session declared twice is declared once; a handler registered twice for the same event type and session is registered once.
- **The container's services.** The `ILoggerFactory` and `TimeProvider` in the container are the sidecar's. `IMeterFactory` is #40's, with the telemetry it feeds.
- **Fake WhatsApp.** The internal `WhatsmeowBuilder.UseFakeWhatsApp()` starts the sidecar with `--fake-whatsapp`, for the integration tests ([ADR 0019](0019-fake-whatsapp-mode.md)).
- **Packaging.** `Whatsmeow.Extensions.Hosting` uses `Whatsmeow.Client` internals (`InternalsVisibleTo`), so it depends on it at exactly its own version (`PackExactVersion`, [ADR 0023](0023-native-packages-build-pin-and-find.md)), which `build/ci.sh install` checks; its dependencies are `Microsoft.Extensions.Hosting.Abstractions` and `Microsoft.Extensions.Options.ConfigurationExtensions`.
  NuGet does not enforce the exact pin: an application that also references another `Whatsmeow.Client` version directly gets NU1608, a warning, and the internals the hosting package calls can be missing at runtime [inferred: NuGet's dependency resolution, not measured here]; the same risk as ADR 0023's native pins.
  `tools/check-repo` allows `EnableConfigurationBindingGenerator`, with the value `true` only.
  The library build's trim and AOT analyzers cover all of the package. `tests/AotSmoke` roots the assembly, so Native AOT compiles and analyses what rooting reaches, which leaves out generic code no one instantiates, such as `AddHandler<TEvent, THandler>` [inferred: ILC compiles generic methods per instantiation]; #44 runs the hosting path there.

## Consequences

- An application can take a keyed client in any constructor, a hosted service's included, and use it from its `StartAsync` on, whatever order it registered things in; its declared sessions connect only once every `StartAsync` succeeded.
- A host whose WhatsApp connect fails does not start; one whose sidecar crashes later keeps running while the supervisor restarts it.
- A host stop takes at most the shutdown timeout for the handlers, then up to the 5 s grace for the process.
- `ValidateOnStart` and the validator are also guarded by `Create`, which validates again: a host with invalid options fails to start even without them, and the validator is what makes `IOptions<WhatsmeowSidecarOptions>.Value` throw for any other reader.
- Tests: `Hosting/HostingTests.cs`, one host and fake-mode sidecar per test on a database of its own, since starting and stopping is the behaviour under test (docs/guidelines/tests.md); the dispatcher's bounded stop in `DispatcherTests`.
