using System.Diagnostics;
using System.Threading.Channels;
using QRCoder;
using Whatsmeow.Events;
using Whatsmeow.IntegrationTests;
using Whatsmeow.IntegrationTests.Events;
using Whatsmeow.IntegrationTests.Pairing;
using Whatsmeow.IntegrationTests.Sessions;
using Whatsmeow.IntegrationTests.Support;
using Whatsmeow.Sessions;

namespace Whatsmeow.LiveTests;

/// <summary>
/// The live tests' <c>whatsmeow-net</c>, a release build with the live seams (<c>-tags livetest</c>, ADR 0032), against
/// real WhatsApp on the persistent live database (<see cref="LiveSettings.DatabaseVariable"/>), started when a live
/// test first needs it; nothing starts while the live tests are off. The database holds the
/// <see cref="LiveSessionId"/> session, paired once and reused by every run: it is never logged out or deleted, and
/// copied only into the backup each run takes before its first start (<see cref="StoreBackup"/>).
/// A throwaway session (<see cref="ThrowawaySessionAsync"/>) is logged out and deleted at the end, so the phone keeps
/// only the live session's linked device.
/// What a person must see goes to files in <see cref="OutputDirectory"/>, deleted at the end: <c>qr.png</c>, the
/// current QR code, and <c>pairing-code.txt</c>. The sidecar's log goes to <c>sidecar.log</c> there, with phone numbers
/// redacted.
/// </summary>
public sealed class LiveSidecar : IAsyncLifetime
{
	/// <summary>The persistent live session's id.</summary>
	public const string LiveSessionId = "live";

	/// <summary>What the owner sends to "Message yourself" from the phone (docs/guidelines/tests.md Live WhatsApp).</summary>
	public const string PingText = "wmn ping";

	/// <summary>How the owner answers a question (<see cref="AskOwnerAsync"/>): a text to "Message yourself" starting with one of these.</summary>
	public const string AnswerHint = "answer with \"wmn yes\" or \"wmn no\", and what you saw, to Message yourself";

	/// <summary>How long a test waits for the owner's ping, or for an answer.</summary>
	internal static TimeSpan PingBound { get; } = TimeSpan.FromMinutes(5);

	private readonly SemaphoreSlim _starting = new(1, 1);
	private readonly List<string> _throwaway = [];
	private readonly TaskCompletionSource<MessageReceived> _ping = new(TaskCreationOptions.RunContinuationsAsynchronously);
	private readonly Channel<MessageReceived> _answers = Channel.CreateUnbounded<MessageReceived>();
	private string? _directory;
	private string? _binary;
	private LiveLog? _log;
	private WhatsmeowSidecar? _sidecar;
	private IDisposable? _pingWatch;
	private bool _backedUp;

	/// <summary><c>dotnet/artifacts/live/</c>, which git ignores.</summary>
	public string OutputDirectory { get; } = Path.Combine(new SidecarBinaries().Root, "dotnet", "artifacts", "live");

	/// <summary>The live sidecar's log records, for a test that waits on one.</summary>
	internal LogCapture Logs => _log?.Capture ?? throw new InvalidOperationException("No live sidecar was started.");

	public ValueTask InitializeAsync() => ValueTask.CompletedTask;

	/// <summary>A session on the live sidecar, created unpaired when the database has none with this id.</summary>
	public async Task<IWhatsAppClient> SessionAsync(string id, CancellationToken cancellationToken)
	{
		var sidecar = await StartAsync(cancellationToken).ConfigureAwait(false);
		return await sidecar.Sessions.GetOrCreateAsync(SessionId.From(id), cancellationToken).ConfigureAwait(false);
	}

	/// <summary>
	/// The live session, connected and CaughtUp. It must be paired already: the QR test pairs it. The first call on a
	/// sidecar also starts watching for the owner's ping (<see cref="PingAsync"/>) and answers (<see cref="AskOwnerAsync"/>),
	/// before it connects, so a ping the phone sent while the session was offline is seen too.
	/// </summary>
	public async Task<IWhatsAppClient> LiveAsync(CancellationToken cancellationToken)
	{
		var client = await SessionAsync(LiveSessionId, cancellationToken).ConfigureAwait(false);
		_pingWatch ??= client.On<MessageReceived>(
			(e, token) =>
			{
				if (IsPing(e))
					_ = _ping.TrySetResult(e);
				if (IsAnswer(e))
					_ = _answers.Writer.TryWrite(e);
				return ValueTask.CompletedTask;
			}
		);

		var state = await SessionSetup.StateAsync(client, cancellationToken).ConfigureAwait(false);
		if (state is SessionState.Created or SessionState.LoggedOut)
			throw new InvalidOperationException("The live session is not paired: the QR test pairs it (docs/guidelines/tests.md Live WhatsApp).");
		if (state is SessionState.CaughtUp)
			return client;

		PairingChecks.ValidateUsable(await ConnectAsync(client, cancellationToken).ConfigureAwait(false));
		_ = await StayUntilCaughtUpAsync(client, cancellationToken).ConfigureAwait(false);
		return client;
	}

