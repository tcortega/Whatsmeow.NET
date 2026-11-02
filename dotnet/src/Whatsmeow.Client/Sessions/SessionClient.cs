using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using Grpc.Core;
using Whatsmeow.Contacts;
using Whatsmeow.Core.Errors;
using Whatsmeow.Events;
using Whatsmeow.Pairing;
using Whatsmeow.Protocol.V1;
using BlockedUser = Whatsmeow.Contacts.BlockedUser;
using Contact = Whatsmeow.Contacts.Contact;
using PhoneNumberCheck = Whatsmeow.Contacts.PhoneNumberCheck;
using ResolvedJid = Whatsmeow.Contacts.ResolvedJid;
using UserInfo = Whatsmeow.Contacts.UserInfo;

namespace Whatsmeow.Sessions;

/// <summary>A session's <see cref="IWhatsAppClient"/>: calls on the sidecar's one channel.</summary>
internal sealed class SessionClient(
	WhatsmeowSessions sessions,
	WhatsmeowSidecar sidecar,
	SessionService.SessionServiceClient client,
	CallInvoker invoker,
	SessionId sessionId,
	ILogger log
) : IWhatsAppClient, IAsyncDisposable
{
	/// <summary>How long a watch waits before it opens again after a call that ended without a status.</summary>
	private static readonly TimeSpan s_retryDelay = TimeSpan.FromSeconds(1);

	private readonly SessionEvents _events = new(sidecar, invoker, sessionId, sidecar.TimeProvider, log);
	private readonly ContactService.ContactServiceClient _contacts = new(invoker);

	public SessionId SessionId => sessionId;

	public IDisposable On<TEvent>(Func<TEvent, CancellationToken, ValueTask> handler)
		where TEvent : WhatsAppEvent
	{
		ObjectDisposedException.ThrowIf(_events.IsDisposed, this);
		return _events.On(handler);
	}

	public Task<TEvent> WaitForAsync<TEvent>(Func<TEvent, bool> predicate, TimeSpan timeout, CancellationToken cancellationToken = default)
		where TEvent : WhatsAppEvent
	{
		ObjectDisposedException.ThrowIf(_events.IsDisposed, this);
		return _events.WaitForAsync(predicate, timeout, cancellationToken);
	}

	public IDisposable OnHandlerError(Action<HandlerError> handler)
	{
		ObjectDisposedException.ThrowIf(_events.IsDisposed, this);
		return _events.OnHandlerError(handler);
	}

	/// <summary>Stops the session's events and acks what was handled.</summary>
	public ValueTask DisposeAsync() => _events.DisposeAsync();

	public async Task ConnectAsync(CancellationToken cancellationToken = default)
	{
		ObjectDisposedException.ThrowIf(sidecar.IsDisposed, sidecar);
		ObjectDisposedException.ThrowIf(_events.IsDisposed, this);
		_ = await RpcErrors
			.CallAsync(client.ConnectAsync(new ConnectRequest { SessionId = sessionId.Value }, cancellationToken: cancellationToken))
			.ConfigureAwait(false);
		sessions.Remember(sessionId);

		// Events flow from the first connect in this process (ADR 0022).
		_events.Start();
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

			Next next;
			while ((next = await NextAsync(call.ResponseStream, launch, cancellationToken).ConfigureAwait(false)) is Next.State)
				yield return SessionMapping.ToState(call.ResponseStream.Current.State);

			if (next is Next.End)
				yield break;

			// The sidecar went away: the stream goes on from the restarted one, whose first state is its current one.
			if (next is Next.Gone)
				await sidecar.WaitForLaunchAsync(launch, cancellationToken).ConfigureAwait(false);
		}
	}

	/// <summary>What reading the next state found.</summary>
	private enum Next
	{
		/// <summary>A state.</summary>
		State,

		/// <summary>The stream ended.</summary>
		End,

		/// <summary>The sidecar went away: open again once it is back.</summary>
		Gone,

		/// <summary>The call ended without a status: open again now.</summary>
		Again,
	}

	/// <summary>Reads the next state, or finds why there is none; a failure the watch cannot go on from throws.</summary>
	private async Task<Next> NextAsync(IAsyncStreamReader<WatchSessionStateResponse> stream, int launch, CancellationToken cancellationToken)
	{
		try
		{
			return await stream.MoveNext(cancellationToken).ConfigureAwait(false) ? Next.State : Next.End;
		}
		catch (RpcException) when (sidecar.IsDisposed)
		{
			ObjectDisposedException.ThrowIf(condition: true, sidecar);
			throw;
		}
		catch (RpcException e) when (RpcErrors.IsUnreachable(e))
		{
			return Next.Gone;
		}
		catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
		{
			// A call can end with no status at all, as one to a sidecar that is gone does [observed: WatchDownTests]. The
			// same sidecar may still be up, so it does not wait for a launch: it opens again, after a pause if none
			// happened since the call opened.
			if (sidecar.Launches == launch)
				await Task.Delay(s_retryDelay, sidecar.TimeProvider, cancellationToken).ConfigureAwait(false);
			return Next.Again;
		}
		catch (RpcException e)
		{
			RpcErrors.Throw(e);
			throw;
		}
	}

	public async Task<ImmutableArray<PhoneNumberCheck>> IsOnWhatsAppAsync(IReadOnlyList<string> phoneNumbers, CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(phoneNumbers);
		ObjectDisposedException.ThrowIf(sidecar.IsDisposed, sidecar);
		var request = new IsOnWhatsAppRequest { SessionId = sessionId.Value, PhoneNumbers = { phoneNumbers } };
		return ContactMapping.ToChecks(await RpcErrors.CallAsync(_contacts.IsOnWhatsAppAsync(request, cancellationToken: cancellationToken)).ConfigureAwait(false));
	}

	public async Task<ImmutableArray<UserInfo>> GetUserInfoAsync(IReadOnlyList<Jid> users, CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(users);
		ObjectDisposedException.ThrowIf(sidecar.IsDisposed, sidecar);
		var request = new GetUserInfoRequest { SessionId = sessionId.Value, Jids = { users.Select(jid => jid.ToString()) } };
		return ContactMapping.ToUsers(await RpcErrors.CallAsync(_contacts.GetUserInfoAsync(request, cancellationToken: cancellationToken)).ConfigureAwait(false));
	}

	public async Task<ProfilePicture> GetProfilePictureAsync(Jid jid, CancellationToken cancellationToken = default)
	{
		ObjectDisposedException.ThrowIf(sidecar.IsDisposed, sidecar);
		var request = new GetProfilePictureRequest { SessionId = sessionId.Value, Jid = jid.ToString() };
		return ContactMapping.ToPicture(await RpcErrors.CallAsync(_contacts.GetProfilePictureAsync(request, cancellationToken: cancellationToken)).ConfigureAwait(false));
	}

	public async Task<ImmutableArray<ResolvedJid>> ResolveJidsAsync(IReadOnlyList<Jid> jids, CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(jids);
		ObjectDisposedException.ThrowIf(sidecar.IsDisposed, sidecar);
		var request = new ResolveJidsRequest { SessionId = sessionId.Value, Jids = { jids.Select(jid => jid.ToString()) } };
		return ContactMapping.ToResolved(await RpcErrors.CallAsync(_contacts.ResolveJidsAsync(request, cancellationToken: cancellationToken)).ConfigureAwait(false));
	}

	public async Task<ImmutableArray<Contact>> GetContactsAsync(CancellationToken cancellationToken = default)
	{
		ObjectDisposedException.ThrowIf(sidecar.IsDisposed, sidecar);
		var request = new ListContactsRequest { SessionId = sessionId.Value };
		return ContactMapping.ToContacts(await RpcErrors.CallAsync(_contacts.ListContactsAsync(request, cancellationToken: cancellationToken)).ConfigureAwait(false));
	}

	public async Task<ImmutableArray<BlockedUser>> GetBlocklistAsync(CancellationToken cancellationToken = default)
	{
		ObjectDisposedException.ThrowIf(sidecar.IsDisposed, sidecar);
		var request = new GetBlocklistRequest { SessionId = sessionId.Value };
		return ContactMapping.ToBlocked(await RpcErrors.CallAsync(_contacts.GetBlocklistAsync(request, cancellationToken: cancellationToken)).ConfigureAwait(false));
	}

	public Task BlockAsync(Jid user, CancellationToken cancellationToken = default) => UpdateBlocklistAsync(user, BlocklistAction.Block, cancellationToken);

	public Task UnblockAsync(Jid user, CancellationToken cancellationToken = default) => UpdateBlocklistAsync(user, BlocklistAction.Unblock, cancellationToken);

	private async Task UpdateBlocklistAsync(Jid user, BlocklistAction action, CancellationToken cancellationToken)
	{
		ObjectDisposedException.ThrowIf(sidecar.IsDisposed, sidecar);
		var request = new UpdateBlocklistRequest { SessionId = sessionId.Value, Jid = user.ToString(), Action = action };
		_ = await RpcErrors.CallAsync(_contacts.UpdateBlocklistAsync(request, cancellationToken: cancellationToken)).ConfigureAwait(false);
	}
}
