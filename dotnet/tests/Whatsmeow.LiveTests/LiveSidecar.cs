using Microsoft.Extensions.Logging.Testing;
using Microsoft.Extensions.Options;
using QRCoder;
using Whatsmeow.IntegrationTests;
using Whatsmeow.IntegrationTests.Sessions;
using Whatsmeow.IntegrationTests.Support;
using Whatsmeow.Sessions;

namespace Whatsmeow.LiveTests;

/// <summary>
/// A release <c>whatsmeow-net</c> against real WhatsApp on the persistent live database
/// (<see cref="LiveSettings.DatabaseVariable"/>), started when a live test first needs it; nothing starts while the
/// live tests are off. The database holds the <see cref="LiveSessionId"/> session, paired once and reused by every
/// run: it is never logged out, deleted or copied. A throwaway session (<see cref="ThrowawaySessionAsync"/>) is logged
/// out and deleted at the end, so the phone keeps only the live session's linked device.
/// What a person must see goes to files in <see cref="OutputDirectory"/>, deleted at the end: <c>qr.png</c>, the
/// current QR code, and <c>pairing-code.txt</c>. The sidecar's log goes to <c>sidecar.log</c> there, with phone numbers
/// redacted.
/// </summary>
public sealed class LiveSidecar : IAsyncLifetime
{
	/// <summary>The persistent live session's id.</summary>
	public const string LiveSessionId = "live";

	private readonly SemaphoreSlim _starting = new(1, 1);
	private readonly List<IWhatsAppClient> _throwaway = [];
	private readonly Lock _logLock = new();
	private string? _directory;
	private StreamWriter? _log;
	private ILoggerFactory? _loggerFactory;
	private WhatsmeowSidecar? _sidecar;

	/// <summary><c>dotnet/artifacts/live/</c>, which git ignores.</summary>
	public string OutputDirectory { get; } = Path.Combine(new SidecarBinaries().Root, "dotnet", "artifacts", "live");

	public ValueTask InitializeAsync() => ValueTask.CompletedTask;

	/// <summary>A session on the live sidecar, created unpaired when the database has none with this id.</summary>
	public async Task<IWhatsAppClient> SessionAsync(string id, CancellationToken cancellationToken)
	{
		var sidecar = await StartAsync(cancellationToken).ConfigureAwait(false);
		return await sidecar.Sessions.GetOrCreateAsync(SessionId.From(id), cancellationToken).ConfigureAwait(false);
	}

	/// <summary>
	/// A throwaway session, unpaired: one an earlier run left behind is discarded first. It is discarded again at
	/// the end. Never the live session.
	/// </summary>
	public async Task<IWhatsAppClient> ThrowawaySessionAsync(string id, CancellationToken cancellationToken)
	{
		ArgumentOutOfRangeException.ThrowIfEqual(id, LiveSessionId);
		await DiscardAsync(await SessionAsync(id, cancellationToken).ConfigureAwait(false), cancellationToken).ConfigureAwait(false);
		var client = await SessionAsync(id, cancellationToken).ConfigureAwait(false);
		_throwaway.Add(client);
		return client;
	}

	/// <summary>Connects a paired session and returns its state once WhatsApp accepted the login.</summary>
	public static async Task<SessionState> ConnectAsync(IWhatsAppClient client, CancellationToken cancellationToken)
	{
		await client.ConnectAsync(cancellationToken).ConfigureAwait(false);
		var watch = StateWatch.Start(client, cancellationToken);
		await using (watch.ConfigureAwait(false))
		{
			var state = await watch.NextAsync().ConfigureAwait(false);
			while (state is SessionState.Disconnected or SessionState.Connecting or SessionState.Reconnecting)
				state = await watch.NextAsync().ConfigureAwait(false);
			return state;
		}
	}

	/// <summary>
	/// Shows a QR code: <c>qr.png</c> to scan. Each code replaces the previous one. Its payload carries the device's
	/// ADV secret (pair.go:125-130), so it is never written as text.
	/// </summary>
	public async Task ShowQrCodeAsync(string code, CancellationToken cancellationToken)
	{
		using var data = QRCodeGenerator.GenerateQrCode(code, QRCodeGenerator.ECCLevel.L);
		using var renderer = new PngByteQRCode(data);
		var png = renderer.GetGraphic(10);
		await File.WriteAllBytesAsync(Path.Combine(OutputDirectory, "qr.png"), png, cancellationToken).ConfigureAwait(false);
	}

	/// <summary>
	/// Keeps the session connected until WhatsApp sent the backlog (CaughtUp), at most two minutes, and returns how long
	/// that took. A device that went offline about a second after Paired stayed "connecting" on the phone, and one held
	/// online got CaughtUp 5 s after Connected [observed: the owner's phone, 2026-11-01]; that a new device must stay
	/// online through its first sync is [inferred].
	/// </summary>
	public async Task<TimeSpan> StayUntilCaughtUpAsync(IWhatsAppClient client, CancellationToken cancellationToken)
	{
#pragma warning disable RS0030 // Justification: a live test measures and bounds real time; nothing here can be faked.
		var time = TimeProvider.System;
#pragma warning restore RS0030
		var started = time.GetTimestamp();
		using var bound = new CancellationTokenSource(TimeSpan.FromMinutes(2), time);
		using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, bound.Token);
		var watch = StateWatch.Start(client, linked.Token);
		await using (watch.ConfigureAwait(false))
			await watch
				.ReachesAsync(new SessionState.CaughtUp(), new SessionState.Connected(), new SessionState.Connecting(), new SessionState.Reconnecting())
				.ConfigureAwait(false);