	/// <summary>
	/// The owner's ping: the first message the phone sent to its own chat with the text <see cref="PingText"/> (any
	/// case). Its chat is the account's own JID, and its id is one the phone made. Waits at most five minutes.
	/// </summary>
	public async Task<MessageReceived> PingAsync(CancellationToken cancellationToken)
	{
		if (!_ping.Task.IsCompleted)
			_ = await LiveAsync(cancellationToken).ConfigureAwait(false);
		Write($"live: waiting at most {PingBound.TotalMinutes:F0} min for the owner's \"{PingText}\" to Message yourself");
		return await _ping.Task.WaitAsync(PingBound, LiveClock.Time, cancellationToken).ConfigureAwait(false);
	}

	/// <summary>
	/// Asks the owner, who looks at a phone, what it shows: the question goes to <c>owner-question.txt</c>, the log and
	/// the test output, and the answer is the owner's next text to "Message yourself" that starts <c>wmn yes</c> or
	/// <c>wmn no</c> (<see cref="AnswerHint"/>), sent after the question, within five minutes. The answer is logged and
	/// returned; what it means is recorded, not checked.
	/// </summary>
	public async Task<string> AskOwnerAsync(string question, CancellationToken cancellationToken)
	{
		_ = await LiveAsync(cancellationToken).ConfigureAwait(false);
		// An answer sent before this question is not one to it.
		while (_answers.Reader.TryRead(out _)) { }

		var shown = Path.Combine(OutputDirectory, "owner-question.txt");
		await File.WriteAllTextAsync(shown, $"{question}{Environment.NewLine}({AnswerHint}){Environment.NewLine}", cancellationToken).ConfigureAwait(false);
		Write($"live: OWNER QUESTION: {question} ({AnswerHint}, within {PingBound.TotalMinutes:F0} min)");
		try
		{
			using var bound = LiveClock.Bound(PingBound, cancellationToken);
			var text = DeliveryChecks.TextOf(await _answers.Reader.ReadAsync(bound.Token).ConfigureAwait(false))!.Trim();
			Write($"live: the owner answered: {text}");
			return text;
		}
		catch (OperationCanceledException e) when (!cancellationToken.IsCancellationRequested)
		{
			Write($"live: the owner did not answer within {PingBound.TotalMinutes:F0} min");
			throw new TimeoutException($"The owner did not answer within {PingBound.TotalMinutes:F0} min: {question}", e);
		}
		finally
		{
			File.Delete(shown);
		}
	}

	/// <summary>The live session's account, from <see cref="SessionInfo"/> (<see cref="AccountOfAsync"/>).</summary>
	public async Task<(Jid Chat, Jid? Lid)> AccountAsync(CancellationToken cancellationToken) =>
		await AccountOfAsync(await StartAsync(cancellationToken).ConfigureAwait(false), SessionId.From(LiveSessionId), cancellationToken).ConfigureAwait(false);

	/// <summary>
	/// A paired session's account: its own chat, this device's JID without the device, and its LID the same way, null
	/// when WhatsApp sent none. It never creates the session.
	/// </summary>
	internal static async Task<(Jid Chat, Jid? Lid)> AccountOfAsync(WhatsmeowSidecar sidecar, SessionId id, CancellationToken cancellationToken)
	{
		var sessions = await sidecar.Sessions.ListAsync(cancellationToken).ConfigureAwait(false);
		if (sessions.SingleOrDefault(s => s.Id == id) is not { Jid: { } jid } info)
			throw new InvalidOperationException($"The session {id} is not paired (docs/guidelines/tests.md Live WhatsApp).");
		return (jid with { Device = 0 }, info.Lid is { } lid ? lid with { Device = 0 } : null);
	}

	/// <summary>
	/// A throwaway session, unpaired: one an earlier run left behind is discarded first. It is discarded again at
	/// the end. Never the live session.
	/// </summary>
	public async Task<IWhatsAppClient> ThrowawaySessionAsync(string id, CancellationToken cancellationToken)
	{
		ArgumentOutOfRangeException.ThrowIfEqual(id, LiveSessionId);
		await DiscardAsync(await SessionAsync(id, cancellationToken).ConfigureAwait(false), _sidecar!, cancellationToken).ConfigureAwait(false);
		var client = await SessionAsync(id, cancellationToken).ConfigureAwait(false);
		_throwaway.Add(id);
		return client;
	}

