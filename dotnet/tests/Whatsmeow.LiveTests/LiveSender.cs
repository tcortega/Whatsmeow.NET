using Whatsmeow.IntegrationTests.Pairing;
using Whatsmeow.IntegrationTests.Sessions;
using Whatsmeow.Messaging;
using Whatsmeow.Pairing;

namespace Whatsmeow.LiveTests;

/// <summary>
/// A second, throwaway companion of the same account, <see cref="SessionIdValue"/>, so the live tests make the
/// incoming messages themselves: what it sends to the account's own chat reaches the live session as the account's
/// own message, IsFromMe (whatsmeow encrypts a message for every device of the sender's account but the sending one,
/// send.go:889, 1314-1316) [inferred until the first live run]. It runs in a sidecar process and a database of its
/// own, so killing the live sidecar never touches it. It is paired by QR code (<c>qr.png</c>) when a test first needs
/// it, the owner's scan being the consent, and at the end it is logged out, deleted and checked gone, and its database
/// deleted, also when a test failed (ADR 0029).
/// </summary>
public sealed class LiveSender : IAsyncLifetime
{
	/// <summary>The sender's session id.</summary>
	public const string SessionIdValue = "live-sender";

	private readonly SemaphoreSlim _starting = new(1, 1);
	private string? _directory;
	private LiveLog? _log;
	private WhatsmeowSidecar? _sidecar;
	private IWhatsAppClient? _client;

	public ValueTask InitializeAsync() => ValueTask.CompletedTask;

	/// <summary>The sender, paired by QR code the first time (the owner scans <c>qr.png</c>), connected and CaughtUp.</summary>
	public async Task<IWhatsAppClient> PairedAsync(LiveSidecar live, CancellationToken cancellationToken)
	{
		await _starting.WaitAsync(cancellationToken).ConfigureAwait(false);
		try
		{
			if (_client is not null)
				return _client;

			var binary = await live.BinaryAsync(cancellationToken).ConfigureAwait(false);
			// Owner-only on Unix (Directory.CreateTempSubdirectory): the throwaway device's keys.
			_directory = Directory.CreateTempSubdirectory("wmn-sender-").FullName;
			_log = new LiveLog(Path.Combine(live.OutputDirectory, "sender.log"));
			var options = new WhatsmeowSidecarOptions { SidecarPath = binary, DatabasePath = Path.Combine(_directory, "sender.db") };
			_sidecar = await WhatsmeowSidecar.StartAsync(options, _log.Capture.Factory, cancellationToken).ConfigureAwait(false);
			var client = await _sidecar.Sessions.GetOrCreateAsync(SessionId.From(SessionIdValue), cancellationToken).ConfigureAwait(false);

			live.Write("live: scan qr.png with the phone to link the throwaway sender (WhatsApp, Linked devices, Link a device)");
			var updates = new List<PairingUpdate>();
			await foreach (var update in client.PairWithQrCodeAsync(cancellationToken).ConfigureAwait(false))
			{
				updates.Add(update);
				if (update is PairingUpdate.QrCode qrCode)
					await live.ShowQrCodeAsync(qrCode.Code, cancellationToken).ConfigureAwait(false);
			}

			live.DeleteShown();
			PairingChecks.ValidateQrPairing(updates, await SessionSetup.StateAsync(client, cancellationToken).ConfigureAwait(false));
			_ = await LiveSidecar.StayUntilCaughtUpAsync(client, live.Write, cancellationToken).ConfigureAwait(false);
			_client = client;
			return client;
		}
		finally
		{
			_ = _starting.Release();
		}
	}

	/// <summary>Sends each message of the burst to <paramref name="chat"/>, in order, through the paced send queue.</summary>
	public static async Task<IReadOnlyList<SendResult>> SendAsync(IWhatsAppClient sender, Jid chat, Burst burst, CancellationToken cancellationToken)
	{
		var results = new List<SendResult>(burst.Messages.Count);
		foreach (var (text, id) in burst.Messages)
			results.Add(await sender.SendTextAsync(chat, text, id, cancellationToken).ConfigureAwait(false));
		return results;
	}

	public async ValueTask DisposeAsync()
	{
		try
		{
			if (_sidecar is not null)
			{
				var client = await _sidecar.Sessions.GetOrCreateAsync(SessionId.From(SessionIdValue), CancellationToken.None).ConfigureAwait(false);
				await LiveSidecar.DiscardAsync(client, _sidecar, CancellationToken.None).ConfigureAwait(false);
				_log?.Write("live: the throwaway sender is logged out and deleted");
			}
		}
		finally
		{
			if (_sidecar is not null)
				await _sidecar.DisposeAsync().ConfigureAwait(false);
			_log?.Dispose();

			// The sender's keys go with its database.
			if (_directory is not null)
				Directory.Delete(_directory, recursive: true);
			_starting.Dispose();
		}
	}
}
