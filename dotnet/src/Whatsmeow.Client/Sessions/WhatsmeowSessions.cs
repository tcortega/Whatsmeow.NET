using System.Collections.Concurrent;
using System.Collections.Immutable;
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
	private readonly CallInvoker _invoker;
	private readonly ILogger _log;
	private readonly Action<IWhatsAppClient>? _clientCreated;
	private readonly IReadOnlySet<SessionId> _declared;
	private readonly ConcurrentDictionary<SessionId, SessionClient> _clients = new();

	/// <summary>The sessions the application connected and did not disconnect, log out or delete since (ARCHITECTURE §8.5).</summary>
	private readonly HashSet<SessionId> _connected = [];

	private readonly Lock _lock = new();

	/// <summary>Taken to create a client, so <see cref="_clientCreated"/> runs once per client, before anyone gets it.</summary>
	private readonly Lock _creating = new();

	internal WhatsmeowSessions(WhatsmeowSidecar sidecar, CallInvoker invoker, ILogger log, Action<IWhatsAppClient>? clientCreated, IReadOnlySet<SessionId> declared)
	{
		_sidecar = sidecar;
		_client = new SessionService.SessionServiceClient(invoker);
		_invoker = invoker;
		_log = log;
		_clientCreated = clientCreated;
		_declared = declared;
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
			.CallAsync(_client.CreateSessionAsync(new CreateSessionRequest { SessionId = sessionId.Value }, cancellationToken: cancellationToken), cancellationToken)
			.ConfigureAwait(false);
		return Client(sessionId);
	}

	/// <summary>
	/// The client of <paramref name="sessionId"/>, the one <see cref="GetOrCreateAsync"/> returns, without creating the
	/// session in the sidecar: the hosting package hands it out before the sidecar starts (ADR 0034).
	/// </summary>
	internal SessionClient Client(SessionId sessionId)
	{
		if (_clients.TryGetValue(sessionId, out var existing))
			return existing;

		lock (_creating)
		{
			if (_clients.TryGetValue(sessionId, out existing))
				return existing;

			var client = NewClient(sessionId);
			_clientCreated?.Invoke(client);
			_clients[sessionId] = client;
			return client;
		}
	}

	/// <summary>Every session and its state, ordered by id.</summary>
	/// <exception cref="SidecarUnavailableException">The sidecar cannot be reached.</exception>
	public async Task<ImmutableArray<SessionInfo>> ListAsync(CancellationToken cancellationToken = default)
	{
		ObjectDisposedException.ThrowIf(_sidecar.IsDisposed, _sidecar);
		var response = await RpcErrors.CallAsync(_client.ListSessionsAsync(new ListSessionsRequest(), cancellationToken: cancellationToken), cancellationToken).ConfigureAwait(false);
		return [.. response.Sessions.Select(SessionMapping.ToInfo)];
	}

	/// <summary>
	/// Disconnects the session if it is connected, then deletes it and its local data, its device keys included. It
	/// does not tell WhatsApp, so a paired device stays linked on the phone; to unlink it, log out first. Watches of it
	/// end with <see cref="SessionNotFoundException"/>.
	/// </summary>
	/// <exception cref="SessionNotFoundException">No session has the id.</exception>
	/// <exception cref="SidecarUnavailableException">The sidecar cannot be reached.</exception>
	/// <exception cref="InvalidOperationException">
	/// The session is declared with <c>AddSession</c> of <c>Whatsmeow.Extensions.Hosting</c>: its keyed client would stay behind.
	/// </exception>
	public async Task DeleteAsync(SessionId sessionId, CancellationToken cancellationToken = default)
	{
		ObjectDisposedException.ThrowIf(_sidecar.IsDisposed, _sidecar);
		if (_declared.Contains(sessionId))
			throw new InvalidOperationException($"Session {sessionId.Value} is declared with AddSession, so it cannot be deleted while the host runs: its keyed client would stay behind.");

		_ = await RpcErrors
			.CallAsync(_client.DeleteSessionAsync(new DeleteSessionRequest { SessionId = sessionId.Value }, cancellationToken: cancellationToken), cancellationToken)
			.ConfigureAwait(false);

		// Forgotten only once it is gone: a delete that failed leaves a session the application still connected.
		Forget(sessionId);
		if (_clients.TryRemove(sessionId, out var removed))
			await removed.DisposeAsync().ConfigureAwait(false);
	}

	private SessionClient NewClient(SessionId sessionId) => new(this, _sidecar, _client, _invoker, sessionId, _log);

	/// <summary>Stops every session's events and acks what was handled, while the sidecar still runs.</summary>
	/// <param name="stopWaiting">Once cancelled, no session waits for handlers that ignore their cancellation (ADR 0034).</param>
	/// <remarks>All at once: every session's handlers see their cancellation together, and the stop takes the slowest session's time, not their sum.</remarks>
	internal async Task StopEventsAsync(CancellationToken stopWaiting) =>
		await Task.WhenAll(_clients.Values.Select(client => StopOneAsync(client, stopWaiting))).ConfigureAwait(false);

	private async Task StopOneAsync(SessionClient client, CancellationToken stopWaiting)
	{
		// Each on its own: one client's failure must not leave the others' handlers running and their acks unsent.
		try
		{
			await client.StopEventsAsync(stopWaiting).ConfigureAwait(false);
		}
#pragma warning disable CA1031 // Justification: logged; the sidecar's stop goes on
		catch (Exception e)
#pragma warning restore CA1031
		{
			SessionLog.StopEventsFailed(_log, e, client.SessionId.Value);
		}
	}

	internal void Remember(SessionId sessionId)
	{
		lock (_lock)
			_ = _connected.Add(sessionId);
	}

	private bool IsRemembered(SessionId sessionId)
	{
		lock (_lock)
			return _connected.Contains(sessionId);
	}

	internal void Forget(SessionId sessionId)
	{
		lock (_lock)
			_ = _connected.Remove(sessionId);
	}

	/// <summary>Connects the host's eligible declarations together, then reports the first failure after starting every successful session's events.</summary>
	internal async Task ConnectAsync(IReadOnlyCollection<SessionId> sessionIds, CancellationToken cancellationToken)
	{
		ObjectDisposedException.ThrowIf(_sidecar.IsDisposed, _sidecar);
		if (sessionIds.Count == 0)
			return;

		var response = await ConnectSessionsAsync(sessionIds.Select(id => id.Value), cancellationToken).ConfigureAwait(false);
		foreach (var result in response.Results)
		{
			if (result.Reason is ErrorReason.Unspecified)
				// The sidecar's own client has the handlers, even if the application's keyed registration was replaced.
				Client(SessionId.From(result.SessionId)).ConnectSucceeded();
		}

		if (response.Results.FirstOrDefault(result => result.Reason is not ErrorReason.Unspecified) is { } failed)
			RpcErrors.Throw(failed.Reason);
	}

	private Task<ConnectSessionsResponse> ConnectSessionsAsync(IEnumerable<string> ids, CancellationToken cancellationToken) =>
		RpcErrors.CallAsync(_client.ConnectSessionsAsync(new ConnectSessionsRequest { SessionIds = { ids } }, cancellationToken: cancellationToken), cancellationToken);

	/// <summary>
	/// After a sidecar restart, connects again the sessions the application had connected that came back
	/// <see cref="SessionState.Disconnected"/>; a session that stopped (logged out, replaced, banned, outdated, failed)
	/// keeps its state, so two clients never fight over one device. A failure is logged, never thrown: of the call, for
	/// each session, or of one session, for it.
	/// </summary>
	internal async Task ReconnectAsync(CancellationToken cancellationToken)
	{
		SessionId[] connected;
		lock (_lock)
			connected = [.. _connected];

		if (connected.Length == 0)
			return;

		ImmutableArray<SessionInfo> sessions;
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
		catch (ObjectDisposedException) when (_sidecar.IsDisposed)
		{
			// The sidecar is stopping, so there is nothing to connect again, and this is not a failed launch.
			return;
		}

		// Checked again now, not only in the snapshot: the application may have disconnected one meanwhile.
		string[] ids = [.. sessions.Where(session => session is { State: SessionState.Disconnected } && IsRemembered(session.Id)).Select(session => session.Id.Value)];
		if (ids.Length == 0)
			return;

		// One call for all of them: the sidecar records their Connecting states in one commit (#77).
		ConnectSessionsResponse response;
		try
		{
			response = await ConnectSessionsAsync(ids, cancellationToken).ConfigureAwait(false);
		}
		catch (WhatsmeowException e)
		{
			foreach (var id in ids)
				SessionLog.ReconnectFailed(_log, e, id);
			return;
		}

		foreach (var result in response.Results)
		{
			if (result.Reason is not ErrorReason.Unspecified)
				SessionLog.ReconnectRefused(_log, result.SessionId, RpcErrors.Code(result.Reason));
		}
	}
}
