using Whatsmeow.Events;
using Whatsmeow.Extensions.Hosting;

namespace EchoBot;

/// <summary>Answers each text a session receives with the same text, in the same chat.</summary>
public sealed partial class EchoHandler(ILogger<EchoHandler> logger) : IWhatsAppEventHandler<MessageReceived>
{
	private const string Prefix = "echo: ";

	public async ValueTask HandleAsync(MessageReceived e, IWhatsAppClient client, CancellationToken cancellationToken)
	{
		// Never the account's own texts, nor an echo: two sessions that text each other would echo each other forever.
		if (e.Source.IsFromMe || e.Content is not TextContent { Text: var text } || text.StartsWith(Prefix, StringComparison.Ordinal))
			return;

		_ = await client.SendTextAsync(e.Source.Chat, Prefix + text, cancellationToken: cancellationToken).ConfigureAwait(false);
		Echoed(logger, e.Id.Value, e.SessionId.Value, e.Timestamp);
	}

	[LoggerMessage(1, LogLevel.Information, "Echoed {MessageId} of session {SessionId}, sent at {Timestamp:O}")]
	private static partial void Echoed(ILogger logger, string messageId, string sessionId, DateTimeOffset timestamp);
}
