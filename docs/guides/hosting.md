# Hosting and testing

`Whatsmeow.Extensions.Hosting` runs the sidecar with a generic host, and `Whatsmeow.Testing` lets you test your handlers without one ([ADR 0034](../adr/0034-hosting-package.md), [ADR 0036](../adr/0036-testing-package-and-fake-client.md)).

## Setup

```csharp
builder.Services
	.AddWhatsmeow()
	.AddSession("sales")
	.AddSession("support")
	.AddHandler<MessageReceived, OrderHandler>()
	.AddHandler<MessageReceived, TicketHandler>("support");
```

* `AddWhatsmeow()` registers one `WhatsmeowSidecar` for the host, and binds its options from the `Whatsmeow` configuration section.
* `AddSession(key)` declares a session whose id is its key, resolved as `[FromKeyedServices("sales")] IWhatsAppClient`.
* `AddHandler<TEvent, THandler>()` runs a handler for that event of every session, and with a key for one session only.

A handler implements `IWhatsAppEventHandler<TEvent>`, and gets the event with the client of the session it came from, to answer on:

```csharp
public sealed class OrderHandler(Orders orders) : IWhatsAppEventHandler<MessageReceived>
{
	public async ValueTask HandleAsync(MessageReceived e, IWhatsAppClient client, CancellationToken cancellationToken) { /* ... */ }
}
```

Each event runs in a DI scope of its own, so a handler can take scoped services.
Handlers also run for sessions you create at run time through `WhatsmeowSidecar.Sessions`; only declared sessions are keyed services, and they cannot be deleted.

## Configuration

```json
{
  "Whatsmeow": {
    "DatabasePath": "data/whatsmeow-net.db",
    "MediaRoot": "/srv/media",
    "SendPacing": { "Burst": 5, "Interval": "00:00:01" },
    "History": { "RequestFullSync": true }
  }
}
```

The keys are `WhatsmeowSidecarOptions`'s properties: `SidecarPath`, `StartupTimeout`, `DatabasePath`, `Transport`, `MediaRoot`, `History`, `SendPacing` and `External` ([External mode](external-mode.md)).
A relative `DatabasePath`, `MediaRoot` or `External:TokenPath`, and the default database file, are in the host's content root, not the current directory, which for a Windows service is `System32`.
Every option is checked when the host starts, so a bad value fails the start, never the first send ([options guideline](../guidelines/options.md)).

## Lifecycle

* Starting: the sidecar starts and the declared sessions are created before any hosted service's `StartAsync`, so your services can use them; once every service started, the declared sessions that are paired and `Disconnected` are connected.
* A failure on the way, a failed connect included, fails the host's start and leaves no sidecar running.
* Stopping: the sidecar stops after every hosted service stopped. Handlers get the cancel and have the host's shutdown timeout to finish; one that ignores its token is left behind and its event comes again next time (§8.5).
* A crash of the sidecar in between is the library's: it starts a new one and reconnects the sessions ([Sessions](sessions-and-pairing.md)).

The library pairs nothing by itself: a hosted service that pairs unpaired sessions, like the sample's [`QrPairing`](../../dotnet/samples/EchoBot/QrPairing.cs), is yours to write.

## Testing

`FakeWhatsAppClient` is an `IWhatsAppClient` with no sidecar and no WhatsApp:

* `RaiseAsync(event)` runs your handlers on the client's own event pipeline, in the same per-chat order, and waits for them.
* `Calls` records what your code called, with its arguments; `FailNext(member, exception)` makes the next call to a member throw.
* `UseFakeClient(fake)` on the builder replaces a declared session, and a host whose declared sessions are all fakes starts no sidecar.

```csharp
await using var fake = new FakeWhatsAppClient(SessionId.From("sales"));
builder.Services.AddWhatsmeow().AddSession("sales").AddHandler<MessageReceived, OrderHandler>().UseFakeClient(fake);
using var host = builder.Build();
await host.StartAsync(cancellationToken);

await fake.RaiseAsync(message, cancellationToken);

Assert.Equal(nameof(IWhatsAppClient.SendTextAsync), Assert.Single(fake.Calls).Member);
```

Replace every declared session, or the start fails naming the one left.
To throw the library's own errors, build its exceptions: each one sets its `ErrorCode`, so your code sees what the real client would throw.
