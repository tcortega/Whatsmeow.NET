using System.Collections.Concurrent;
using Grpc.Core;
using Whatsmeow.Core.Errors;
using Whatsmeow.Protocol.V1;

namespace Whatsmeow.Sessions;

/// <summary>
/// The sessions of a <see cref="WhatsmeowSidecar"/>, one WhatsApp account each, from
/// <see cref="WhatsmeowSidecar.Sessions"/>. Sessions and their keys live in the sidecar's database, so they survive a
/// restart of either process.
/// </summary>
public sealed class WhatsmeowSessions
{
	private readonly WhatsmeowSidecar _sidecar;
	private readonly SessionService.SessionServiceClient _client;
	private readonly ILogger _log;
	private readonly ConcurrentDictionary<SessionId, SessionClient> _clients = new();

	/// <summary>The sessions the application connected and did not disconnect, log out or delete since (ARCHITECTURE §8.5).</summary>
	private readonly HashSet<SessionId> _connected = [];

	private readonly Lock _lock = new();

	internal WhatsmeowSessions(WhatsmeowSidecar sidecar, CallInvoker invoker, ILogger log)
	{
		_sidecar = sidecar;
		_client = new SessionService.SessionServiceClient(invoker);
		_log = log;
	}

	/// <summary>
	/// Returns the session with <paramref name="sessionId"/>, creating it unpaired (<see cref="SessionState.Created"/>)
	/// when there is none. It returns the same client every time for one id.
	/// </summary>
	/// <exception cref="SidecarUnavailableException">The sidecar cannot be reached, for example while it restarts.</exception>
	public async Task<IWhatsAppClient> GetOrCreateAsync(SessionId sessionId, CancellationToken cancellationToken = default)
	{
		ObjectDisposedException.ThrowIf(_sidecar.IsDisposed, _sidecar);
		_ = await RpcErrors
			.CallAsync(_client.CreateSessionAsync(new CreateSessionRequest { SessionId = sessionId.Value }, cancellationToken: cancellationToken))
			.ConfigureAwait(false);
		return _clients.GetOrAdd(sessionId, static (id, sessions) => new SessionClient(sessions, sessions._sidecar, sessions._client, id), this);
	}

	/// <summary>Every session and its state, ordered by id.</summary>
	/// <exception cref="SidecarUnavailableException">The sidecar cannot be reached.</exception>
	public async Task<IReadOnlyList<SessionInfo>> ListAsync(CancellationToken cancellationToken = default)
	{
		ObjectDisposedException.ThrowIf(_sidecar.IsDisposed, _sidecar);
		var response = await RpcErrors.CallAsync(_client.ListSessionsAsync(new ListSessionsRequest(), cancellationToken: cancellationToken)).ConfigureAwait(false);
		return [.. response.Sessions.Select(SessionMapping.ToInfo)];
	}

	/// <summary>
	/// Disconnects the session if it is connected, then deletes it and its local data, its device keys included. It
	/// does not tell WhatsApp, so a paired device stays linked on the phone; to unlink it, log out first. Watches of it
	/// end with <see cref="SessionNotFoundException"/>.
	/// </summary>
	/// <exception cref="SessionNotFoundException">No session has the id.</exception>
	/// <exception cref="SidecarUnavailableException">The sidecar cannot be reached.</exception>
	public async Task DeleteAsync(SessionId sessionId, CancellationToken cancellationToken = default)
	{
		ObjectDisposedException.ThrowIf(_sidecar.IsDisposed, _sidecar);
		Forget(sessionId);
		_ = await RpcErrors
			.CallAsync(_client.DeleteSessionAsync(new DeleteSessionRequest { SessionId = sessionId.Value }, cancellationToken: cancellationToken))
			.ConfigureAwait(false);
		_ = _clients.TryRemove(sessionId, out _);
	}

	internal void Remember(SessionId sessionId)
	{
		lock (_lock)
			_ = _connected.Add(sessionId);
	}

	internal void Forget(SessionId sessionId)
	{
		lock (_lock)
			_ = _connected.Remove(sessionId);
	}

	/// <summary>
	/// After a sidecar restart, connects again the sessions the application had connected that came back
	/// <see cref="SessionState.Disconnected"/>; a session that stopped (logged out, replaced, banned, outdated, failed)
	/// keeps its state, so two clients never fight over one device. A failure is logged, never thrown.
	/// </summary>
	internal async Task ReconnectAsync(CancellationToken cancellationToken)
	{
		SessionId[] connected;
		lock (_lock)
			connected = [.. _connected];

		if (connected.Length == 0)
			return;

		IReadOnlyList<SessionInfo> sessions;
		try
		{
			sessions = await ListAsync(cancellationToken).ConfigureAwait(false);
		}
		catch (WhatsmeowException e)
		{
			foreach (var id in connected)
				SessionLog.ReconnectFailed(_log, e, id.Value);
			return;
		}

		foreach (var session in sessions)
		{
			if (session is not { State: SessionState.Disconnected } || !connected.Contains(session.Id))
				continue;

			try
			{
				_ = await RpcErrors
					.CallAsync(_client.ConnectAsync(new ConnectRequest { SessionId = session.Id.Value }, cancellationToken: cancellationToken))
					.ConfigureAwait(false);
			}
			catch (WhatsmeowException e)
			{
				SessionLog.ReconnectFailed(_log, e, session.Id.Value);
			}
		}
	}
}
