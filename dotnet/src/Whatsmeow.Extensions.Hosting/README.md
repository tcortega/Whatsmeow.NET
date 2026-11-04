# Whatsmeow.Extensions.Hosting

Dependency injection and a generic host for [`Whatsmeow.Client`](https://www.nuget.org/packages/Whatsmeow.Client).
`AddWhatsmeow()` binds the options from the `Whatsmeow` configuration section, starts the sidecar with the host and stops it after every hosted service, declares sessions as keyed `IWhatsAppClient` services, and runs `IWhatsAppEventHandler<TEvent>` handlers in a DI scope per event.

This is not an official WhatsApp client and has nothing to do with WhatsApp or Meta.

## Quick start

Two sessions that answer every text with the same text:

<!-- snippet: dotnet/samples/EchoBot/EchoBotServices.cs -->
```csharp
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

The full sample, with its QR code pairing and OpenTelemetry, is [`dotnet/samples/EchoBot`](https://github.com/tcortega/Whatsmeow.NET/blob/main/dotnet/samples/EchoBot).
[Hosting and testing](https://github.com/tcortega/Whatsmeow.NET/blob/main/docs/guides/hosting.md) covers configuration, keyed sessions and the host's lifecycle.

License: MPL-2.0.
