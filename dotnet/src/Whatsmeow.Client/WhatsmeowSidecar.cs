using System.Diagnostics.Metrics;
using Grpc.Core;
using Grpc.Core.Interceptors;
using Grpc.Health.V1;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Whatsmeow.Core.Errors;
using Whatsmeow.Core.Telemetry;
using Whatsmeow.Core.Transport;
using Whatsmeow.Protocol.V1;
using Whatsmeow.Sessions;
using Whatsmeow.Sidecar;

namespace Whatsmeow;

/// <summary>
/// The <c>whatsmeow-net</c> sidecar process and the calls to it. It finds, starts, checks and supervises the
/// process, restarts it when it exits on its own, and stops it on <see cref="DisposeAsync"/>. With
/// <see cref="WhatsmeowSidecarOptions.External"/> it connects to a sidecar it does not start instead, connects again
/// when that one goes away, and never stops it (ADR 0046).
/// </summary>
public sealed class WhatsmeowSidecar : IAsyncDisposable
{
	/// <summary>
	/// The contract revisions this client speaks (ADR 0015). Protocol 2 adds the group photo calls; an external
	/// sidecar of protocol 1 is still accepted, and answers those two calls as calls it does not have (ADR 0070).
	/// </summary>
	internal const int MinProtocol = 1;

	internal const int MaxProtocol = 2;

	private readonly WhatsmeowSidecarOptions _options;
	private readonly int _minProtocol;
	private readonly int _maxProtocol;
	private readonly bool _fakeWhatsApp;
	private readonly ILogger _log;
	private readonly ILogger _sidecarLog;
	private readonly ILoggerFactory _loggerFactory;
	private readonly SidecarAddress? _external;
	private readonly LocalChannel _none;
	private readonly LaunchInvoker _launchInvoker;
	private readonly SidecarService.SidecarServiceClient _client;
	private readonly SidecarSupervisor _supervisor;
	private readonly CancellationTokenSource _disposing = new();
	private readonly Lock _launchLock = new();
	private volatile SidecarProcess? _current;
	private LocalChannel? _channel;
	private LocalChannel? _retired;
	private string? _binary;
	private string? _socketDirectory;
	private Task? _starting;
	private Task _watching = Task.CompletedTask;
	private int _disposed;
	private int _launches;
	private TaskCompletionSource _launched = new(TaskCreationOptions.RunContinuationsAsynchronously);

	private WhatsmeowSidecar(
		WhatsmeowSidecarOptions options,
		int minProtocol,
		int maxProtocol,
		bool fakeWhatsApp,
		ILoggerFactory loggerFactory,
		IMeterFactory? meterFactory,
		TimeProvider timeProvider,
		Action<IWhatsAppClient>? clientCreated,
		IReadOnlySet<SessionId> declared
	)
	{
		_options = options;
		_minProtocol = minProtocol;
		_maxProtocol = maxProtocol;
		_fakeWhatsApp = fakeWhatsApp;
		TimeProvider = timeProvider;
		_log = loggerFactory.CreateLogger(SidecarLog.Category);
		_sidecarLog = loggerFactory.CreateLogger(SidecarLog.SidecarCategory);
		_loggerFactory = loggerFactory;
		Metrics = new ClientMetrics(meterFactory);
		_none = new LocalChannel(options.Transport is SidecarTransport.NamedPipe, endpoint: null, token: "", loggerFactory, timeProvider);
		_launchInvoker = new LaunchInvoker(_none.CallInvoker);
		// Every failed call is counted here, by its error code, the library's own calls included (ADR 0037).
		CallInvoker = _launchInvoker.Intercept(new ErrorCounter(Metrics));
		_client = new SidecarService.SidecarServiceClient(CallInvoker);
		_external = SidecarAddress.Parse(options.External?.Address);
		_supervisor = new SidecarSupervisor(_external is null ? LaunchAsync : ConnectAsync, timeProvider, _log, _external?.ToString());
		Sessions = new WhatsmeowSessions(this, CallInvoker, _log, clientCreated, declared);
	}

	/// <summary>The sidecar's sessions, one WhatsApp account each.</summary>
	public WhatsmeowSessions Sessions { get; }

