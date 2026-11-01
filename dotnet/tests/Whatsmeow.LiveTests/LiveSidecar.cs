using Microsoft.Extensions.Logging.Testing;
using Microsoft.Extensions.Options;
using QRCoder;
using Whatsmeow.IntegrationTests;

namespace Whatsmeow.LiveTests;

/// <summary>
/// A release <c>whatsmeow-net</c> against real WhatsApp, with a database of its own, started when a live test first
/// needs it; nothing starts while the live tests are off. What a person must see goes to files in
/// <see cref="OutputDirectory"/>: <c>qr.png</c> and <c>qr.txt</c>, the current QR code, and <c>pairing-code.txt</c>.
/// The sidecar's log goes to <c>sidecar.log</c> there, with the test number redacted. Each session a test made is logged
/// out at the end, so the phone keeps no linked device.
/// </summary>
public sealed class LiveSidecar : IAsyncLifetime
{
	private const string Redacted = "<redacted phone number>";

	private readonly SemaphoreSlim _starting = new(1, 1);
	private readonly List<IWhatsAppClient> _clients = [];
	private readonly Lock _logLock = new();
	private string? _directory;
	private StreamWriter? _log;
	private ILoggerFactory? _loggerFactory;
	private WhatsmeowSidecar? _sidecar;

	/// <summary><c>dotnet/artifacts/live/</c>, which git ignores.</summary>
	public string OutputDirectory { get; } = Path.Combine(new SidecarBinaries().Root, "dotnet", "artifacts", "live");

	public ValueTask InitializeAsync() => ValueTask.CompletedTask;

	/// <summary>A new session on the live sidecar, logged out at the end.</summary>
	public async Task<IWhatsAppClient> NewSessionAsync(string id, CancellationToken cancellationToken)
	{
		var sidecar = await StartAsync(cancellationToken).ConfigureAwait(false);
		var client = await sidecar.Sessions.GetOrCreateAsync(SessionId.From(id), cancellationToken).ConfigureAwait(false);
		_clients.Add(client);
		return client;
	}

	/// <summary>Shows a QR code: <c>qr.png</c> to scan, and <c>qr.txt</c>, its payload. Each code replaces the previous one.</summary>
	public async Task ShowQrCodeAsync(string code, CancellationToken cancellationToken)
	{
		using var data = QRCodeGenerator.GenerateQrCode(code, QRCodeGenerator.ECCLevel.L);
		using var renderer = new PngByteQRCode(data);
		var png = renderer.GetGraphic(10);
		await File.WriteAllBytesAsync(Path.Combine(OutputDirectory, "qr.png"), png, cancellationToken).ConfigureAwait(false);
		await File.WriteAllTextAsync(Path.Combine(OutputDirectory, "qr.txt"), code, cancellationToken).ConfigureAwait(false);
	}

	/// <summary>Shows the code to enter on the phone, in <c>pairing-code.txt</c>.</summary>
	public Task ShowPairingCodeAsync(string code, CancellationToken cancellationToken) =>
		File.WriteAllTextAsync(Path.Combine(OutputDirectory, "pairing-code.txt"), code, cancellationToken);

	private async Task<WhatsmeowSidecar> StartAsync(CancellationToken cancellationToken)
	{
		await _starting.WaitAsync(cancellationToken).ConfigureAwait(false);
		try
		{
			if (_sidecar is not null)
				return _sidecar;

			Directory.CreateDirectory(OutputDirectory);
			foreach (var shown in new[] { "qr.png", "qr.txt", "pairing-code.txt" })
				File.Delete(Path.Combine(OutputDirectory, shown));

			_directory = Directory.CreateTempSubdirectory("wmn-live-").FullName;
			var binary = await SidecarBinaries
				.BuildAsync("sidecar", "./cmd/whatsmeow-net", $"-X main.version={SidecarBinaries.ClientVersion}", "", _directory, cancellationToken)
				.ConfigureAwait(false);

			_log = new StreamWriter(Path.Combine(OutputDirectory, "sidecar.log"), append: false) { AutoFlush = true };
			var collector = new FakeLogCollector(Options.Create(new FakeLogCollectorOptions { OutputSink = WriteLog }));
			_loggerFactory = LoggerFactory.Create(builder => builder.AddProvider(new FakeLoggerProvider(collector)).SetMinimumLevel(LogLevel.Trace));

			var options = new WhatsmeowSidecarOptions { SidecarPath = binary, DatabasePath = Path.Combine(_directory, "whatsmeow-net.db") };
			_sidecar = await WhatsmeowSidecar.StartAsync(options, _loggerFactory, cancellationToken).ConfigureAwait(false);
			return _sidecar;
		}
		finally
		{
			_ = _starting.Release();
		}
	}

	/// <summary>
	/// Writes a log line with the test number redacted: whatsmeow logs every node it sends at debug level
	/// (client.go:990), and PairPhone's request carries the number (pair-code.go:103-111).
	/// </summary>
	private void WriteLog(string line)
	{
		var digits = new string([.. LiveSettings.PhoneNumber.Where(char.IsAsciiDigit)]);
		var redacted = line.Replace(LiveSettings.PhoneNumber, Redacted, StringComparison.Ordinal);
		if (digits.Length > 0)
			redacted = redacted.Replace(digits, Redacted, StringComparison.Ordinal);

		lock (_logLock)
			_log?.WriteLine(redacted);
	}

	public async ValueTask DisposeAsync()
	{
		foreach (var client in _clients)
		{
			try
			{
				await client.LogoutAsync(TestContext.Current.CancellationToken).ConfigureAwait(false);
			}
			catch (WhatsmeowException e)
			{
				// Cleanup only: a session that never paired, or is no longer connected, has nothing to unlink.
				WriteLog($"logging out {client.SessionId} after the live tests: {e.ErrorCode}");
			}
		}

		if (_sidecar is not null)
			await _sidecar.DisposeAsync().ConfigureAwait(false);
		_loggerFactory?.Dispose();
		lock (_logLock)
		{
			_log?.Dispose();
			_log = null;
		}

		if (_directory is not null)
			Directory.Delete(_directory, recursive: true);
		_starting.Dispose();
	}
}
