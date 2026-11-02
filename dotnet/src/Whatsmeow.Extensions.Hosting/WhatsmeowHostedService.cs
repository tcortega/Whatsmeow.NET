using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Whatsmeow.Sessions;
using Whatsmeow.Sidecar;

namespace Whatsmeow.Extensions.Hosting;

/// <summary>
/// Runs the sidecar for the host's lifetime (ADR 0034). It starts in <see cref="StartingAsync"/>, before any hosted
/// service's <c>StartAsync</c>, so a service that took a client in its constructor finds the sidecar running, whatever
/// the registration order; it stops in <see cref="StoppedAsync"/>, after every service stopped using it. Crashes in
/// between are the supervisor's: it restarts the sidecar with its backoff and never throws (ARCHITECTURE §8.5).
/// </summary>
internal sealed class WhatsmeowHostedService(WhatsmeowSidecar sidecar, IEnumerable<DeclaredSession> declared, IServiceProvider services) : IHostedLifecycleService
{
	private readonly ILogger _log = (services.GetService<ILoggerFactory>() ?? NullLoggerFactory.Instance).CreateLogger(SidecarLog.Category);

	/// <summary>Starts the sidecar, creates the declared sessions and connects the paired ones; a failure leaves no sidecar behind.</summary>
	public async Task StartingAsync(CancellationToken cancellationToken)
	{
		try
		{
			await sidecar.StartProcessAsync(cancellationToken).ConfigureAwait(false);
			await StartSessionsAsync(cancellationToken).ConfigureAwait(false);
		}
		catch
		{
			await sidecar.DisposeAsync().ConfigureAwait(false);
			throw;
		}
	}

	public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

	public Task StartedAsync(CancellationToken cancellationToken) => Task.CompletedTask;

	public Task StoppingAsync(CancellationToken cancellationToken) => Task.CompletedTask;

	public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

	/// <summary>
	/// Stops the handlers, acks what they finished, then stops the sidecar: Shutdown, a 5 s grace, then the kill. It does
	/// not end at the host's shutdown timeout, which the host waits beyond, so the sidecar never outlives the host
	/// (§13 invariant 9).
	/// </summary>
	public async Task StoppedAsync(CancellationToken cancellationToken) => await sidecar.DisposeAsync().ConfigureAwait(false);

	private async Task StartSessionsAsync(CancellationToken cancellationToken)
	{
		var ids = declared.Select(static session => session.Id).ToHashSet();
		if (ids.Count == 0)
			return;

		foreach (var id in ids)
			_ = await sidecar.Sessions.GetOrCreateAsync(id, cancellationToken).ConfigureAwait(false);

		foreach (var session in await sidecar.Sessions.ListAsync(cancellationToken).ConfigureAwait(false))
		{
			if (!ids.Contains(session.Id) || session.State is SessionState.Created or SessionState.LoggedOut)
				continue;

			// A stopping state stays as the restart left it; the application decides (ADR 0020).
			if (session.State is not SessionState.Disconnected)
			{
				HostingLog.SessionNotConnected(_log, session.Id.Value, session.State.GetType().Name);
				continue;
			}

			// Resolved here, not taken in the constructor, so an application that registers its own client for the key gets it connected.
			await services.GetRequiredKeyedService<IWhatsAppClient>(session.Id.Value).ConnectAsync(cancellationToken).ConfigureAwait(false);
		}
	}
}