	/// <summary>The release build with the live seams, built once: <c>-tags livetest</c> (ADR 0032).</summary>
	public async Task<string> BinaryAsync(CancellationToken cancellationToken)
	{
		await _starting.WaitAsync(cancellationToken).ConfigureAwait(false);
		try
		{
			return await BuildAsync(cancellationToken).ConfigureAwait(false);
		}
		finally
		{
			_ = _starting.Release();
		}
	}

	/// <summary>
	/// Stops the live sidecar gracefully, so another process can own the live database; the next
	/// <see cref="SessionAsync"/> starts it again. The ping already seen is kept.
	/// </summary>
	public async Task StopAsync()
	{
		if (_sidecar is not null)
			await _sidecar.DisposeAsync().ConfigureAwait(false);
		_sidecar = null;
		_pingWatch = null;
	}

	/// <summary>Kills the live sidecar's process, as a crash would, and waits until the client restarted it (ARCHITECTURE §8.5).</summary>
	public async Task CrashAsync(CancellationToken cancellationToken)
	{
		var restarts = Logs.Named("SidecarRestarted").Count;
		var (processId, _) = await Logs.StartedAsync(Logs.Named("SidecarStarted").Count, cancellationToken).ConfigureAwait(false);

		using (var process = Process.GetProcessById(processId))
			process.Kill();

		_ = await Logs.WaitForAsync(_ => Logs.Named("SidecarRestarted").Count > restarts, cancellationToken).ConfigureAwait(false);
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
		_ = Directory.CreateDirectory(OutputDirectory);
		using var data = QRCodeGenerator.GenerateQrCode(code, QRCodeGenerator.ECCLevel.L);
		using var renderer = new PngByteQRCode(data);
		var png = renderer.GetGraphic(10);
		await File.WriteAllBytesAsync(Path.Combine(OutputDirectory, "qr.png"), png, cancellationToken).ConfigureAwait(false);
	}

	/// <summary>Deletes what a person was shown, once a pairing ended.</summary>
	public void DeleteShown()
	{
		foreach (var shown in new[] { "qr.png", "pairing-code.txt", "owner-question.txt" })
			File.Delete(Path.Combine(OutputDirectory, shown));
	}

	/// <summary>
	/// Keeps the session connected until WhatsApp sent the backlog (CaughtUp), at most two minutes, and returns how long
	/// that took. A device that went offline about a second after Paired stayed "connecting" on the phone, and one held
	/// online got CaughtUp 5 s after Connected [observed: the owner's phone, 2026-11-01]; that a new device must stay
	/// online through its first sync is [inferred].
	/// </summary>
	public Task<TimeSpan> StayUntilCaughtUpAsync(IWhatsAppClient client, CancellationToken cancellationToken) =>
		StayUntilCaughtUpAsync(client, Write, cancellationToken);

	/// <inheritdoc cref="StayUntilCaughtUpAsync(IWhatsAppClient, CancellationToken)"/>
	internal static async Task<TimeSpan> StayUntilCaughtUpAsync(IWhatsAppClient client, Action<string> log, CancellationToken cancellationToken)
	{
		var started = LiveClock.Time.GetTimestamp();
		using var bound = LiveClock.Bound(TimeSpan.FromMinutes(2), cancellationToken);
		var watch = StateWatch.Start(client, bound.Token);
		await using (watch.ConfigureAwait(false))
			await watch
				.ReachesAsync(new SessionState.CaughtUp(), new SessionState.Connected(), new SessionState.Connecting(), new SessionState.Reconnecting())
				.ConfigureAwait(false);

		var elapsed = LiveClock.Time.GetElapsedTime(started);
		log($"live: {client.SessionId} CaughtUp {elapsed.TotalSeconds:F1} s after the session was usable");
		return elapsed;
	}

	/// <summary>Shows the code to enter on the phone, in <c>pairing-code.txt</c>.</summary>
	public Task ShowPairingCodeAsync(string code, CancellationToken cancellationToken) =>
		File.WriteAllTextAsync(Path.Combine(OutputDirectory, "pairing-code.txt"), code, cancellationToken);

	/// <summary>Writes a measurement to <c>sidecar.log</c>, redacted, and to the running test's output.</summary>
	public void Write(string line)
	{
		_log?.Write(line);
		TestContext.Current.TestOutputHelper?.WriteLine(LogRedaction.Redact(line, LiveSettings.PhoneNumber));
	}

