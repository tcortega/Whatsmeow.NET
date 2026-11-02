using System.Collections.Immutable;
using Whatsmeow.Contacts;
using Whatsmeow.Events;
using Whatsmeow.Pairing;
using Whatsmeow.Presence;
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
	/// <exception cref="ObjectDisposedException">The session was deleted through this client's <see cref="WhatsmeowSessions"/>, or the sidecar disposed.</exception>
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
	/// pairing. Keep the session connected after <see cref="PairingUpdate.Paired"/> until it is
	/// <see cref="SessionState.CaughtUp"/>: a device that went offline right after pairing was seen to stay
	/// "connecting" on the phone. Once the first code came, the client connects the session again after a sidecar
	/// restart, as after <see cref="ConnectAsync"/>.
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
	/// Tells WhatsApp the account is online (<see cref="PresenceState.Available"/>) or offline. Call it once after
	/// connecting, so WhatsApp has the account's push name, which whatsmeow sends with it. While available, whatsmeow
	/// answers messages with active delivery receipts; while unavailable, with inactive ones. It returns once the update
	/// is handed to the connection; WhatsApp sends no answer.
	/// </summary>
	/// <exception cref="WhatsmeowException">
	/// <see cref="WhatsmeowErrorCode.NoPushName"/>: the account has no push name yet, which WhatsApp sends with the first
	/// app state sync after pairing; nothing was sent, try again later.
	/// </exception>
	/// <exception cref="WhatsmeowValidationException"><paramref name="state"/> is not a defined value.</exception>
	/// <exception cref="SessionNotFoundException">The session was deleted.</exception>
	/// <exception cref="NotLoggedInException">The session has never been paired.</exception>
	/// <exception cref="SessionLoggedOutException">The session was logged out.</exception>
	/// <exception cref="NotConnectedException">The session is not <see cref="SessionState.Connected"/> or <see cref="SessionState.CaughtUp"/>; nothing was sent.</exception>
	/// <exception cref="TemporarilyBannedException">The account is temporarily banned.</exception>
	/// <exception cref="SidecarUnavailableException">The sidecar cannot be reached.</exception>
	Task SendPresenceAsync(PresenceState state, CancellationToken cancellationToken = default);

	/// <summary>
	/// Asks WhatsApp to send <paramref name="user"/>'s presence, which it sends only after a subscription: changes
	/// then arrive as <see cref="PresenceChanged"/> events, best effort. whatsmeow notes that WhatsApp seems to require
	/// the account to be available (<see cref="SendPresenceAsync"/>) for them; whether it sends any to an unavailable
	/// account has not been measured.
	/// </summary>
	/// <exception cref="WhatsmeowValidationException"><paramref name="user"/> is not a user JID.</exception>
	/// <exception cref="SessionNotFoundException">The session was deleted.</exception>
	/// <exception cref="NotLoggedInException">The session has never been paired.</exception>
	/// <exception cref="SessionLoggedOutException">The session was logged out.</exception>
	/// <exception cref="NotConnectedException">The session is not connected; nothing was sent.</exception>
	/// <exception cref="TemporarilyBannedException">The account is temporarily banned.</exception>
	/// <exception cref="SidecarUnavailableException">The sidecar cannot be reached.</exception>
	Task SubscribePresenceAsync(Jid user, CancellationToken cancellationToken = default);

	/// <summary>
	/// Shows the other side of <paramref name="chat"/> that the account is typing (<see cref="ChatPresenceState.Typing"/>)
	/// or recording audio (<see cref="ChatPresenceState.Recording"/>), or stops it (<see cref="ChatPresenceState.Paused"/>).
	/// It returns once the update is handed to the connection; WhatsApp sends no answer.
	/// </summary>
	/// <exception cref="WhatsmeowValidationException"><paramref name="chat"/> is not a chat JID, or <paramref name="state"/> is not a defined value.</exception>
	/// <exception cref="SessionNotFoundException">The session was deleted.</exception>
	/// <exception cref="NotLoggedInException">The session has never been paired.</exception>
	/// <exception cref="SessionLoggedOutException">The session was logged out.</exception>
	/// <exception cref="NotConnectedException">The session is not connected; nothing was sent.</exception>
	/// <exception cref="TemporarilyBannedException">The account is temporarily banned.</exception>
	/// <exception cref="SidecarUnavailableException">The sidecar cannot be reached.</exception>
	Task SendChatPresenceAsync(Jid chat, ChatPresenceState state, CancellationToken cancellationToken = default);

	/// <summary>
	/// The session's state: the current one first, then each change, until <paramref name="cancellationToken"/> is
	/// cancelled. A reader that falls behind gets the latest state and can miss intermediate ones. When the sidecar
	/// restarts, the watch goes on with the state the restarted sidecar reports, first.
	/// </summary>
	/// <exception cref="SessionNotFoundException">The session does not exist, or it was deleted while watching.</exception>
	/// <exception cref="ObjectDisposedException">The sidecar was disposed.</exception>
	IAsyncEnumerable<SessionState> WatchStateAsync(CancellationToken cancellationToken = default);

	/// <summary>
	/// Calls <paramref name="handler"/> for every event of the session that is a <typeparamref name="TEvent"/>, until
	/// the returned registration is disposed. Events flow once the session was connected in this process
	/// (<see cref="ConnectAsync"/>), starting with the ones not handled before, also by an earlier process: register
	/// handlers before connecting, because an event dispatched before a handler exists is not repeated for it.
	/// Events of one chat reach the handlers one at a time, in order; different chats run concurrently. An event's
	/// handlers run one after the other, and the event is acknowledged to the sidecar only after all of them returned.
	/// A handler that throws does not stop the stream: the event counts as handled, and <see cref="OnHandlerError"/>
	/// is raised. Delivery is at least once, so handlers must be idempotent: after a crash of either process an event
	/// can come again, with the same <see cref="WhatsAppEvent.SessionId"/> and message id, which together are the
	/// dedupe key. The token is cancelled when the sidecar is disposed or the session deleted; a handler that ends while
	/// it is cancelled, by returning or by throwing, has not handled the event, which comes again in the next process.
	/// A handler may delete its own session or dispose the sidecar; that call then does not wait for the handler itself.
	/// Disposing the registration stops new events from reaching the handler, but an event already being dispatched
	/// can still reach it once.
	/// The ephemeral events, <see cref="PresenceChanged"/>, <see cref="ChatPresenceChanged"/> and
	/// <see cref="EphemeralEventsDropped"/>, are the exception to at least once: best effort. The sidecar keeps them in
	/// memory only, never acknowledges them and never sends one again: one a handler did not finish, one that arrived
	/// before the sidecar restarted, and the oldest when more arrive than it keeps (256 per session) while the handlers
	/// fall behind or the session is not connected in this process are gone, and <see cref="EphemeralEventsDropped"/>
	/// says how many were dropped. They run in lanes of their own, one per user or chat, in order among themselves but
	/// in no order with the other events, and never hold back or delay them.
	/// The events end for good, logged as an error, when the session is deleted, or when another client of the same
	/// sidecar database streams them under the same consumer and replaces this one.
	/// </summary>
	/// <exception cref="ObjectDisposedException">The session was deleted, or the sidecar disposed.</exception>
	IDisposable On<TEvent>(Func<TEvent, CancellationToken, ValueTask> handler)
		where TEvent : WhatsAppEvent;

	/// <summary>
	/// Waits for the next event of the session that is a <typeparamref name="TEvent"/> and matches
	/// <paramref name="predicate"/>. It is a handler like the ones of <see cref="On{TEvent}"/>, in the same dispatcher, so
	/// it sees events in the same order, and only the ones dispatched after it was called. A predicate that throws is a
	/// handler error (<see cref="OnHandlerError"/>). Called from a handler and awaited there, it waits for an event of the
	/// same chat only until it times out: that event runs after the handler that waits for it.
	/// </summary>
	/// <exception cref="TimeoutException">No such event came within <paramref name="timeout"/>.</exception>
	/// <exception cref="ObjectDisposedException">The session was deleted, or the sidecar disposed, before or while it waited.</exception>
	Task<TEvent> WaitForAsync<TEvent>(Func<TEvent, bool> predicate, TimeSpan timeout, CancellationToken cancellationToken = default)
		where TEvent : WhatsAppEvent;

	/// <summary>
	/// Calls <paramref name="handler"/> whenever a handler of the session's events throws, until the returned
	/// registration is disposed. It runs right after the failure, before the next handler of the same chat.
	/// </summary>
	/// <exception cref="ObjectDisposedException">The session was deleted, or the sidecar disposed.</exception>
	IDisposable OnHandlerError(Action<HandlerError> handler);

	/// <summary>
	/// Checks whether phone numbers are on WhatsApp: one result per number, in the order given, with the number's JID
	/// and its LID when it is on WhatsApp. It also teaches the session's store each LID, for
	/// <see cref="ResolveJidsAsync"/>. An empty list returns an empty list without asking WhatsApp.
	/// </summary>
	/// <param name="phoneNumbers">International phone numbers: digits with an optional leading <c>+</c>.</param>
	/// <param name="cancellationToken">Cancels the call.</param>
	/// <exception cref="WhatsmeowValidationException">A number is not digits with an optional leading <c>+</c>; nothing was asked.</exception>
	/// <exception cref="RateLimitedException">WhatsApp rate limited the lookup.</exception>
	/// <exception cref="NotConnectedException">The session is not logged in to WhatsApp right now; it never waits for a connection.</exception>
	/// <exception cref="NotLoggedInException">The session has never been paired.</exception>
	/// <exception cref="SessionLoggedOutException">The session was logged out.</exception>
	/// <exception cref="TemporarilyBannedException">The account is temporarily banned.</exception>
	/// <exception cref="SessionNotFoundException">The session was deleted.</exception>
	/// <exception cref="WhatsmeowException">
	/// WhatsApp refused, with <see cref="WhatsmeowErrorCode.WhatsAppError"/>, or did not answer in time, with
	/// <see cref="WhatsmeowErrorCode.WhatsAppTimedOut"/>: nothing was done, and the call can be repeated.
	/// </exception>
	/// <exception cref="SidecarUnavailableException">The sidecar cannot be reached.</exception>
	/// <exception cref="ObjectDisposedException">The sidecar was disposed.</exception>
	/// <exception cref="ArgumentNullException"><paramref name="phoneNumbers"/> is null.</exception>
	Task<ImmutableArray<PhoneNumberCheck>> IsOnWhatsAppAsync(IReadOnlyList<string> phoneNumbers, CancellationToken cancellationToken = default);

	/// <summary>
	/// Reads users' profiles: the about text, the verified business name and the picture id, when WhatsApp has them. One
	/// entry per user WhatsApp answered for, ordered by JID. It also teaches the session's store each user's LID. An
	/// empty list returns an empty list without asking WhatsApp.
	/// </summary>
	/// <param name="users">Users, by phone-number JID or LID.</param>
	/// <param name="cancellationToken">Cancels the call.</param>
	/// <exception cref="WhatsmeowValidationException">A JID is not a user; nothing was asked.</exception>
	/// <exception cref="RateLimitedException">As for <see cref="IsOnWhatsAppAsync"/>.</exception>
	/// <exception cref="NotConnectedException">As for <see cref="IsOnWhatsAppAsync"/>.</exception>
	/// <exception cref="NotLoggedInException">As for <see cref="IsOnWhatsAppAsync"/>.</exception>
	/// <exception cref="SessionLoggedOutException">As for <see cref="IsOnWhatsAppAsync"/>.</exception>
	/// <exception cref="TemporarilyBannedException">As for <see cref="IsOnWhatsAppAsync"/>.</exception>
	/// <exception cref="SessionNotFoundException">The session was deleted.</exception>
	/// <exception cref="WhatsmeowException">As for <see cref="IsOnWhatsAppAsync"/>.</exception>
	/// <exception cref="SidecarUnavailableException">The sidecar cannot be reached.</exception>
	/// <exception cref="ObjectDisposedException">The sidecar was disposed.</exception>
	/// <exception cref="ArgumentNullException"><paramref name="users"/> is null.</exception>
	Task<ImmutableArray<UserInfo>> GetUserInfoAsync(IReadOnlyList<Jid> users, CancellationToken cancellationToken = default);

	/// <summary>
	/// Reads the profile picture of a user or a group: its URL and id, or a result saying there is none or that it is
	/// hidden from this account. The application downloads the picture from the URL itself.
	/// </summary>
	/// <param name="jid">A user or a group.</param>
	/// <param name="cancellationToken">Cancels the call.</param>
	/// <exception cref="WhatsmeowValidationException">The JID is neither a user nor a group.</exception>
	/// <exception cref="RateLimitedException">As for <see cref="IsOnWhatsAppAsync"/>.</exception>
	/// <exception cref="NotConnectedException">As for <see cref="IsOnWhatsAppAsync"/>.</exception>
	/// <exception cref="NotLoggedInException">As for <see cref="IsOnWhatsAppAsync"/>.</exception>
	/// <exception cref="SessionLoggedOutException">As for <see cref="IsOnWhatsAppAsync"/>.</exception>
	/// <exception cref="TemporarilyBannedException">As for <see cref="IsOnWhatsAppAsync"/>.</exception>
	/// <exception cref="SessionNotFoundException">The session was deleted.</exception>
	/// <exception cref="WhatsmeowException">As for <see cref="IsOnWhatsAppAsync"/>.</exception>
	/// <exception cref="SidecarUnavailableException">The sidecar cannot be reached.</exception>
	/// <exception cref="ObjectDisposedException">The sidecar was disposed.</exception>
	Task<ProfilePicture> GetProfilePictureAsync(Jid jid, CancellationToken cancellationToken = default);

	/// <summary>
	/// Gives each JID its other address form, the LID of a phone-number JID or the phone-number JID of a LID, when the
	/// session's store knows it: one result per JID, in the order given; an unknown one has a null
	/// <see cref="ResolvedJid.JidAlt"/>, and nothing throws for it. Some LIDs never resolve to a phone number. The store
	/// learns a pair from every message that carries both forms (<see cref="Events.MessageSource.SenderAlt"/>), from
	/// <see cref="IsOnWhatsAppAsync"/>, <see cref="GetUserInfoAsync"/> and the blocklist; this never asks WhatsApp, so it
	/// works while the session is disconnected. The store is shared by the sidecar's sessions.
	/// </summary>
	/// <param name="jids">Users, by phone-number JID or LID.</param>
	/// <param name="cancellationToken">Cancels the call.</param>
	/// <exception cref="WhatsmeowValidationException">A JID is not a user.</exception>
	/// <exception cref="NotLoggedInException">The session has never been paired.</exception>
	/// <exception cref="SessionLoggedOutException">The session was logged out.</exception>
	/// <exception cref="SessionNotFoundException">The session was deleted.</exception>
	/// <exception cref="SidecarUnavailableException">The sidecar cannot be reached.</exception>
	/// <exception cref="ObjectDisposedException">The sidecar was disposed.</exception>
	/// <exception cref="ArgumentNullException"><paramref name="jids"/> is null.</exception>
	Task<ImmutableArray<ResolvedJid>> ResolveJidsAsync(IReadOnlyList<Jid> jids, CancellationToken cancellationToken = default);

	/// <summary>
	/// Lists the session's contacts as WhatsApp synced them, ordered by JID: each one's saved name, push name and
	/// business name. One person can be listed under both address forms. It reads the session's store, so it works
	/// while the session is disconnected; this library keeps no contact book of its own.
	/// </summary>
	/// <exception cref="NotLoggedInException">The session has never been paired.</exception>
	/// <exception cref="SessionLoggedOutException">The session was logged out.</exception>
	/// <exception cref="SessionNotFoundException">The session was deleted.</exception>
	/// <exception cref="SidecarUnavailableException">The sidecar cannot be reached.</exception>
	/// <exception cref="ObjectDisposedException">The sidecar was disposed.</exception>
	Task<ImmutableArray<Contact>> GetContactsAsync(CancellationToken cancellationToken = default);

	/// <summary>Lists the users the account blocked, as WhatsApp has them now.</summary>
	/// <exception cref="RateLimitedException">As for <see cref="IsOnWhatsAppAsync"/>.</exception>
	/// <exception cref="NotConnectedException">As for <see cref="IsOnWhatsAppAsync"/>.</exception>
	/// <exception cref="NotLoggedInException">As for <see cref="IsOnWhatsAppAsync"/>.</exception>
	/// <exception cref="SessionLoggedOutException">As for <see cref="IsOnWhatsAppAsync"/>.</exception>
	/// <exception cref="TemporarilyBannedException">As for <see cref="IsOnWhatsAppAsync"/>.</exception>
	/// <exception cref="SessionNotFoundException">The session was deleted.</exception>
	/// <exception cref="WhatsmeowException">As for <see cref="IsOnWhatsAppAsync"/>.</exception>
	/// <exception cref="SidecarUnavailableException">The sidecar cannot be reached.</exception>
	/// <exception cref="ObjectDisposedException">The sidecar was disposed.</exception>
	Task<ImmutableArray<BlockedUser>> GetBlocklistAsync(CancellationToken cancellationToken = default);

	/// <summary>Blocks a user; the next <see cref="GetBlocklistAsync"/> lists them.</summary>
	/// <param name="user">The user, by phone-number JID or LID; a device in it is ignored.</param>
	/// <param name="cancellationToken">Cancels the call.</param>
	/// <exception cref="WhatsmeowValidationException">The JID is not a user.</exception>
	/// <exception cref="RecipientNotFoundException">The phone number is not on WhatsApp.</exception>
	/// <exception cref="OutcomeUnknownException">The request went out and no answer came back: the user may or may not be blocked.</exception>
	/// <exception cref="RateLimitedException">As for <see cref="IsOnWhatsAppAsync"/>.</exception>
	/// <exception cref="NotConnectedException">As for <see cref="IsOnWhatsAppAsync"/>.</exception>
	/// <exception cref="NotLoggedInException">As for <see cref="IsOnWhatsAppAsync"/>.</exception>
	/// <exception cref="SessionLoggedOutException">As for <see cref="IsOnWhatsAppAsync"/>.</exception>
	/// <exception cref="TemporarilyBannedException">As for <see cref="IsOnWhatsAppAsync"/>.</exception>
	/// <exception cref="SessionNotFoundException">The session was deleted.</exception>
	/// <exception cref="WhatsmeowException">WhatsApp refused, with <see cref="WhatsmeowErrorCode.WhatsAppError"/>.</exception>
	/// <exception cref="SidecarUnavailableException">The sidecar cannot be reached.</exception>
	/// <exception cref="ObjectDisposedException">The sidecar was disposed.</exception>
	Task BlockAsync(Jid user, CancellationToken cancellationToken = default);

	/// <summary>Unblocks a user; the next <see cref="GetBlocklistAsync"/> no longer lists them.</summary>
	/// <param name="user">The user, by phone-number JID or LID; a device in it is ignored.</param>
	/// <param name="cancellationToken">Cancels the call.</param>
	/// <exception cref="WhatsmeowValidationException">The JID is not a user.</exception>
	/// <exception cref="RecipientNotFoundException">The phone number is not on WhatsApp.</exception>
	/// <exception cref="OutcomeUnknownException">The request went out and no answer came back: the user may or may not be unblocked.</exception>
	/// <exception cref="RateLimitedException">As for <see cref="IsOnWhatsAppAsync"/>.</exception>
	/// <exception cref="NotConnectedException">As for <see cref="IsOnWhatsAppAsync"/>.</exception>
	/// <exception cref="NotLoggedInException">As for <see cref="IsOnWhatsAppAsync"/>.</exception>
	/// <exception cref="SessionLoggedOutException">As for <see cref="IsOnWhatsAppAsync"/>.</exception>
	/// <exception cref="TemporarilyBannedException">As for <see cref="IsOnWhatsAppAsync"/>.</exception>
	/// <exception cref="SessionNotFoundException">The session was deleted.</exception>
	/// <exception cref="WhatsmeowException">WhatsApp refused, with <see cref="WhatsmeowErrorCode.WhatsAppError"/>.</exception>
	/// <exception cref="SidecarUnavailableException">The sidecar cannot be reached.</exception>
	/// <exception cref="ObjectDisposedException">The sidecar was disposed.</exception>
	Task UnblockAsync(Jid user, CancellationToken cancellationToken = default);
}