		var elapsed = time.GetElapsedTime(started);
		WriteLog($"live: CaughtUp {elapsed.TotalSeconds:F1} s after the session was usable");
		return elapsed;
	}

	/// <summary>Shows the code to enter on the phone, in <c>pairing-code.txt</c>.</summary>
	public Task ShowPairingCodeAsync(string code, CancellationToken cancellationToken) =>
		File.WriteAllTextAsync(Path.Combine(OutputDirectory, "pairing-code.txt"), code, cancellationToken);

	private async Task<WhatsmeowSidecar> StartAsync(CancellationToken cancellationToken)
	{
		// Never against a database by accident: only with the opt-in set (docs/guidelines/tests.md).
		if (!LiveSettings.Enabled)
			throw new InvalidOperationException($"The live tests run only with {LiveSettings.DatabaseVariable} set.");

		await _starting.WaitAsync(cancellationToken).ConfigureAwait(false);
		try
		{
			if (_sidecar is not null)
				return _sidecar;

			_ = Directory.CreateDirectory(OutputDirectory);
			DeleteShown();

			// The account's keys: owner-only, outside the repository (the sidecar creates the file 0600).
			var databaseDirectory = Path.GetDirectoryName(Path.GetFullPath(LiveSettings.Database))!;
			_ = OperatingSystem.IsWindows()
				? Directory.CreateDirectory(databaseDirectory)
				: Directory.CreateDirectory(databaseDirectory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

			_directory = Directory.CreateTempSubdirectory("wmn-live-").FullName;
			var binary = await SidecarBinaries
				.BuildAsync("sidecar", "./cmd/whatsmeow-net", $"-X main.version={SidecarBinaries.ClientVersion}", "", _directory, cancellationToken)
				.ConfigureAwait(false);

			_log = new StreamWriter(Path.Combine(OutputDirectory, "sidecar.log"), append: false) { AutoFlush = true };
			var collector = new FakeLogCollector(Options.Create(new FakeLogCollectorOptions { OutputSink = WriteLog }));
			_loggerFactory = LoggerFactory.Create(builder => builder.AddProvider(new FakeLoggerProvider(collector)).SetMinimumLevel(LogLevel.Trace));

			var options = new WhatsmeowSidecarOptions { SidecarPath = binary, DatabasePath = LiveSettings.Database };
			_sidecar = await WhatsmeowSidecar.StartAsync(options, _loggerFactory, cancellationToken).ConfigureAwait(false);
			return _sidecar;
		}
		finally
		{
			_ = _starting.Release();
		}
	}

	/// <summary>
	/// Unlinks a throwaway session's device from the phone and deletes the session: a paired session is connected
	/// first, because WhatsApp is told of a logout only over a connection.
	/// </summary>
	private async Task DiscardAsync(IWhatsAppClient client, CancellationToken cancellationToken)
	{
		var state = await SessionSetup.StateAsync(client, cancellationToken).ConfigureAwait(false);
		if (state is SessionState.Disconnected)
			state = await ConnectAsync(client, cancellationToken).ConfigureAwait(false);
		if (state is SessionState.Connected or SessionState.CaughtUp)
			await client.LogoutAsync(cancellationToken).ConfigureAwait(false);
		await _sidecar!.Sessions.DeleteAsync(client.SessionId, cancellationToken).ConfigureAwait(false);
	}

	private void DeleteShown()
	{
		foreach (var shown in new[] { "qr.png", "pairing-code.txt" })
			File.Delete(Path.Combine(OutputDirectory, shown));
	}

	/// <summary>Writes a log line with every JID's user part and the test number redacted (<see cref="LogRedaction"/>).</summary>
	private void WriteLog(string line)
	{
		var redacted = LogRedaction.Redact(line, LiveSettings.PhoneNumber);
		lock (_logLock)
			_log?.WriteLine(redacted);
	}

	public async ValueTask DisposeAsync()
	{
		foreach (var client in _throwaway)
			await DiscardAsync(client, CancellationToken.None).ConfigureAwait(false);

		if (_sidecar is not null)
			await _sidecar.DisposeAsync().ConfigureAwait(false);
		_loggerFactory?.Dispose();
		lock (_logLock)
		{
			_log?.Dispose();
			_log = null;
		}

		// The build directory and what was shown only, when this run started anything (xUnit disposes the fixture of
		// tests it did not run too): the live database stays.
		if (_directory is not null)
		{
			Directory.Delete(_directory, recursive: true);
			DeleteShown();
		}

		_starting.Dispose();
	}
}
