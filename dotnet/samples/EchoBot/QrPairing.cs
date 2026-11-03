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
			// One session's pairing that fails, a code nobody scanned in time included, leaves the other's to go on.
			try
			{
				await PairAsync(client, stoppingToken).ConfigureAwait(false);
			}
			catch (WhatsmeowException exception)
			{
				PairingFailed(logger, exception, client.SessionId.Value, exception.ErrorCode);
			}
		}
	}

	private async Task PairAsync(IWhatsAppClient client, CancellationToken cancellationToken)
	{
		// A paired session is connected by the host; pairing it again would fail with AlreadyPaired.
		if (await client.WatchStateAsync(cancellationToken).FirstAsync(cancellationToken).ConfigureAwait(false) is not (SessionState.Created or SessionState.LoggedOut))
			return;

		await foreach (var update in client.PairWithQrCodeAsync(cancellationToken).ConfigureAwait(false))
		{
			switch (update)
			{
				case PairingUpdate.QrCode code:
					// Only the picture: the code's text carries the device's secret and never goes to a log.
					ShowingQrCode(logger, client.SessionId.Value);
					Console.WriteLine(AsciiQRCodeHelper.GetQRCode(code.Code, QRCodeGenerator.ECCLevel.L, drawQuietZones: true, invert: true));
					break;
				case PairingUpdate.Paired:
					// The session is connected, and its events reach the handlers with no ConnectAsync.
					Paired(logger, client.SessionId.Value);
					break;
				default:
					break;
			}
		}
	}

	[LoggerMessage(10, LogLevel.Information, "Scan the QR code below with the phone of session {SessionId}: WhatsApp, Linked devices, Link a device")]
	private static partial void ShowingQrCode(ILogger logger, string sessionId);

	[LoggerMessage(11, LogLevel.Information, "Session {SessionId} is paired")]
	private static partial void Paired(ILogger logger, string sessionId);

	[LoggerMessage(12, LogLevel.Warning, "Pairing session {SessionId} failed with {ErrorCode}; restart to pair it")]
	private static partial void PairingFailed(ILogger logger, Exception exception, string sessionId, WhatsmeowErrorCode errorCode);
}
