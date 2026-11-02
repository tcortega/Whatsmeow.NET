using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Whatsmeow.Sessions;
using Whatsmeow.Sidecar;

namespace Whatsmeow.Extensions.Hosting;

/// <summary>
/// Runs the sidecar for the host's lifetime (ADR 0034). <see cref="StartingAsync"/>, before any hosted service's
/// <c>StartAsync</c>, starts it and creates the declared sessions, so a service that took a client in its constructor
/// finds both, whatever the registration order. <see cref="StartedAsync"/>, after every <c>StartAsync</c> succeeded,
/// connects the paired ones: a host that fails to start never had events flowing into handlers whose container it then
/// disposes. <see cref="StoppedAsync"/>, after every service stopped using it, stops it. Crashes in between are the
/// supervisor's: it restarts the sidecar with its backoff and never throws (ARCHITECTURE §8.5).
/// </summary>
internal sealed class WhatsmeowHostedService(WhatsmeowSidecar sidecar, IEnumerable<DeclaredSession> declared, IServiceProvider services) : IHostedLifecycleService
{
	private readonly ILogger _log = (services.GetService<ILoggerFactory>() ?? NullLoggerFactory.Instance).CreateLogger(SidecarLog.Category);
	private readonly HashSet<SessionId> _declared = [.. declared.Select(static session => session.Id)];

	/// <summary>Starts the sidecar and creates the declared sessions; a failure leaves no sidecar behind.</summary>
	public async Task StartingAsync(CancellationToken cancellationToken)
	{
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

	/// <summary>Connects the declared sessions that are paired; a failure fails the host's start and leaves no sidecar behind.</summary>
	public async Task StartedAsync(CancellationToken cancellationToken)
	{
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
	public Task StoppedAsync(CancellationToken cancellationToken) => sidecar.StopAsync(cancellationToken);

	private async Task ConnectAsync(CancellationToken cancellationToken)
	{
		if (_declared.Count == 0)
			return;

		foreach (var session in await sidecar.Sessions.ListAsync(cancellationToken).ConfigureAwait(false))
		{
			if (!_declared.Contains(session.Id) || session.State is SessionState.Created or SessionState.LoggedOut)
				continue;

			// A stopping state stays as the restart left it; the application decides (ADR 0020).
			if (session.State is not SessionState.Disconnected)
			{
				HostingLog.SessionNotConnected(_log, session.Id.Value, Name(session.State));
				continue;
			}

			// The sidecar's own client, the one the handlers are on, even when the application replaced the keyed registration.
			await sidecar.Sessions.Client(session.Id).ConnectAsync(cancellationToken).ConfigureAwait(false);
		}
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
			SessionState.TempBanned => nameof(SessionState.TempBanned),
			SessionState.Outdated => nameof(SessionState.Outdated),
			SessionState.Error => nameof(SessionState.Error),
			_ => nameof(SessionState.Unknown),
		};
}