	/// <summary>
	/// Starts the sidecar, waits until it is ready and checks its version; in External mode, connects to the sidecar and
	/// checks its protocol only, so another release that speaks this client's contract is accepted.
	/// </summary>
	/// <exception cref="OptionsValidationException"><paramref name="options"/> is invalid; nothing was started.</exception>
	/// <exception cref="SidecarUnavailableException">
	/// The binary was not found, or the sidecar did not start. In External mode: the token file cannot be read, the
	/// sidecar refused the token, or it did not answer within <see cref="WhatsmeowSidecarOptions.StartupTimeout"/>.
	/// </exception>
	/// <exception cref="SidecarVersionMismatchException">
	/// The sidecar is another version, and it was stopped; in External mode, it speaks no protocol this client supports,
	/// and it keeps running.
	/// </exception>
	public static Task<WhatsmeowSidecar> StartAsync(WhatsmeowSidecarOptions options, CancellationToken cancellationToken) =>
		StartAsync(options, NullLoggerFactory.Instance, meterFactory: null, cancellationToken);

	/// <inheritdoc cref="StartAsync(WhatsmeowSidecarOptions, ILoggerFactory, IMeterFactory?, CancellationToken)"/>
	public static Task<WhatsmeowSidecar> StartAsync(WhatsmeowSidecarOptions options, ILoggerFactory loggerFactory, CancellationToken cancellationToken) =>
		StartAsync(options, loggerFactory, meterFactory: null, cancellationToken);

	/// <inheritdoc cref="StartAsync(WhatsmeowSidecarOptions, CancellationToken)"/>
	/// <param name="options">How to find and start the sidecar.</param>
	/// <param name="loggerFactory">For the client's logs (category <c>Whatsmeow.Client</c>) and the sidecar's stderr (category <c>Whatsmeow.Sidecar</c>).</param>
	/// <param name="meterFactory">
	/// Creates the sidecar's <see cref="Meter"/>, named <see cref="WhatsmeowTelemetry.MeterName"/>, and owns it. When null,
	/// the sidecar creates a meter of the same name itself and disposes it with itself.
	/// </param>
	/// <param name="cancellationToken">Cancels the start; a half-started process is killed.</param>
	public static Task<WhatsmeowSidecar> StartAsync(WhatsmeowSidecarOptions options, ILoggerFactory loggerFactory, IMeterFactory? meterFactory, CancellationToken cancellationToken) =>
		StartCoreAsync(options, loggerFactory, meterFactory, MinProtocol, MaxProtocol, fakeWhatsApp: false, timeProvider: null, cancellationToken);

	/// <summary>Takes the protocol range the client accepts, so a test can make the real sidecar mismatch.</summary>
	internal static Task<WhatsmeowSidecar> StartAsync(
		WhatsmeowSidecarOptions options,
		ILoggerFactory loggerFactory,
		int minProtocol,
		int maxProtocol,
		CancellationToken cancellationToken
	) =>
		StartCoreAsync(options, loggerFactory, meterFactory: null, minProtocol, maxProtocol, fakeWhatsApp: false, timeProvider: null, cancellationToken);

	/// <summary>
	/// Starts the sidecar on <paramref name="timeProvider"/>, so the live tests' host can hold the clock still and ack
	/// exactly once (ADR 0032).
	/// </summary>
	internal static Task<WhatsmeowSidecar> StartAsync(
		WhatsmeowSidecarOptions options,
		ILoggerFactory loggerFactory,
		TimeProvider timeProvider,
		CancellationToken cancellationToken
	) =>
		StartCoreAsync(options, loggerFactory, meterFactory: null, MinProtocol, MaxProtocol, fakeWhatsApp: false, timeProvider, cancellationToken);

	/// <summary>
	/// Starts a test build of the sidecar with <c>--fake-whatsapp</c>, for the integration tests' shared fixture
	/// (ADR 0019). A release build refuses the flag, so this fails with <see cref="SidecarUnavailableException"/> there.
	/// </summary>
	internal static Task<WhatsmeowSidecar> StartFakeWhatsAppAsync(WhatsmeowSidecarOptions options, ILoggerFactory loggerFactory, CancellationToken cancellationToken) =>
		StartCoreAsync(options, loggerFactory, meterFactory: null, MinProtocol, MaxProtocol, fakeWhatsApp: true, timeProvider: null, cancellationToken);

