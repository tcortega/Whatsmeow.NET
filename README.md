# Whatsmeow.NET

Whatsmeow.NET lets a .NET application use WhatsApp's multi-device protocol through [whatsmeow](https://github.com/tulir/whatsmeow), the Go library behind mautrix-whatsapp.
Your app talks to `whatsmeow-net`, a small Go sidecar that ships inside the NuGet packages, and the library starts it, supervises it and hides it behind a plain C# API.
It covers messages, media, groups, contacts, presence, receipts, history and chat state, and it is Native AOT and trimming safe.

This is not an official WhatsApp client and has nothing to do with WhatsApp or Meta.
WhatsApp can ban accounts it sees as automated, and which sending rates are safe is [unknown].

## Install

It targets .NET 10 on Linux (glibc and musl), macOS and Windows, x64 and arm64.

```sh
dotnet add package Whatsmeow.Client
```

Add `Whatsmeow.Extensions.Hosting` instead for dependency injection and a generic host.
The sidecar binary for your platform comes with the package; you never install Go.

## Quick start

A console app with one session that answers every text with the same text.
The first run shows a QR code to scan with the phone (WhatsApp, Linked devices, Link a device), and later runs connect the paired session.
It draws the QR code with QRCoder:

```sh
dotnet new console -n EchoConsole
cd EchoConsole
dotnet add package Whatsmeow.Client
dotnet add package QRCoder
```

Then replace `Program.cs` with the first file below, and add the second as `Echo.cs`.

<!-- snippet: dotnet/samples/EchoConsole/Program.cs -->
```csharp
using EchoConsole;
using Whatsmeow;

// One WhatsApp session that echoes every text it gets, without DI. From NuGet the sidecar comes with the package; in
// this repository, set WHATSMEOW_NET_SIDECAR_PATH to a whatsmeow-net you built. Scan the QR code with the phone:
// WhatsApp, Linked devices, Link a device.
var sidecar = await WhatsmeowSidecar.StartAsync(new WhatsmeowSidecarOptions(), CancellationToken.None).ConfigureAwait(false);
await using (sidecar.ConfigureAwait(false))
{
	var client = await sidecar.Sessions.GetOrCreateAsync(SessionId.From("main"), CancellationToken.None).ConfigureAwait(false);
	using var echo = await Echo.StartAsync(client, Console.Out, CancellationToken.None).ConfigureAwait(false);

	await Console.Out.WriteLineAsync("Echoing every text; press Enter to stop.").ConfigureAwait(false);
	_ = await Console.In.ReadLineAsync().ConfigureAwait(false);
}
```

