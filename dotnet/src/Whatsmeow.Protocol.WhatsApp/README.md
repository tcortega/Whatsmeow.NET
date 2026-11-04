# Whatsmeow.Protocol.WhatsApp

WhatsApp's own protobuf messages (`Whatsmeow.Proto.*`), generated from [whatsmeow](https://github.com/tulir/whatsmeow)'s definitions.
[`Whatsmeow.Client`](https://www.nuget.org/packages/Whatsmeow.Client) uses them for its `[Experimental]` raw members, `MessageReceived.Raw` and `SendRawAsync`.

It is unstable: these types change whenever whatsmeow does, which can be every week, and a patch release of Whatsmeow.NET may carry such a change.

License: MPL-2.0, as whatsmeow's definitions are.
Source: [https://github.com/tcortega/Whatsmeow.NET](https://github.com/tcortega/Whatsmeow.NET).