	/// <summary>Starts a test build with <c>--fake-whatsapp</c> whose meter comes from <paramref name="meterFactory"/>.</summary>
	internal static Task<WhatsmeowSidecar> StartFakeWhatsAppWithMeterFactoryAsync(
		WhatsmeowSidecarOptions options,
		ILoggerFactory loggerFactory,
		IMeterFactory meterFactory,
		CancellationToken cancellationToken
	) =>
		StartCoreAsync(options, loggerFactory, meterFactory, MinProtocol, MaxProtocol, fakeWhatsApp: true, timeProvider: null, cancellationToken);

	/// <summary>Starts a test build with <c>--fake-whatsapp</c> on <paramref name="timeProvider"/>, so a test can hold the clock still.</summary>
	internal static Task<WhatsmeowSidecar> StartFakeWhatsAppAsync(
		WhatsmeowSidecarOptions options,
		ILoggerFactory loggerFactory,
		TimeProvider timeProvider,
		CancellationToken cancellationToken
	) =>
		StartCoreAsync(options, loggerFactory, meterFactory: null, MinProtocol, MaxProtocol, fakeWhatsApp: true, timeProvider, cancellationToken);

	private static async Task<WhatsmeowSidecar> StartCoreAsync(
		WhatsmeowSidecarOptions options,
		ILoggerFactory loggerFactory,
		IMeterFactory? meterFactory,
		int minProtocol,
		int maxProtocol,
		bool fakeWhatsApp,
		TimeProvider? timeProvider,
		CancellationToken cancellationToken
	)
	{
		var sidecar = Create(options, loggerFactory, meterFactory, minProtocol, maxProtocol, fakeWhatsApp, timeProvider, clientCreated: null, declared: s_none);
		try
		{
			await sidecar.StartProcessAsync(cancellationToken).ConfigureAwait(false);
			return sidecar;
		}
		catch
		{
			await sidecar.DisposeAsync().ConfigureAwait(false);
			throw;
		}
	}

	private static readonly HashSet<SessionId> s_none = [];

	/// <summary>How long a connect waits before it asks an external sidecar that did not answer again.</summary>
	private static readonly TimeSpan s_connectRetry = TimeSpan.FromSeconds(1);

	/// <summary>
	/// A sidecar that is not started yet, for the hosting package: its sessions' clients exist before the host starts it
	/// (ADR 0034). <paramref name="clientCreated"/> runs for each new client of <see cref="Sessions"/>, before anyone can
	/// connect it. The <paramref name="declared"/> sessions cannot be deleted, since their keyed clients would stay behind.
	/// </summary>
	/// <exception cref="OptionsValidationException"><paramref name="options"/> is invalid.</exception>
	internal static WhatsmeowSidecar Create(
		WhatsmeowSidecarOptions options,
		ILoggerFactory loggerFactory,
		IMeterFactory? meterFactory,
		TimeProvider? timeProvider,
		bool fakeWhatsApp,
		Action<IWhatsAppClient>? clientCreated,
		IReadOnlySet<SessionId> declared
	) =>
		Create(options, loggerFactory, meterFactory, MinProtocol, MaxProtocol, fakeWhatsApp, timeProvider, clientCreated, declared);

	private static WhatsmeowSidecar Create(
		WhatsmeowSidecarOptions options,
		ILoggerFactory loggerFactory,
		IMeterFactory? meterFactory,
		int minProtocol,
		int maxProtocol,
		bool fakeWhatsApp,
		TimeProvider? timeProvider,
		Action<IWhatsAppClient>? clientCreated,
		IReadOnlySet<SessionId> declared
	)
	{
		ArgumentNullException.ThrowIfNull(options);
		ArgumentNullException.ThrowIfNull(loggerFactory);

		// A copy: the caller's instance is left as it is.
		var effective = options.Copy();
		WhatsmeowSidecarOptionsSetup.Apply(effective);
		WhatsmeowSidecarOptionsSetup.Validate(effective);

#pragma warning disable RS0030 // Justification: the default when none is injected (docs/guidelines/csharp.md Time)
		timeProvider ??= TimeProvider.System;
#pragma warning restore RS0030

		return new WhatsmeowSidecar(effective, minProtocol, maxProtocol, fakeWhatsApp, loggerFactory, meterFactory, timeProvider, clientCreated, declared);
	}

