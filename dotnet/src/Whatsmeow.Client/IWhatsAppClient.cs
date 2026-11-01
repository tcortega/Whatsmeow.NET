using Whatsmeow.Sessions;

namespace Whatsmeow;

/// <summary>
/// One session (one WhatsApp account) of a <see cref="WhatsmeowSidecar"/>, from
/// <see cref="WhatsmeowSessions.GetOrCreateAsync"/>. It is a lightweight view over the sidecar: the session lives in
/// the sidecar, and this interface exists so applications can substitute it in their own tests (ADR 0012).
/// </summary>
public interface IWhatsAppClient
{
	/// <summary>The session's id.</summary>
	SessionId SessionId { get; }

	/// <summary>
	/// Connects the paired session. It returns once the WhatsApp connection is open, with the session
	/// <see cref="SessionState.Connecting"/> until WhatsApp accepts the login, or once whatsmeow is retrying
	/// (<see cref="SessionState.Reconnecting"/>). A login WhatsApp refuses arrives as the session's state. Connecting
	/// a session that is connecting or connected does nothing. The client connects it again after a sidecar restart
	/// when it comes back <see cref="SessionState.Disconnected"/>.
	/// </summary>
	/// <exception cref="SessionNotFoundException">The session was deleted.</exception>
	/// <exception cref="NotLoggedInException">The session has never been paired.</exception>
	/// <exception cref="SessionLoggedOutException">The session was logged out.</exception>
	/// <exception cref="TemporarilyBannedException">The account is banned, and the ban has a known end that has not passed.</exception>
	/// <exception cref="NotConnectedException">Opening the connection failed; the session is back in its previous state.</exception>
	/// <exception cref="SidecarUnavailableException">The sidecar cannot be reached, for example while it restarts.</exception>
	Task ConnectAsync(CancellationToken cancellationToken = default);

	/// <summary>
	/// Closes the connection and stops reconnecting; the session stays paired, <see cref="SessionState.Disconnected"/>.
	/// Disconnecting a session that is not connected does nothing.
	/// </summary>
	/// <exception cref="SessionNotFoundException">The session was deleted.</exception>
	/// <exception cref="SidecarUnavailableException">The sidecar cannot be reached.</exception>
	Task DisconnectAsync(CancellationToken cancellationToken = default);

	/// <summary>
	/// Unlinks the device on WhatsApp, then deletes its keys; the session stays, <see cref="SessionState.LoggedOut"/>
	/// with code 0, and can be paired again. WhatsApp is told first, so the session must be connected.
	/// </summary>
	/// <exception cref="SessionNotFoundException">The session was deleted.</exception>
	/// <exception cref="NotLoggedInException">The session has never been paired.</exception>
	/// <exception cref="SessionLoggedOutException">The session is already logged out.</exception>
	/// <exception cref="NotConnectedException">The session is not connected.</exception>
	/// <exception cref="OutcomeUnknownException">No answer came from WhatsApp: the device may or may not be unlinked.</exception>
	/// <exception cref="WhatsmeowException">WhatsApp refused, with <see cref="WhatsmeowErrorCode.WhatsAppError"/>.</exception>
	/// <exception cref="SidecarUnavailableException">The sidecar cannot be reached.</exception>
	Task LogoutAsync(CancellationToken cancellationToken = default);

	/// <summary>
	/// The session's state: the current one first, then each change, until <paramref name="cancellationToken"/> is
	/// cancelled. A reader that falls behind gets the latest state and can miss intermediate ones. When the sidecar
	/// restarts, the watch goes on with the state the restarted sidecar reports, first.
	/// </summary>
	/// <exception cref="SessionNotFoundException">The session does not exist, or it was deleted while watching.</exception>
	/// <exception cref="ObjectDisposedException">The sidecar was disposed.</exception>
	IAsyncEnumerable<SessionState> WatchStateAsync(CancellationToken cancellationToken = default);
}
