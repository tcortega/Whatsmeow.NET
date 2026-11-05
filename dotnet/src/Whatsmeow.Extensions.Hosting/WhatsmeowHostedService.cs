using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Whatsmeow.Sessions;
using Whatsmeow.Sidecar;

namespace Whatsmeow.Extensions.Hosting;

/// <summary>
/// Runs the sidecar for the host's lifetime (ADR 0034). <see cref="StartingAsync"/>, before any hosted service's
/// <c>StartAsync</c>, starts it and creates the declared sessions, so a service that took a client in its constructor
/// finds both, whatever the registration order. <see cref="StartedAsync"/>, after every <c>StartAsync</c> succeeded,
/// connects the paired ones, so a host that fails to start had no events flowing into handlers whose container it then
/// disposes, unless a service connected or paired a session in its own <c>StartAsync</c>, or a later service's
/// <c>StartedAsync</c> failed (ADR 0034, ADR 0052). <see cref="StoppedAsync"/>, after every service stopped using it,
/// stops it. Crashes in between are the supervisor's: it restarts the sidecar with its backoff and never throws
/// (ARCHITECTURE §8.5).
/// A host whose sessions a test replaced with fakes (<see cref="ReplacedSession"/>, ADR 0036) starts no sidecar and
/// creates or connects nothing: the handlers go on the fakes in <see cref="StartedAsync"/>, and come off them in
/// <see cref="StoppedAsync"/>, which first stops the ones running.
/// </summary>
internal sealed class WhatsmeowHostedService(
	WhatsmeowSidecar sidecar,
	IEnumerable<DeclaredSession> declared,
	IEnumerable<ReplacedSession> replaced,
	IServiceProvider services
) : IHostedLifecycleService
{
	private readonly ILogger _log = (services.GetService<ILoggerFactory>() ?? NullLoggerFactory.Instance).CreateLogger(SidecarLog.Category);
	private readonly HashSet<SessionId> _declared = [.. declared.Select(static session => session.Id)];
	private readonly ReplacedSession[] _replaced = [.. replaced];
	private readonly List<IDisposable> _onFakes = [];
	private readonly HandlerRuns _fakeRuns = new();

	/// <summary>
	/// Starts the sidecar and creates the declared sessions; a failure leaves no sidecar behind. With fakes, it checks
	/// that every declared session has one instead.
	/// </summary>
	/// <exception cref="InvalidOperationException">A host with fakes has a declared session without one.</exception>
	public async Task StartingAsync(CancellationToken cancellationToken)
	{
		if (_replaced.Length > 0)
		{
			CheckEverySessionHasAFake();
			return;
		}

		try
		{
			await sidecar.StartProcessAsync(cancellationToken).ConfigureAwait(false);
			foreach (var id in _declared)
				_ = await sidecar.Sessions.GetOrCreateAsync(id, cancellationToken).ConfigureAwait(false);
		}
		catch
		{
			await sidecar.DisposeAsync().ConfigureAwait(false);
			throw;
		}
	}

	public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

	/// <summary>
	/// Connects the declared sessions that are paired; a failure fails the host's start and leaves no sidecar behind.
	/// With fakes, it puts the handlers on them instead, so a host whose start failed before here had no events flowing
	/// into handlers whose container it then disposes; a later service's <c>StartedAsync</c> that fails leaves the same
	/// window as with a sidecar (ADR 0034, ADR 0036).
	/// </summary>
	public async Task StartedAsync(CancellationToken cancellationToken)
	{
		if (_replaced.Length > 0)
		{
			AttachToFakes();
			return;
		}

		try
		{
			await ConnectAsync(cancellationToken).ConfigureAwait(false);
		}
		catch
		{
			await sidecar.DisposeAsync().ConfigureAwait(false);
			throw;
		}
	}

	public Task StoppingAsync(CancellationToken cancellationToken) => Task.CompletedTask;

	public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

	/// <summary>
	/// Stops the handlers, acks what they finished, then stops the sidecar: Shutdown, a 5 s grace, then the kill. Once
	/// the host's shutdown timeout passes, it stops waiting for handlers that ignore their cancellation, whose events
	/// then come again; the process stop is never cut short, and the host waits for it, so the sidecar never outlives
	/// the host (§13 invariant 9).
	/// </summary>
	/// <remarks>
	/// With fakes, it takes the host's handlers off them, then cancels the ones still running and waits for them, with
	/// the same shutdown timeout: the fakes are the test's and outlive the host, whose container goes away.
	/// </remarks>
	public async Task StoppedAsync(CancellationToken cancellationToken)
	{
		foreach (var registration in _onFakes)
			registration.Dispose();
		_onFakes.Clear();
		await _fakeRuns.StopAsync(cancellationToken).ConfigureAwait(false);
		await sidecar.StopAsync(cancellationToken).ConfigureAwait(false);
	}

	/// <summary>A host with fakes starts no sidecar, so a real client of a declared session would fail every call.</summary>
	private void CheckEverySessionHasAFake()
	{
		var faked = _replaced.Select(static session => session.Id).ToHashSet();
		foreach (var id in _declared)
		{
			if (!faked.Contains(id))
				throw new InvalidOperationException(
					$"Session {id.Value} is declared with AddSession but has no fake: a host with fakes starts no sidecar, so each declared session needs one (UseFakeClient)."
				);
		}
	}

	/// <summary>The handlers go on each fake, as they go on a sidecar's new client, with runs the host's stop ends.</summary>
	private void AttachToFakes()
	{
		var handlers = services.GetServices<HandlerRegistration>().ToArray();
		foreach (var session in _replaced)
		{
			foreach (var handler in handlers)
			{
				if (handler.AttachTo(session.Client, services, _fakeRuns) is { } registration)
					_onFakes.Add(registration);
			}
		}
	}

	private async Task ConnectAsync(CancellationToken cancellationToken)
	{
		if (_declared.Count == 0)
			return;

		var ids = new List<SessionId>();
		foreach (var session in await sidecar.Sessions.ListAsync(cancellationToken).ConfigureAwait(false))
		{
			if (!_declared.Contains(session.Id) || session.State is SessionState.Created or SessionState.LoggedOut)
				continue;

			// A stopping state stays as the restart left it; the application decides (ADR 0020). An external sidecar that
			// outlived the last host keeps its sessions online: Connect does nothing to one that is connecting, connected
			// or reconnecting (session.proto), and starts this host's events (ADR 0046).
			if (session.State is not (SessionState.Disconnected or SessionState.Connecting or SessionState.Connected or SessionState.CaughtUp or SessionState.Reconnecting))
			{
				HostingLog.SessionNotConnected(_log, session.Id.Value, Name(session.State));
				continue;
			}

			ids.Add(session.Id);
		}

		await sidecar.Sessions.ConnectAsync(ids, cancellationToken).ConfigureAwait(false);
	}

	private static string Name(SessionState state) =>
		state switch
		{
			SessionState.Created => nameof(SessionState.Created),
			SessionState.Pairing => nameof(SessionState.Pairing),
			SessionState.Connecting => nameof(SessionState.Connecting),
			SessionState.Connected => nameof(SessionState.Connected),
			SessionState.CaughtUp => nameof(SessionState.CaughtUp),
			SessionState.Reconnecting => nameof(SessionState.Reconnecting),
			SessionState.Disconnected => nameof(SessionState.Disconnected),
			SessionState.LoggedOut => nameof(SessionState.LoggedOut),
			SessionState.Replaced => nameof(SessionState.Replaced),
			SessionState.TemporarilyBanned => nameof(SessionState.TemporarilyBanned),
			SessionState.Outdated => nameof(SessionState.Outdated),
			SessionState.Error => nameof(SessionState.Error),
			_ => nameof(SessionState.Unknown),
		};
}
