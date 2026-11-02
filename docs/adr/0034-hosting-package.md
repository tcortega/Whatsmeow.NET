# 0034. Hosting: DI, options, keyed sessions and a hosted sidecar

- Status: Accepted
- Date: 2026-11-02
- Evidence: issue #39 and its plan review; throwaway measurements at SDK 10.0.403 with Microsoft.Extensions.* 10.0.14 (Native AOT on osx-arm64 unless named), listed below; `Hosting/HostingTests.cs`
- Amends: ARCHITECTURE §8.4, the DI handler's signature

## Context

Most applications are generic hosts.
`Whatsmeow.Extensions.Hosting` gives them `services.AddWhatsmeow()`: options from configuration, one sidecar for the host's lifetime, a keyed client per session, and handlers in DI, all AOT safe.
What the host and the binder do decided the design:

- The host builds every hosted service before it starts any of them: a service that takes `[FromKeyedServices("sales")] IWhatsAppClient` in its constructor gets it before the library's hosted service has started anything [observed: scratch host, a consumer registered before and one after the library's service].
- `IHostedLifecycleService.StartingAsync` runs before every hosted service's `StartAsync`, whatever the registration order, and `StoppedAsync` after every `StopAsync` [observed: same host].
- A hosted service that throws in `StartingAsync` makes `host.StartAsync` throw that exception, not wrapped [observed: same host].
- With `HostOptions.ShutdownTimeout` at 1 s, the stop token is cancelled at about 1 s, and the host still waits for a slow `StoppedAsync`: it returned after 3 s [observed: same host].
- A library with `IsAotCompatible` and without `EnableConfigurationBindingGenerator` fails `BindConfiguration` with IL2026 and IL3050; with it, it builds, and a Native AOT app that roots the library publishes with zero warnings and binds a `TimeSpan`, an enum and an environment variable [observed: scratch library and app; the ARCHITECTURE §14 spike saw the same at SDK 10.0.400].
- `ValidateOnStart` with a hand-written `IValidateOptions<T>` fails `host.StartAsync` with `OptionsValidationException` under Native AOT [observed: same app].
- A synchronous `IHost.Dispose()` disposes `IAsyncDisposable` singletons, factory-made and keyed ones included, in reverse order [observed: scratch host, JIT].
- No `Meter` exists in the library yet [observed: no `Meter` or `IMeterFactory` in `Whatsmeow.Client`]; #40 adds telemetry.

## Decision

- **API**, in namespace `Whatsmeow.Extensions.Hosting`: `services.AddWhatsmeow()` returns a `WhatsmeowBuilder` with `AddSession(string key)`, `AddHandler<TEvent, THandler>()` and `AddHandler<TEvent, THandler>(string key)`; handlers implement `IWhatsAppEventHandler<TEvent>`.
  No overload takes options or a section: the section is `Whatsmeow`, and an application that needs more uses the options pattern itself (`services.Configure<WhatsmeowSidecarOptions>`).
- **Options.** `BindConfiguration("Whatsmeow")` through the configuration binding generator, `PostConfigure(WhatsmeowSidecarOptionsSetup.Apply)`, `WhatsmeowSidecarOptionsValidator` and `ValidateOnStart`.
  The validator runs `WhatsmeowSidecarOptionsSetup.Failures`, the rules the no-DI path throws, so both paths share the setup step and the rules (docs/guidelines/options.md).
- **The sidecar is created unstarted.** `WhatsmeowSidecar` is a singleton the container builds with the internal `WhatsmeowSidecar.Create` (copy, setup step, rules); the hosted service starts it with `StartProcessAsync` (find the binary, socket directory, launch, supervisor). The no-DI `StartAsync` is the two in a row.
  So a keyed client is the session's real client, from the internal `WhatsmeowSessions.Client(id)`, the one `GetOrCreateAsync` returns, also before the host started; a call on it before then fails with `SidecarUnavailableException` [inferred: the channel has no endpoint until the first launch].
