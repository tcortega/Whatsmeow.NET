using System.Security.Cryptography;
using System.Text;
using QRCoder;
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
			// Never the account's own texts, nor an echo: two sessions that text each other would echo forever.
			if (e.Source.IsFromMe || e.Content is not TextContent { Text: var text } || text.StartsWith(Prefix, StringComparison.Ordinal))
				return;

			// A message can come again, after a crash before its ack; the echo's id is the same each time, and a send
			// of an id already sent is never sent again, so each text is echoed once.
			_ = await client.SendTextAsync(e.Source.Chat, Prefix + text, EchoId(e), cancellationToken: ct).ConfigureAwait(false);
		});

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

		return echo;
	}

	/// <summary>The echo's id, from the session, the chat and the message's id, in the only format a send takes: <c>3EB0</c> and 18 uppercase hex digits.</summary>
	private static MessageId EchoId(MessageReceived e) =>
		MessageId.From("3EB0" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{e.SessionId.Value}\n{e.Source.Chat}\n{e.Id.Value}")))[..18]);
}
