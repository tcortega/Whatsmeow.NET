using Grpc.Core;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Whatsmeow.Core.Errors;
using Whatsmeow.Core.Transport;
using Whatsmeow.Protocol.V1;
using Whatsmeow.Sidecar;

namespace Whatsmeow;

/// <summary>
/// The <c>whatsmeow-net</c> sidecar process and the one channel to it. It finds, starts, checks and supervises the
/// process, restarts it when it exits on its own, and stops it on <see cref="DisposeAsync"/>.
/// </summary>
public sealed class WhatsmeowSidecar : IAsyncDisposable
{
	/// <summary>The contract revisions this client speaks (ADR 0015).</summary>
	internal const int MinProtocol = 1;

	internal const int MaxProtocol = 1;

	private readonly WhatsmeowSidecarOptions _options;
	private readonly string _binary;
	private readonly string? _socketDirectory;
	private readonly int _minProtocol;
	private readonly int _maxProtocol;
	private readonly bool _fakeWhatsApp;
	private readonly TimeProvider _timeProvider;
	private readonly ILogger _log;
	private readonly ILogger _sidecarLog;
	private readonly LocalChannel _channel;
	private readonly SidecarService.SidecarServiceClient _client;
	private readonly SidecarSupervisor _supervisor;
	private volatile SidecarProcess? _current;
	private int _disposed;

	private WhatsmeowSidecar(
		WhatsmeowSidecarOptions options,
		string binary,
		string? socketDirectory,
		int minProtocol,
		int maxProtocol,
		bool fakeWhatsApp,
		ILoggerFactory loggerFactory,
		TimeProvider timeProvider
	)
	{
		_options = options;
		_binary = binary;
		_socketDirectory = socketDirectory;
		_minProtocol = minProtocol;
		_maxProtocol = maxProtocol;
		_fakeWhatsApp = fakeWhatsApp;
		_timeProvider = timeProvider;
		_log = loggerFactory.CreateLogger(SidecarLog.Category);
		_sidecarLog = loggerFactory.CreateLogger(SidecarLog.SidecarCategory);
		_channel = new LocalChannel(options.Transport is SidecarTransport.NamedPipe, () => _current?.Endpoint, () => _current?.Token ?? "", loggerFactory);
		_client = new SidecarService.SidecarServiceClient(_channel.CallInvoker);
		_supervisor = new SidecarSupervisor(LaunchAsync, timeProvider, _log);
	}

	/// <summary>Starts the sidecar, waits until it is ready and checks its version.</summary>
	/// <exception cref="OptionsValidationException"><paramref name="options"/> is invalid; nothing was started.</exception>
	/// <exception cref="SidecarUnavailableException">The binary was not found, or the sidecar did not start.</exception>
	/// <exception cref="SidecarVersionMismatchException">The sidecar is another version; it was stopped.</exception>
	public static Task<WhatsmeowSidecar> StartAsync(WhatsmeowSidecarOptions options, CancellationToken cancellationToken) =>
		StartAsync(options, NullLoggerFactory.Instance, cancellationToken);

	/// <inheritdoc cref="StartAsync(WhatsmeowSidecarOptions, CancellationToken)"/>
	/// <param name="options">How to find and start the sidecar.</param>
	/// <param name="loggerFactory">For the client's logs (category <c>Whatsmeow.Client</c>) and the sidecar's stderr (category <c>Whatsmeow.Sidecar</c>).</param>
	/// <param name="cancellationToken">Cancels the start; a half-started process is killed.</param>
	public static Task<WhatsmeowSidecar> StartAsync(WhatsmeowSidecarOptions options, ILoggerFactory loggerFactory, CancellationToken cancellationToken) =>
		StartAsync(options, loggerFactory, MinProtocol, MaxProtocol, cancellationToken);

	/// <summary>Takes the protocol range the client accepts, so a test can make the real sidecar mismatch.</summary>
	internal static Task<WhatsmeowSidecar> StartAsync(
		WhatsmeowSidecarOptions options,
		ILoggerFactory loggerFactory,
		int minProtocol,
		int maxProtocol,
		CancellationToken cancellationToken
	) =>
		StartCoreAsync(options, loggerFactory, minProtocol, maxProtocol, fakeWhatsApp: false, cancellationToken);

	/// <summary>
	/// Starts a test build of the sidecar with <c>--fake-whatsapp</c>, for the integration tests' shared fixture
	/// (ADR 0019). A release build refuses the flag, so this fails with <see cref="SidecarUnavailableException"/> there.
	/// </summary>
	internal static Task<WhatsmeowSidecar> StartFakeWhatsAppAsync(WhatsmeowSidecarOptions options, ILoggerFactory loggerFactory, CancellationToken cancellationToken) =>
		StartCoreAsync(options, loggerFactory, MinProtocol, MaxProtocol, fakeWhatsApp: true, cancellationToken);