- **Lifecycle.** One internal `IHostedLifecycleService`. `StartingAsync` starts the sidecar, creates each declared session, and connects the ones `ListAsync` reports `Disconnected` (paired); `Created` and `LoggedOut` wait for the application to pair them, and a stopping state stays, logged ([ADR 0020](0020-sessions-and-the-store.md): the application decides).
  Any failure, a refused connect included, fails the host's start, and the sidecar is disposed first, so a failed start leaves no process: fail fast, and the orchestrator's restart is the retry.
  `StoppedAsync` disposes the sidecar: the handlers stop and what they finished is acked, then Shutdown, the 5 s grace, the kill. It ignores the host's shutdown token, which the host waits beyond, so the kill always runs and the sidecar never outlives the host (§13 invariant 9). Cutting the grace at the token would bound the host's stop more tightly; nothing asked for it.
  Crashes in between are the supervisor's, unchanged (ADR 0017).
- **Handlers.** `IWhatsAppEventHandler<TEvent>.HandleAsync(TEvent e, IWhatsAppClient client, CancellationToken cancellationToken)`.
  The client of the event's session is a parameter because a handler for every session has no key to resolve one by, and the library has no synchronous lookup by id; a non-keyed scoped client set per event was the alternative, and it throws wherever else it is resolved. ARCHITECTURE §8.4 had `(TEvent, CancellationToken)`.
  Each handler is a scoped service, resolved in a scope of its own per event, inside a delegate given to `client.On<TEvent>`, so ordering, the watermark and the error rules are `On<T>`'s.
  `WhatsmeowSidecar.Create` takes a hook that runs once for each new client of its sessions, before anyone gets it; the hosting package attaches the handlers there, so they reach declared sessions and sessions created at runtime through `WhatsmeowSidecar.Sessions` alike, before the application can connect them. A handler registered with a key reaches that session only.
  Registration is explicit and generic, `THandler` with `DynamicallyAccessedMembers(PublicConstructors)`: the event type is named, because finding the handler interfaces a type implements needs reflection.
- **Sessions are declared in code only**, because keyed services are registered with their keys, before configuration is read.
- **Idempotent.** A second `AddWhatsmeow` adds nothing but returns a builder; a session declared twice is declared once.
- **The container's services.** The `ILoggerFactory` and `TimeProvider` in the container are the sidecar's. `IMeterFactory` is #40's, with the telemetry it feeds.
- **Fake WhatsApp.** The internal `WhatsmeowBuilder.UseFakeWhatsApp()` starts the sidecar with `--fake-whatsapp`, for the integration tests ([ADR 0019](0019-fake-whatsapp-mode.md)).
- **Packaging.** `Whatsmeow.Extensions.Hosting` uses `Whatsmeow.Client` internals (`InternalsVisibleTo`), so it depends on it at exactly its own version (`PackExactVersion`, [ADR 0023](0023-native-packages-build-pin-and-find.md)), which `build/ci.sh install` checks; its dependencies are `Microsoft.Extensions.Hosting.Abstractions` and `Microsoft.Extensions.Options.ConfigurationExtensions`.
  `tools/check-repo` allows `EnableConfigurationBindingGenerator`, with the value `true` only.
  `tests/AotSmoke` roots the assembly, so all of it is analysed under Native AOT; running the hosting path there is #44.

## Consequences

- An application can take a keyed client in any constructor, a hosted service's included, and use it from its `StartAsync` on, whatever order it registered things in.
- A host whose WhatsApp connect fails does not start; one whose sidecar crashes later keeps running while the supervisor restarts it.
- A host stop can take up to the 5 s grace beyond `ShutdownTimeout`, plus what the handlers take to see their token.
- `ValidateOnStart` and the validator are also guarded by `Create`, which validates again: a host with invalid options fails to start even without them, and the validator is what makes `IOptions<WhatsmeowSidecarOptions>.Value` throw for any other reader.
- Tests: `Hosting/HostingTests.cs`, one host and fake-mode sidecar per test on a database of its own, since starting and stopping is the behaviour under test (docs/guidelines/tests.md).