<!-- snippet: dotnet/samples/EchoConsole/Echo.cs -->
```csharp
using System.Security.Cryptography;
using System.Text;
using QRCoder;
using Whatsmeow;
using Whatsmeow.Events;
using Whatsmeow.Pairing;
using Whatsmeow.Sessions;

namespace EchoConsole;

/// <summary>The sample's setup: answers each text a session gets with the same text, in the same chat.</summary>
public static class Echo
{
	private const string Prefix = "echo: ";

	/// <summary>
	/// Starts echoing, then pairs the session with a QR code on <paramref name="console"/> if it is not paired, or
	/// connects it if it is. Dispose the result to stop echoing.
	/// </summary>
	public static async Task<IDisposable> StartAsync(IWhatsAppClient client, TextWriter console, CancellationToken cancellationToken)
	{
		// The handler comes first: events start when the session connects or its pairing yields Paired.
		var echo = client.On<MessageReceived>(async (e, ct) =>
		{
			// Never a status (status@broadcast), which cannot be answered; never the account's own texts, nor an echo:
			// two sessions that text each other would echo forever.
			if (IsBroadcast(e.Source.Chat) || e.Source.IsFromMe || e.Content is not TextContent { Text: var text } || text.StartsWith(Prefix, StringComparison.Ordinal))
				return;

			// A message can come again, after a stop or a crash before its ack; the echo's id is the same each time, and a
			// send of an id already sent is never sent again, so each text is echoed once.
			_ = await client.SendTextAsync(e.Source.Chat, Prefix + text, EchoId(e), cancellationToken: ct).ConfigureAwait(false);
		});

		// A start that fails, a pairing nobody scanned in time included, stops echoing.
		try
		{
			await PairOrConnectAsync(client, console, cancellationToken).ConfigureAwait(false);
		}
		catch
		{
			echo.Dispose();
			throw;
		}

		return echo;
	}

	private static async Task PairOrConnectAsync(IWhatsAppClient client, TextWriter console, CancellationToken cancellationToken)
	{
		var state = await client.WatchStateAsync(cancellationToken).FirstAsync(cancellationToken).ConfigureAwait(false);
		switch (state)
		{
			case SessionState.Created or SessionState.LoggedOut:
				// Paired means connected, with events flowing: no ConnectAsync after it.
				await foreach (var update in client.PairWithQrCodeAsync(cancellationToken).ConfigureAwait(false))
				{
					if (update is PairingUpdate.QrCode code)
						await console.WriteLineAsync(AsciiQRCodeHelper.GetQRCode(code.Code, QRCodeGenerator.ECCLevel.L, drawQuietZones: true, invert: true)).ConfigureAwait(false);
				}

				break;
			case SessionState.Disconnected:
				await client.ConnectAsync(cancellationToken).ConfigureAwait(false);
				break;
			default:
				await console.WriteLineAsync($"The session is {state}, so it neither pairs nor connects.").ConfigureAwait(false);
				break;
		}
	}

	private static bool IsBroadcast(Jid chat) => string.Equals(chat.Server, "broadcast", StringComparison.Ordinal);

	/// <summary>The echo's id, from the session, the chat and the message's id, in the only format a send takes: <c>3EB0</c> and 18 uppercase hex digits.</summary>
	private static MessageId EchoId(MessageReceived e) =>
		MessageId.From("3EB0" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{e.SessionId.Value}\n{e.Source.Chat}\n{e.Id.Value}")))[..18]);
}
```

Pairing starts the events, so there is no `ConnectAsync` after it; [Sessions and pairing](docs/guides/sessions-and-pairing.md) says why handlers come first and why a new device stays online until `CaughtUp`.

### With a host

The same bot with `Microsoft.Extensions.Hosting`, two sessions this time, each one a keyed `IWhatsAppClient`.
The host starts the sidecar, connects the paired sessions, and calls your handlers in a DI scope per event.

<!-- snippet: dotnet/samples/EchoBot/EchoBotServices.cs -->
```csharp
using Microsoft.Extensions.DependencyInjection;
using Whatsmeow.Events;
using Whatsmeow.Extensions.Hosting;
using Whatsmeow.History;

namespace EchoBot;

/// <summary>The echo bot's setup: one sidecar for the host, two sessions, their handlers, and pairing on the console.</summary>
public static class EchoBotServices
{
	/// <summary>The first session's id, which is also its service key.</summary>
	public const string Alice = "alice";

	/// <summary>The second session's id, which is also its service key.</summary>
	public const string Bob = "bob";

	public static WhatsmeowBuilder AddEchoBot(this IServiceCollection services)
	{
		var whatsmeow = services
			.AddWhatsmeow()
			.AddSession(Alice)
			.AddSession(Bob)
			.AddHandler<MessageReceived, EchoHandler>()
			.AddHandler<ConnectionStateChanged, StateLog>()
			.AddHandler<HistoryReceived, HistoryLog>();
		_ = services.AddHostedService<QrPairing>();
		return whatsmeow;
	}
}
```