	/// <summary>
	/// Finds the binary, starts the first sidecar and its supervisor. Called once, on a sidecar from
	/// <see cref="Create(WhatsmeowSidecarOptions, ILoggerFactory, IMeterFactory?, TimeProvider?, bool, Action{IWhatsAppClient}?, IReadOnlySet{SessionId})"/>.
	/// A dispose meanwhile cancels it and waits for it, so it leaves no process and no socket directory behind.
	/// </summary>
	/// <exception cref="SidecarUnavailableException">The binary was not found, or the sidecar did not start.</exception>
	/// <exception cref="SidecarVersionMismatchException">The sidecar is another version; it was stopped.</exception>
	/// <exception cref="ObjectDisposedException">The sidecar was disposed before or while it started.</exception>
	internal async Task StartProcessAsync(CancellationToken cancellationToken)
	{
		Task starting;
		lock (_launchLock)
		{
			ObjectDisposedException.ThrowIf(_disposed != 0, this);
			_starting = starting = Task.Run(() => StartFirstAsync(cancellationToken), CancellationToken.None);
		}

		await starting.ConfigureAwait(false);
	}

	private async Task StartFirstAsync(CancellationToken cancellationToken)
	{
		if (_external is null)
		{
			_binary = SidecarBinary.Locate(_options);

			// The client owns the socket directory: created 0700 here, reused by every restart, deleted on dispose (ADR 0017).
			_socketDirectory = _options.Transport is SidecarTransport.UnixDomainSocket ? Directory.CreateTempSubdirectory("wmn-").FullName : null;
		}

		using var starting = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _disposing.Token);
		try
		{
			var (processId, exited) = await (_external is null ? LaunchAsync(starting.Token) : ConnectAsync(starting.Token)).ConfigureAwait(false);
			_supervisor.Start(processId, exited);
		}
		catch (OperationCanceledException) when (_disposing.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
		{
			ObjectDisposedException.ThrowIf(condition: true, this);
			throw;
		}
	}

	/// <summary>
	/// Every call goes through this invoker, which starts it on the current launch's channel and follows restarts. The
	/// integration tests reach the fake WhatsApp control through it (ADR 0019).
	/// </summary>
	internal CallInvoker CallInvoker { get; }

	/// <summary>The media root as a full path, or null when none is configured.</summary>
	internal string? MediaRoot => _options.MediaRoot;

	/// <summary>The name the sidecar keeps this client's event cursors under (ARCHITECTURE §8.4).</summary>
	internal string Consumer => _options.External?.Consumer ?? ExternalSidecarOptions.DefaultConsumer;

	internal bool IsDisposed => _disposed != 0;

	/// <summary>The clock and timers of everything this sidecar's clients do.</summary>
	internal TimeProvider TimeProvider { get; }

	/// <summary>The instruments of this sidecar and its clients.</summary>
	internal ClientMetrics Metrics { get; }

	/// <summary>How many sidecars started so far, counting the first.</summary>
	internal int Launches
	{
		get
		{
			lock (_launchLock)
				return _launches;
		}
	}

