using Whatsmeow.Pairing;
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
	/// <exception cref="PairingException">The session is being paired (<see cref="WhatsmeowErrorCode.PairingInProgress"/>).</exception>
	/// <exception cref="NotConnectedException">Opening the connection failed; the session is back in its previous state.</exception>
	/// <exception cref="SidecarUnavailableException">The sidecar cannot be reached, for example while it restarts.</exception>
	Task ConnectAsync(CancellationToken cancellationToken = default);

	/// <summary>
	/// Closes the connection and stops reconnecting; the session stays paired, <see cref="SessionState.Disconnected"/>.
	/// Disconnecting a session that is not connected does nothing.
	/// </summary>
	/// <exception cref="SessionNotFoundException">The session was deleted.</exception>
	/// <exception cref="PairingException">
	/// The session is being paired and the phone has not linked it yet (<see cref="WhatsmeowErrorCode.PairingInProgress"/>);
	/// cancel the pairing instead. Once the phone linked it, disconnecting stops the login, and the pairing fails.
	/// </exception>
	/// <exception cref="SidecarUnavailableException">The sidecar cannot be reached.</exception>
	Task DisconnectAsync(CancellationToken cancellationToken = default);

	/// <summary>
	/// Pairs a session that is <see cref="SessionState.Created"/> or <see cref="SessionState.LoggedOut"/> by QR code: it
	/// yields each <see cref="PairingUpdate.QrCode"/> as WhatsApp rotates it, then <see cref="PairingUpdate.Paired"/>,
	/// the last, once the session is usable and <see cref="SessionState.Connected"/>. Each enumeration starts a new
	/// pairing. The client connects the session again after a sidecar restart, as after <see cref="ConnectAsync"/>.
	/// Cancel <paramref name="cancellationToken"/>, or stop enumerating (a <see langword="break"/>, <c>FirstAsync</c>, disposing
	/// the enumerator), to stop: before the phone linked the device, the session goes back to its state before; after,
	/// the login goes on without you, and the session ends up <see cref="SessionState.Connected"/>,
	/// <see cref="SessionState.Disconnected"/>, or in a stopping state WhatsApp ended the login with.
	/// </summary>
	/// <exception cref="PairingException">
	/// <see cref="WhatsmeowErrorCode.AlreadyPaired"/>: the session is paired, which pairing leaves untouched;
	/// <see cref="WhatsmeowErrorCode.PairingInProgress"/>: it is being paired already;
	/// <see cref="WhatsmeowErrorCode.PairingTimedOut"/>: no code was scanned in time, the session is as before;
	/// <see cref="WhatsmeowErrorCode.PairingFailed"/>: pairing failed. Before the link the session is as before; after
	/// it, it is <see cref="SessionState.Disconnected"/> (paired, connecting again retries), or in the stopping state
	/// WhatsApp ended the login with.
	/// </exception>
	/// <exception cref="NotConnectedException">The WhatsApp connection could not be opened, or it dropped; the session is as before.</exception>
	/// <exception cref="SessionNotFoundException">The session was deleted.</exception>
	/// <exception cref="SidecarUnavailableException">The sidecar cannot be reached, or it restarted, which ends the pairing.</exception>
	/// <exception cref="ObjectDisposedException">The sidecar was disposed.</exception>
	IAsyncEnumerable<PairingUpdate> PairWithQrCodeAsync(CancellationToken cancellationToken = default);

	/// <summary>
	/// Pairs a session that is <see cref="SessionState.Created"/> or <see cref="SessionState.LoggedOut"/> by phone
	/// number: the phone gets a notification, and this yields one <see cref="PairingUpdate.PairingCode"/> to enter
	/// on it, then <see cref="PairingUpdate.Paired"/>, as <see cref="PairWithQrCodeAsync"/> does, a new pairing for
	/// each enumeration. Pairing times out when the QR codes WhatsApp rotates meanwhile run out, which took 160 seconds
	/// when measured; how long WhatsApp itself honours the phone code has not been measured.
	/// </summary>
	/// <param name="phoneNumber">The account's phone number in international format; anything but digits is ignored.</param>
	/// <param name="cancellationToken">Cancels pairing, as for <see cref="PairWithQrCodeAsync"/>; so does stopping the enumeration.</param>
	/// <exception cref="WhatsmeowValidationException">The phone number is too short or not international; nothing connected.</exception>
	/// <exception cref="RateLimitedException">WhatsApp rate limited the request; the session is as before.</exception>
	/// <exception cref="WhatsmeowException">WhatsApp refused, with <see cref="WhatsmeowErrorCode.WhatsAppError"/>.</exception>
	/// <exception cref="PairingException">As for <see cref="PairWithQrCodeAsync"/>.</exception>
	/// <exception cref="NotConnectedException">As for <see cref="PairWithQrCodeAsync"/>.</exception>
	/// <exception cref="SessionNotFoundException">The session was deleted.</exception>
	/// <exception cref="SidecarUnavailableException">As for <see cref="PairWithQrCodeAsync"/>.</exception>
	/// <exception cref="ObjectDisposedException">The sidecar was disposed.</exception>
	/// <exception cref="ArgumentNullException"><paramref name="phoneNumber"/> is null.</exception>
	IAsyncEnumerable<PairingUpdate> PairWithPhoneNumberAsync(string phoneNumber, CancellationToken cancellationToken = default);

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
