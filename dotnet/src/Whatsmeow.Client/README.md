# Whatsmeow.Client

WhatsApp's multi-device protocol for .NET, through [whatsmeow](https://github.com/tulir/whatsmeow).
The client starts `whatsmeow-net`, a Go sidecar that comes with this package, supervises it, and gives you one `IWhatsAppClient` per WhatsApp account: pairing, events, messages, media, groups, contacts, presence, chat state and history.
It is Native AOT and trimming safe.

This is not an official WhatsApp client and has nothing to do with WhatsApp or Meta.

## Quick start

One session that answers every text with the same text; the first run shows a QR code to scan with the phone, drawn with QRCoder:

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
						await console.WriteLineAsync(AsciiQRCodeHelper.GetQRCode(code.Code, QRCodeGenerator.ECCLevel.L, drawQuietZones: true, invert: true), cancellationToken).ConfigureAwait(false);
				}

				break;
			case SessionState.Disconnected:
				await client.ConnectAsync(cancellationToken).ConfigureAwait(false);
				break;
			default:
				await console.WriteLineAsync($"The session is {state}, so it neither pairs nor connects.", cancellationToken).ConfigureAwait(false);
				break;
		}
	}

	private static bool IsBroadcast(Jid chat) => string.Equals(chat.Server, "broadcast", StringComparison.Ordinal);

	/// <summary>The echo's id, from the session, the chat and the message's id, in the only format a send takes: <c>3EB0</c> and 18 uppercase hex digits.</summary>
	private static MessageId EchoId(MessageReceived e) =>
		MessageId.From("3EB0" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{e.SessionId.Value}\n{e.Source.Chat}\n{e.Id.Value}")))[..18]);
}
```

Once pairing yields `Paired`, the session is connected and its events flow: there is no `ConnectAsync` after it.

## Guarantees

Decrypted messages, receipts except retry receipts, group notifications and history reach your handlers at least once, through a crash of either process; undecryptable messages, presence, typing, contact and chat state changes are best effort.
whatsmeow answers an undecryptable message only once the sidecar's handler returned: an ack after it recorded the message, or without recording one the client could not read, and a nack for a bot message whose secret is missing; whether WhatsApp sends it again after a failed recording is not measured, so it stays best effort.
Handlers must be idempotent, with `SessionId` plus `MessageId` as the key.
[Delivery](https://github.com/tcortega/Whatsmeow.NET/blob/main/docs/guides/delivery.md) has the details and the evidence.

## More

* [`Whatsmeow.Extensions.Hosting`](https://www.nuget.org/packages/Whatsmeow.Extensions.Hosting) for dependency injection and a generic host.
* [The guides](https://github.com/tcortega/Whatsmeow.NET/blob/main/docs/guides/README.md): pairing, delivery, messaging, media, groups, deployment, security and more.
* License: MPL-2.0.
  The sidecar binary it brings is GPL-3.0-only; see [Licensing](https://github.com/tcortega/Whatsmeow.NET/blob/main/docs/guides/licensing.md).
