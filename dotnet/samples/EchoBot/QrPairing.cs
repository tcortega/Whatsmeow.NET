using QRCoder;
using Whatsmeow.Pairing;
using Whatsmeow.Sessions;

namespace EchoBot;

/// <summary>Pairs each session that is not paired when the host starts, one after the other, with a QR code on the console.</summary>
public sealed partial class QrPairing([FromKeyedServices(EchoBotServices.Alice)] IWhatsAppClient alice, [FromKeyedServices(EchoBotServices.Bob)] IWhatsAppClient bob, ILogger<QrPairing> logger)
	: BackgroundService
{
	protected override async Task ExecuteAsync(CancellationToken stoppingToken)
	{
		foreach (var client in (IWhatsAppClient[])[alice, bob])
		{
			// A paired session is connected by the host; pairing it again would fail with AlreadyPaired.
			if (await client.WatchStateAsync(stoppingToken).FirstAsync(stoppingToken).ConfigureAwait(false) is not (SessionState.Created or SessionState.LoggedOut))
				continue;

			await foreach (var update in client.PairWithQrCodeAsync(stoppingToken).ConfigureAwait(false))
			{
				switch (update)
				{
					case PairingUpdate.QrCode code:
						// Only the picture: the code's text carries the device's secret and never goes to a log.
						ShowingQrCode(logger, client.SessionId.Value);
						Console.WriteLine(AsciiQRCodeHelper.GetQRCode(code.Code, QRCodeGenerator.ECCLevel.L, drawQuietZones: true, invert: true));
						break;
					case PairingUpdate.Paired:
						Paired(logger, client.SessionId.Value);
						break;
					default:
						break;
				}
			}

			// Paired, the session is connected; its events reach the handlers from its first connect in this process.
			await client.ConnectAsync(stoppingToken).ConfigureAwait(false);
		}
	}

	[LoggerMessage(10, LogLevel.Information, "Scan the QR code below with the phone of session {SessionId}: WhatsApp, Linked devices, Link a device")]
	private static partial void ShowingQrCode(ILogger logger, string sessionId);

	[LoggerMessage(11, LogLevel.Information, "Session {SessionId} is paired")]
	private static partial void Paired(ILogger logger, string sessionId);
}