	private static async Task<WhatsmeowSidecar> StartCoreAsync(
		WhatsmeowSidecarOptions options,
		ILoggerFactory loggerFactory,
		int minProtocol,
		int maxProtocol,
		bool fakeWhatsApp,
		CancellationToken cancellationToken
	)
	{
		ArgumentNullException.ThrowIfNull(options);
		ArgumentNullException.ThrowIfNull(loggerFactory);

		// A copy: the caller's instance is left as it is.
		var effective = new WhatsmeowSidecarOptions { SidecarPath = options.SidecarPath, StartupTimeout = options.StartupTimeout, Transport = options.Transport };
		WhatsmeowSidecarOptionsSetup.Apply(effective);
		WhatsmeowSidecarOptionsSetup.Validate(effective);
		var binary = SidecarBinary.Locate(effective);

		// The client owns the socket directory: created 0700 here, reused by every restart, deleted on dispose (ADR 0017).
		var socketDirectory = effective.Transport is SidecarTransport.UnixDomainSocket ? Directory.CreateTempSubdirectory("wmn-").FullName : null;

#pragma warning disable RS0030 // Justification: the default when none is injected (docs/guidelines/csharp.md Time)
		var timeProvider = TimeProvider.System;
#pragma warning restore RS0030

		var sidecar = new WhatsmeowSidecar(effective, binary, socketDirectory, minProtocol, maxProtocol, fakeWhatsApp, loggerFactory, timeProvider);
		try
		{
			var (processId, exited) = await sidecar.LaunchAsync(cancellationToken).ConfigureAwait(false);
			sidecar._supervisor.Start(processId, exited);
			return sidecar;
		}
		catch
		{
			await sidecar.DisposeAsync().ConfigureAwait(false);
			throw;
		}
	}

	/// <summary>
	/// Every call goes through this invoker, which adds the current launch's token and follows restarts. The
	/// integration tests reach the fake WhatsApp control through it (ADR 0019).
	/// </summary>
	internal CallInvoker CallInvoker => _channel.CallInvoker;

	/// <summary>Reports the running sidecar's versions.</summary>
	/// <exception cref="SidecarUnavailableException">The sidecar cannot be reached, for example while it restarts.</exception>
	public async Task<SidecarInfo> GetInfoAsync(CancellationToken cancellationToken = default)
	{
		ObjectDisposedException.ThrowIf(_disposed != 0, this);
		return ToSidecarInfo(await GetInfoCoreAsync(cancellationToken).ConfigureAwait(false));
	}

	/// <summary>Stops the sidecar: a graceful stop, then a kill after a 5 s grace. Its socket directory is deleted.</summary>
	public async ValueTask DisposeAsync()
	{
		if (Interlocked.Exchange(ref _disposed, 1) != 0)
			return;

		try
		{
			await _supervisor.DisposeAsync().ConfigureAwait(false);
		}
		finally
		{
			try
			{
				if (_current is { } process)
					await process.StopAsync(ShutdownAsync, _timeProvider).ConfigureAwait(false);
			}
			finally
			{
				_current = null;
				_channel.Dispose();
				DeleteSocketDirectory();
			}
		}
	}

	/// <summary>
	/// Starts one sidecar: spawn, ready line, nonce, then GetInfo and the version check (ARCHITECTURE §9). Used for
	/// the first start and for every restart. A sidecar that fails a check is stopped before this throws.
	/// </summary>
	private async Task<(int ProcessId, Task<int> Exited)> LaunchAsync(CancellationToken cancellationToken)
	{
		// A sidecar that exited on its own is released before the next one starts.
		if (_current is { } previous)
			await previous.StopAsync(ShutdownAsync, _timeProvider).ConfigureAwait(false);

		using var timeout = new CancellationTokenSource(_options.StartupTimeout, _timeProvider);
		using var startup = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);

		var process = await SidecarProcess
			.StartAsync(_binary, _options.Transport, _socketDirectory, _fakeWhatsApp, _options.StartupTimeout, _log, _sidecarLog, startup.Token, cancellationToken)
			.ConfigureAwait(false);
		_current = process;
		SidecarLog.SidecarStarted(_log, process.Id, process.Version, process.Endpoint);

		GetInfoResponse info;
		try
		{
			info = await GetInfoCoreAsync(startup.Token).ConfigureAwait(false);
		}
		catch (Exception e) when (e is WhatsmeowException or OperationCanceledException)
		{
			await process.StopAsync(ShutdownAsync, _timeProvider).ConfigureAwait(false);
			cancellationToken.ThrowIfCancellationRequested();
			SidecarUnavailableException.ThrowStartFailed($"its first GetInfo call failed: {e.Message}", process.Tail(), e);
			throw;
		}

		var protocol = (int)info.Protocol;
		if (info.Version != ClientVersion.Current || protocol < _minProtocol || protocol > _maxProtocol)
		{
			await process.StopAsync(ShutdownAsync, _timeProvider).ConfigureAwait(false);
			SidecarVersionMismatchException.Throw(ClientVersion.Current, info.Version, protocol, _minProtocol, _maxProtocol);
		}

		return (process.Id, process.Exited);
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