	/// <summary>
	/// Unlinks a throwaway session's device from the phone and deletes the session: a paired session is connected
	/// first, because WhatsApp is told of a logout only over a connection. It then checks the session is gone.
	/// </summary>
	internal static async Task DiscardAsync(IWhatsAppClient client, WhatsmeowSidecar sidecar, CancellationToken cancellationToken)
	{
		if (client.SessionId == SessionId.From(LiveSessionId) || client.SessionId == SessionId.From(LivePeer.SessionIdValue))
			throw new InvalidOperationException("The live and peer sessions are never logged out or deleted.");
		var state = await SessionSetup.StateAsync(client, cancellationToken).ConfigureAwait(false);
		if (state is SessionState.Disconnected)
			state = await ConnectAsync(client, cancellationToken).ConfigureAwait(false);
		if (state is SessionState.Connected or SessionState.CaughtUp)
			await client.LogoutAsync(cancellationToken).ConfigureAwait(false);
		await sidecar.Sessions.DeleteAsync(client.SessionId, cancellationToken).ConfigureAwait(false);
		Assert.DoesNotContain(await sidecar.Sessions.ListAsync(cancellationToken).ConfigureAwait(false), session => session.Id == client.SessionId);
	}

	private static bool IsPing(MessageReceived e) => IsToSelf(e) && string.Equals(DeliveryChecks.TextOf(e)?.Trim(), PingText, StringComparison.OrdinalIgnoreCase);

	private static bool IsAnswer(MessageReceived e) =>
		IsToSelf(e)
		&& DeliveryChecks.TextOf(e)?.Trim() is { } text
		&& (text.StartsWith("wmn yes", StringComparison.OrdinalIgnoreCase) || text.StartsWith("wmn no", StringComparison.OrdinalIgnoreCase));

	/// <summary>A message the account's own phone sent to its own chat.</summary>
	private static bool IsToSelf(MessageReceived e) => e.Source.IsFromMe && string.Equals(e.Source.Chat.User, e.Source.Sender.User, StringComparison.Ordinal);

	private async Task<WhatsmeowSidecar> StartAsync(CancellationToken cancellationToken)
	{
		await _starting.WaitAsync(cancellationToken).ConfigureAwait(false);
		try
		{
			if (_sidecar is not null)
				return _sidecar;

			var binary = await BuildAsync(cancellationToken).ConfigureAwait(false);
			// The one sanctioned copy of the live database: before the run's first start, while nothing holds it.
			if (!_backedUp && File.Exists(LiveSettings.Database))
				Write($"live: backed up the live database as {Path.GetFileName(StoreBackup.Take(LiveSettings.Database, LiveSettings.BackupDirectory, "live", LiveClock.Time.GetUtcNow()))}");
			_backedUp = true;
			var options = new WhatsmeowSidecarOptions { SidecarPath = binary, DatabasePath = LiveSettings.Database };
			_sidecar = await WhatsmeowSidecar.StartAsync(options, _log!.Capture.Factory, cancellationToken).ConfigureAwait(false);
			return _sidecar;
		}
		finally
		{
			_ = _starting.Release();
		}
	}

	/// <summary>Builds the binary and opens the log once; the caller holds <see cref="_starting"/>.</summary>
	private async Task<string> BuildAsync(CancellationToken cancellationToken)
	{
		// Never against a database by accident: only with the opt-in set (docs/guidelines/tests.md).
		if (!LiveSettings.Enabled)
			throw new InvalidOperationException($"The live tests run only with {LiveSettings.DatabaseVariable} set.");
		if (_binary is not null)
			return _binary;

		_ = Directory.CreateDirectory(OutputDirectory);
		DeleteShown();

		// The account's keys: owner-only, outside the repository (the sidecar creates the file 0600).
		var databaseDirectory = Path.GetDirectoryName(Path.GetFullPath(LiveSettings.Database))!;
		_ = OperatingSystem.IsWindows()
			? Directory.CreateDirectory(databaseDirectory)
			: Directory.CreateDirectory(databaseDirectory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

		_directory = Directory.CreateTempSubdirectory("wmn-live-").FullName;
		_binary = await SidecarBinaries
			.BuildAsync("sidecar", "./cmd/whatsmeow-net", $"-X main.version={SidecarBinaries.ClientVersion}", "livetest", _directory, cancellationToken)
			.ConfigureAwait(false);
		_log = new LiveLog(Path.Combine(OutputDirectory, "sidecar.log"));
		return _binary;
	}

	public async ValueTask DisposeAsync()
	{
		try
		{
			// By id: a stop since then (StopAsync) replaced the clients.
			foreach (var id in _throwaway)
				await DiscardAsync(await SessionAsync(id, CancellationToken.None).ConfigureAwait(false), _sidecar!, CancellationToken.None).ConfigureAwait(false);
		}
		finally
		{
			if (_sidecar is not null)
				await _sidecar.DisposeAsync().ConfigureAwait(false);
			_log?.Dispose();

			// The build directory and what was shown only, when this run started anything (xUnit disposes the fixture
			// of tests it did not run too): the live database stays.
			if (_directory is not null)
			{
				Directory.Delete(_directory, recursive: true);
				DeleteShown();
			}

			_starting.Dispose();
		}
	}
}