<!-- snippet: dotnet/samples/EchoBot/EchoHandler.cs -->
```csharp
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using Whatsmeow;
using Whatsmeow.Events;
using Whatsmeow.Extensions.Hosting;

namespace EchoBot;

/// <summary>Answers each text a session receives with the same text, in the same chat.</summary>
public sealed partial class EchoHandler(ILogger<EchoHandler> logger) : IWhatsAppEventHandler<MessageReceived>
{
	private const string Prefix = "echo: ";

	public async ValueTask HandleAsync(MessageReceived e, IWhatsAppClient client, CancellationToken cancellationToken)
	{
		// Never a status (status@broadcast), which cannot be answered; never the account's own texts, nor an echo: two
		// sessions that text each other would echo each other forever.
		if (IsBroadcast(e.Source.Chat) || e.Source.IsFromMe || e.Content is not TextContent { Text: var text } || text.StartsWith(Prefix, StringComparison.Ordinal))
			return;

		// A message can come again, after a stop during the send or a crash before its ack; the echo's id is the same
		// each time, and a send of an id already sent is never sent again, so each text is echoed once.
		_ = await client.SendTextAsync(e.Source.Chat, Prefix + text, EchoId(e), cancellationToken: cancellationToken).ConfigureAwait(false);
		Echoed(logger, e.Id.Value, e.SessionId.Value, e.Timestamp);
	}

	private static bool IsBroadcast(Jid chat) => string.Equals(chat.Server, "broadcast", StringComparison.Ordinal);

	/// <summary>The echo's id, from the session, the chat and the message's id, in the only format a send takes: <c>3EB0</c> and 18 uppercase hex digits.</summary>
	private static MessageId EchoId(MessageReceived e) =>
		MessageId.From("3EB0" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{e.SessionId.Value}\n{e.Source.Chat}\n{e.Id.Value}")))[..18]);

	[LoggerMessage(1, LogLevel.Information, "Echoed {MessageId} of session {SessionId}, sent at {Timestamp:O}")]
	private static partial void Echoed(ILogger logger, string messageId, string sessionId, DateTimeOffset timestamp);
}
```

[`Program.cs`](dotnet/samples/EchoBot/Program.cs) builds the host and exports traces and metrics, and [`QrPairing.cs`](dotnet/samples/EchoBot/QrPairing.cs) pairs each session that is not paired yet.
Both samples are built and run against a fake WhatsApp in CI, and the code above is checked to be theirs.

## What it guarantees

* Decrypted messages, receipts except retry receipts, group notifications and history reach your handlers at least once, through a crash of either process; undecryptable messages, presence, typing, contact and chat state changes are best effort.
  [Delivery](docs/guides/delivery.md) has the full list.
* So handlers must be idempotent, with `SessionId` plus `MessageId` as the key.
* A send whose outcome is unknown is never retried for you, and repeating a `MessageId` never sends a message twice.

## Packages

| Package | For |
|---|---|
| `Whatsmeow.Client` | the client: start the sidecar, sessions, pairing, events, sending |
| `Whatsmeow.Extensions.Hosting` | `AddWhatsmeow()`, configuration, keyed sessions and DI handlers |
| `Whatsmeow.Testing` | `FakeWhatsAppClient`, to test your handlers without WhatsApp |
| `Whatsmeow.Protocol`, `Whatsmeow.Protocol.WhatsApp` | generated protobuf code the others use; no stability promise |
| `Whatsmeow.Sidecar.Native.Linux`, `.MacOS`, `.Windows` | the `whatsmeow-net` binaries; `Whatsmeow.Client` brings them |

## Guides

[docs/guides](docs/guides/README.md) covers pairing, delivery, messaging, media, groups, chats and contacts, history, hosting, testing, errors, telemetry, External mode, deployment and Native AOT, security, and licensing.

## License

The .NET packages and this repository are MPL-2.0.
The `whatsmeow-net` binary links libsignal and is GPL-3.0-only; [Licensing](docs/guides/licensing.md) says what that means if you redistribute it.

## Contributing

Start with [AGENTS.md](AGENTS.md) and [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md).
