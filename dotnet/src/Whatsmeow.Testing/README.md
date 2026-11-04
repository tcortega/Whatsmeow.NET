# Whatsmeow.Testing

`FakeWhatsAppClient` is an `IWhatsAppClient` in memory, with no sidecar and no WhatsApp, for testing the code you build on [`Whatsmeow.Client`](https://www.nuget.org/packages/Whatsmeow.Client).

* `RaiseAsync(event)` runs your handlers on the client's own event pipeline, with its per-chat order, and waits for them.
* `Calls` records every call your code made, and `FailNext` makes the next call to a member throw the exception you give.
* `UseFakeClient` replaces a session declared with [`Whatsmeow.Extensions.Hosting`](https://www.nuget.org/packages/Whatsmeow.Extensions.Hosting), so a host under test starts no sidecar.

[Hosting and testing](https://github.com/tcortega/Whatsmeow.NET/blob/main/docs/guides/hosting.md#testing) has an example.
It is pinned to the exact version of `Whatsmeow.Client` and `Whatsmeow.Extensions.Hosting` it was built with.

License: MPL-2.0.
