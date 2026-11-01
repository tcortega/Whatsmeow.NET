using System.Runtime.CompilerServices;
using Grpc.Core;
using Whatsmeow.Core.Errors;
using Whatsmeow.Pairing;
using Whatsmeow.Protocol.V1;

namespace Whatsmeow.Sessions;

/// <summary>A session's <see cref="IWhatsAppClient"/>: calls on the sidecar's one channel.</summary>
internal sealed class SessionClient(WhatsmeowSessions sessions, WhatsmeowSidecar sidecar, SessionService.SessionServiceClient client, SessionId sessionId)
	: IWhatsAppClient
{
	public SessionId SessionId => sessionId;

	public async Task ConnectAsync(CancellationToken cancellationToken = default)
	{
		ObjectDisposedException.ThrowIf(sidecar.IsDisposed, sidecar);
		_ = await RpcErrors
			.CallAsync(client.ConnectAsync(new ConnectRequest { SessionId = sessionId.Value }, cancellationToken: cancellationToken))
			.ConfigureAwait(false);
		sessions.Remember(sessionId);
	}

	public async Task DisconnectAsync(CancellationToken cancellationToken = default)
	{
		ObjectDisposedException.ThrowIf(sidecar.IsDisposed, sidecar);

		// Forgotten first, so a restart in between does not connect it again.
		sessions.Forget(sessionId);
		_ = await RpcErrors
			.CallAsync(client.DisconnectAsync(new DisconnectRequest { SessionId = sessionId.Value }, cancellationToken: cancellationToken))
			.ConfigureAwait(false);
	}

	public async Task LogoutAsync(CancellationToken cancellationToken = default)
	{
		ObjectDisposedException.ThrowIf(sidecar.IsDisposed, sidecar);

		// Forgotten only once it happened: a logout that failed leaves a session the application still connected.
		_ = await RpcErrors.CallAsync(client.LogoutAsync(new LogoutRequest { SessionId = sessionId.Value }, cancellationToken: cancellationToken)).ConfigureAwait(false);
		sessions.Forget(sessionId);
	}

	public IAsyncEnumerable<PairingUpdate> PairWithQrCodeAsync(CancellationToken cancellationToken = default) =>
		PairAsync(new PairRequest { SessionId = sessionId.Value }, cancellationToken);

	public IAsyncEnumerable<PairingUpdate> PairWithPhoneNumberAsync(string phoneNumber, CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(phoneNumber);
		return PairAsync(
			new PairRequest { SessionId = sessionId.Value, Phone = new PairRequest.Types.Phone { PhoneNumber = phoneNumber } },
			cancellationToken
		);
	}

	private async IAsyncEnumerable<PairingUpdate> PairAsync(PairRequest request, [EnumeratorCancellation] CancellationToken cancellationToken)
	{
		ObjectDisposedException.ThrowIf(sidecar.IsDisposed, sidecar);

		using var call = client.Pair(request, cancellationToken: cancellationToken);

		while (await PairNextAsync(call.ResponseStream, cancellationToken).ConfigureAwait(false))
		{
			if (PairingMapping.ToUpdate(call.ResponseStream.Current) is not { } update)
				continue;

			// The first code means pairing started: a pairing that goes on to link the device ends Connected even when
			// the caller stops reading first, so it is a session the application connected, and a sidecar restart
			// connects it again. A refused call (paired, invalid, gone) never got this far.
			if (update is PairingUpdate.QrCode or PairingUpdate.PairingCode)
				sessions.Remember(sessionId);
			yield return update;
		}
	}

	/// <summary>Reads the next pairing step: false at the end of the stream, otherwise it throws.</summary>
	private async Task<bool> PairNextAsync(IAsyncStreamReader<PairResponse> stream, CancellationToken cancellationToken)
	{
		try
		{
			return await stream.MoveNext(cancellationToken).ConfigureAwait(false);
		}
		catch (RpcException) when (sidecar.IsDisposed)
		{
			ObjectDisposedException.ThrowIf(condition: true, sidecar);
			throw;
		}
		catch (RpcException e)
		{
			RpcErrors.Throw(e);
			throw;
		}
	}

	public async IAsyncEnumerable<SessionState> WatchStateAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
	{
		while (true)
		{
			ObjectDisposedException.ThrowIf(sidecar.IsDisposed, sidecar);
			var launch = sidecar.Launches;
			using var call = client.WatchSessionState(new WatchSessionStateRequest { SessionId = sessionId.Value }, cancellationToken: cancellationToken);

			while (await NextAsync(call.ResponseStream, cancellationToken).ConfigureAwait(false) is { } more)
			{
				if (!more)
					yield break;
				yield return SessionMapping.ToState(call.ResponseStream.Current.State);
			}

			// The sidecar went away: the stream goes on from the restarted one, whose first state is its current one.
			await sidecar.WaitForLaunchAsync(launch, cancellationToken).ConfigureAwait(false);
		}
	}

	/// <summary>Reads the next state: true when there is one, null when the sidecar went away, otherwise it throws.</summary>
	private async Task<bool?> NextAsync(IAsyncStreamReader<WatchSessionStateResponse> stream, CancellationToken cancellationToken)
	{
		try
		{
			return await stream.MoveNext(cancellationToken).ConfigureAwait(false);
		}
		catch (RpcException) when (sidecar.IsDisposed)
		{
			ObjectDisposedException.ThrowIf(condition: true, sidecar);
			throw;
		}
		catch (RpcException e) when (RpcErrors.IsUnreachable(e))
		{
			return null;
		}
		catch (RpcException e)
		{
			RpcErrors.Throw(e);
			throw;
		}
	}
}