	/// <summary>Waits until a sidecar started after the first <paramref name="launches"/>; disposing ends the wait.</summary>
	/// <exception cref="ObjectDisposedException">The sidecar was disposed meanwhile.</exception>
	internal async Task WaitForLaunchAsync(int launches, CancellationToken cancellationToken)
	{
		using var wait = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _disposing.Token);
		while (true)
		{
			Task launched;
			lock (_launchLock)
			{
				if (_launches > launches)
					return;
				launched = _launched.Task;
			}

			try
			{
				await launched.WaitAsync(wait.Token).ConfigureAwait(false);
			}
			catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
			{
				ObjectDisposedException.ThrowIf(condition: true, this);
			}
		}
	}

	/// <summary>Reports the running sidecar's versions.</summary>
	/// <exception cref="SidecarUnavailableException">The sidecar cannot be reached, for example while it restarts.</exception>
	public async Task<SidecarInfo> GetInfoAsync(CancellationToken cancellationToken = default)
	{
		ObjectDisposedException.ThrowIf(_disposed != 0, this);
		return ToSidecarInfo(await GetInfoCoreAsync(cancellationToken).ConfigureAwait(false));
	}

	/// <summary>
	/// Stops the sidecar: a graceful stop, then a kill after a 5 s grace. Its socket directory is deleted. An external
	/// sidecar is never stopped: the client only stops its own event readers, acks what their handlers finished, and
	/// closes its connection.
	/// </summary>
	public ValueTask DisposeAsync() => new(StopAsync(CancellationToken.None));

	/// <summary>
	/// <see cref="DisposeAsync"/>, for the host's stop (ADR 0034): once <paramref name="stopWaiting"/> is cancelled, it no
	/// longer waits for handlers that ignore their own cancellation, and goes on to stop the process. Their events were
	/// not acked, so they come again. The process stop itself is never cut short.
	/// </summary>
	internal async Task StopAsync(CancellationToken stopWaiting)
	{
		Task? starting;
		lock (_launchLock)
		{
			if (_disposed != 0)
				return;
			_disposed = 1;
			starting = _starting;
		}

		try
		{
			try
			{
				await StopStartingAsync(starting).ConfigureAwait(false);

				// While the sidecar still runs: the handlers stop, and what they finished is acked (ARCHITECTURE §8.4).
				await Sessions.StopEventsAsync(stopWaiting).ConfigureAwait(false);
			}
			finally
			{
				await _disposing.CancelAsync().ConfigureAwait(false);
				await _supervisor.DisposeAsync().ConfigureAwait(false);
				await _watching.ConfigureAwait(false);
			}
		}
		finally
		{
			try
			{
				if (_current is { } process)
					await process.StopAsync(ShutdownAsync, TimeProvider).ConfigureAwait(false);
			}
			finally
			{
				_current = null;
				Publish(channel: null);
				_retired?.Dispose();
				_none.Dispose();
				_disposing.Dispose();
				Metrics.Dispose();
				DeleteSocketDirectory();
			}
		}
	}

	/// <summary>Cancels a first start that is still running and waits for it; its caller sees how it ended.</summary>
	private async Task StopStartingAsync(Task? starting)
	{
		if (starting is null or { IsCompleted: true })
			return;

		await _disposing.CancelAsync().ConfigureAwait(false);
		try
		{
			await starting.ConfigureAwait(false);
		}
#pragma warning disable CA1031 // Justification: the start's own caller observes its failure; the dispose goes on
		catch (Exception)
#pragma warning restore CA1031
		{
			// Seen by StartProcessAsync's caller.
		}
	}

	/// <summary>
	/// Starts one sidecar: spawn, ready line, nonce, then GetInfo and the version check, then Connect again for the
	/// sessions the application had connected (ARCHITECTURE §9). Used for the first start and for every restart. A
	/// sidecar that fails a check is stopped before this throws.
	/// </summary>
	private async Task<(int ProcessId, Task<int> Exited)> LaunchAsync(CancellationToken cancellationToken)
	{
		// A sidecar that exited on its own is released before the next one starts.
		if (_current is { } previous)
			await previous.StopAsync(ShutdownAsync, TimeProvider).ConfigureAwait(false);

		using var timeout = new CancellationTokenSource(_options.StartupTimeout, TimeProvider);
		using var startup = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);

		var process = await SidecarProcess
			.StartAsync(_binary!, _options.Transport, _socketDirectory, _options.DatabasePath!, _options.MediaRoot, _options.History, _options.SendPacing, _fakeWhatsApp, _options.StartupTimeout, _log, _sidecarLog, startup.Token, cancellationToken)
			.ConfigureAwait(false);
		_current = process;
		Publish(new LocalChannel(_options.Transport is SidecarTransport.NamedPipe, process.Endpoint, process.Token, _loggerFactory, TimeProvider));
		SidecarLog.SidecarStarted(_log, process.Id, process.Version, process.Endpoint);

		GetInfoResponse info;
		try
		{
			info = await GetInfoCoreAsync(startup.Token).ConfigureAwait(false);
		}
		catch (Exception e) when (e is WhatsmeowException or OperationCanceledException)
		{
			await process.StopAsync(ShutdownAsync, TimeProvider).ConfigureAwait(false);
			cancellationToken.ThrowIfCancellationRequested();
			SidecarUnavailableException.ThrowStartFailed($"its first GetInfo call failed: {e.Message}", process.Tail(), e);
			throw;
		}

		var protocol = (int)info.Protocol;
		if (info.Version != ClientVersion.Current || protocol < _minProtocol || protocol > _maxProtocol)
		{
			await process.StopAsync(ShutdownAsync, TimeProvider).ConfigureAwait(false);
			SidecarVersionMismatchException.Throw(ClientVersion.Current, info.Version, protocol, _minProtocol, _maxProtocol);
		}

		await Sessions.ReconnectAsync(cancellationToken).ConfigureAwait(false);

		CountLaunch();
		return (process.Id, process.Exited);
	}

	/// <summary>
	/// Connects to an external sidecar (ADR 0046), for the first start and after it went away: one connect epoch, an
	/// External-mode launch. Each attempt reads the token file again and publishes a channel of its own with that token,
	/// so a rotated secret is followed and no call of an older epoch reaches this one (ADR 0045). GetInfo and the
	/// protocol check, then the health watch on this epoch's channel, until the sidecar answers both within the startup
	/// timeout; then Connect again for the sessions the application had connected. The epoch ends when the watch does,
	/// or when the sidecar refuses one of its calls <c>Unauthenticated</c>, as after a rotation. Its process id is 0: the
	/// process is not the client's.
	/// </summary>
	private async Task<(int ProcessId, Task<int> Exited)> ConnectAsync(CancellationToken cancellationToken)
	{
		using var timeout = new CancellationTokenSource(_options.StartupTimeout, TimeProvider);
		using var startup = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
		var address = _external!.ToString();

		Exception? unreachable = null;
		try
		{
			GetInfoResponse info;
			Task<int> watching;
			while (true)
			{
				var channel = LocalChannel.ToExternal(_external, ExternalToken.Read(_options.External!.TokenPath!), _options.External.KeepAlive, _loggerFactory, TimeProvider);
				Publish(channel);
				try
				{
					info = await GetInfoCoreAsync(startup.Token).ConfigureAwait(false);
					var protocol = (int)info.Protocol;
					if (protocol < _minProtocol || protocol > _maxProtocol)
						SidecarVersionMismatchException.ThrowProtocol(info.Version, protocol, _minProtocol, _maxProtocol);

					watching = await WatchAsync(channel, address, startup.Token).ConfigureAwait(false);
					break;
				}
				catch (SidecarUnavailableException e) when (e.ErrorCode is WhatsmeowErrorCode.Unauthenticated)
				{
					// After a rotation, a reconnect waits for the token file with the supervisor's backoff.
					SidecarUnavailableException.ThrowTokenRefused(address, e);
				}
				catch (Exception e) when (e is SidecarUnavailableException || (e is OperationCanceledException && !startup.IsCancellationRequested))
				{
					// Not listening yet, or restarting: an orchestrator may start the two in any order. A call to a sidecar
					// that went away mid-connect, the watch's first read included, may also end with no status at all, as a
					// cancellation the caller did not ask for [observed: the sidecar killed before the watch, macOS].
					unreachable = e;
					await Task.Delay(s_connectRetry, TimeProvider, startup.Token).ConfigureAwait(false);
				}
			}

			_watching = watching;
			SidecarLog.ExternalSidecarConnected(_log, address, info.Version);
			await Sessions.ReconnectAsync(cancellationToken).ConfigureAwait(false);

			CountLaunch();
			return (0, watching);
		}
		catch (OperationCanceledException e) when (timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
		{
			SidecarUnavailableException.ThrowNotAnswering(address, _options.StartupTimeout, unreachable ?? e);
			throw;
		}
	}

	/// <summary>
	/// Opens the health watch on <paramref name="channel"/>, this epoch's, and waits for its first status, which proves the
	/// stream reaches the sidecar. The task it returns completes once the epoch ends: the watch ended, however (the
	/// sidecar stopped, restarted, or its connection failed, a silent one included, which the keepalive pings drop), or
	/// the sidecar refused one of the channel's calls. Disposing the client ends it too.
	/// </summary>
	private async Task<Task<int>> WatchAsync(LocalChannel channel, string address, CancellationToken startup)
	{
		var ending = CancellationTokenSource.CreateLinkedTokenSource(_disposing.Token, channel.Refused);
		AsyncServerStreamingCall<HealthCheckResponse>? call = null;
		try
		{
			call = new Health.HealthClient(channel.CallInvoker).Watch(new HealthCheckRequest(), cancellationToken: ending.Token);
			if (!await call.ResponseStream.MoveNext(startup).ConfigureAwait(false))
				SidecarUnavailableException.ThrowNotAnswering(address, _options.StartupTimeout, innerException: null);
		}
		catch (RpcException e)
		{
			call?.Dispose();
			ending.Dispose();
			RpcErrors.Throw(e);
		}
		catch
		{
			call?.Dispose();
			ending.Dispose();
			throw;
		}

#pragma warning disable CA2025 // Justification: the returned task owns the call and its token source, and disposes them when the epoch ends
		return WatchUntilItEndsAsync(call, ending, address, _log, channel.Refused);
#pragma warning restore CA2025
	}

	/// <summary>Completes when the watch ends, whatever ends it: any end or fault is the epoch's end, never a fault of this task.</summary>
	private static async Task<int> WatchUntilItEndsAsync(
		AsyncServerStreamingCall<HealthCheckResponse> call,
		CancellationTokenSource ending,
		string address,
		ILogger log,
		CancellationToken refused
	)
	{
		using (ending)
		using (call)
		{
			try
			{
				while (await call.ResponseStream.MoveNext(CancellationToken.None).ConfigureAwait(false))
				{
					// A status change; the sidecar sends none but its first.
				}
			}
#pragma warning disable CA1031 // Justification: a watch that faults is a sidecar lost; the supervisor connects again
			catch (Exception e)
#pragma warning restore CA1031
			{
				if (refused.IsCancellationRequested)
					SidecarLog.ExternalTokenRefused(log, address);
				if (!refused.IsCancellationRequested)
					SidecarLog.ExternalWatchEnded(log, e);
			}
		}

		return 0;
	}

	/// <summary>Counts a sidecar that is ready, and wakes everything that waits for the next one.</summary>
	private void CountLaunch()
	{
		TaskCompletionSource launched;
		bool restarted;
		lock (_launchLock)
		{
			restarted = ++_launches > 1;
			launched = _launched;
			_launched = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		}

		if (restarted)
			Metrics.Restarted();

		launched.SetResult();
	}

	/// <summary>
	/// Makes <paramref name="channel"/> the one new calls start on, or none when null. The last launch's channel is
	/// disposed only when the next one is published: disposing it would cancel the calls still on it, which fail as
	/// unreachable on their own, and a call started just before the switch would end as a cancellation (ADR 0045).
	/// </summary>
	private void Publish(LocalChannel? channel)
	{
		_retired?.Dispose();
		_retired = _channel;
		_channel = channel;
		_launchInvoker.Use(channel?.CallInvoker);
	}

	private async Task<GetInfoResponse> GetInfoCoreAsync(CancellationToken cancellationToken)
	{
		try
		{
			return await _client.GetInfoAsync(new GetInfoRequest(), cancellationToken: cancellationToken).ConfigureAwait(false);
		}
		catch (RpcException e)
		{
			RpcErrors.Throw(e);
			throw;
		}
	}

	private async Task ShutdownAsync(CancellationToken cancellationToken) =>
		_ = await _client.ShutdownAsync(new ShutdownRequest(), cancellationToken: cancellationToken).ConfigureAwait(false);

	private void DeleteSocketDirectory()
	{
		if (_socketDirectory is null)
			return;

		try
		{
			Directory.Delete(_socketDirectory, recursive: true);
		}
		catch (DirectoryNotFoundException)
		{
			// Already gone.
		}
		catch (Exception e) when (e is IOException or UnauthorizedAccessException)
		{
			SidecarLog.SocketDirectoryNotDeleted(_log, e, _socketDirectory);
		}
	}

	private static SidecarInfo ToSidecarInfo(GetInfoResponse info) =>
		new()
		{
			Version = info.Version,
			Protocol = (int)info.Protocol,
			WhatsmeowVersion = info.WhatsmeowVersion,
			WhatsAppWebVersion = info.WaWebVersion,
			GoVersion = info.GoVersion,
			Commit = info.Commit,
		};
}
