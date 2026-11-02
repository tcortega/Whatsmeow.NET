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
