namespace Whatsmeow;

/// <summary>
/// Why a sidecar call failed: the reason the sidecar sent (the <c>whatsmeow.v1</c> contract's error reasons). Match
/// on this or on the exception type, never on the message.
/// </summary>
public enum WhatsmeowErrorCode
{
	/// <summary>The sidecar sent no reason, the failure happened in this process, or this client does not know the reason.</summary>
	Unknown = 0,

	/// <summary>An unexpected sidecar failure.</summary>
	Internal,

	/// <summary>The request was malformed.</summary>
	InvalidArgument,

	/// <summary>The call did not carry the sidecar's per-launch token.</summary>
	Unauthenticated,

	/// <summary>The sidecar is shutting down.</summary>
	SidecarShuttingDown,

	/// <summary>No session has the id, or it was deleted.</summary>
	SessionNotFound,

	/// <summary>The session has never been paired.</summary>
	NotLoggedIn,

	/// <summary>The session was logged out; it has no device until it is paired again.</summary>
	LoggedOut,

	/// <summary>Pairing was asked of a paired session.</summary>
	AlreadyPaired,

	/// <summary>The session is being paired.</summary>
	PairingInProgress,

	/// <summary>Pairing ended because no code was used in time.</summary>
	PairingTimedOut,

	/// <summary>Pairing failed for another reason.</summary>
	PairingFailed,

	/// <summary>The session is paired but not logged in to WhatsApp right now; nothing was done.</summary>
	NotConnected,

	/// <summary>Opening the WhatsApp connection failed with an error that is not retried.</summary>
	ConnectionFailed,

	/// <summary>The account is temporarily banned.</summary>
	TemporarilyBanned,

	/// <summary>A newer event stream for the same session and consumer replaced this one.</summary>
	ConsumerReplaced,

	/// <summary>The recipient is not on WhatsApp; nothing was sent.</summary>
	RecipientNotOnWhatsApp,

	/// <summary>WhatsApp rate limited the request; nothing was done.</summary>
	RateLimited,

	/// <summary>The request may have reached WhatsApp and no answer came back: it may or may not have taken effect.</summary>
	OutcomeUnknown,

	/// <summary>WhatsApp answered with an error no other code covers; nothing was done.</summary>
	WhatsAppError,

	/// <summary>WhatsApp did not answer a read in time; nothing was done, and the call can be repeated.</summary>
	WhatsAppTimedOut,

	/// <summary>The account has no push name yet, which sending presence needs; nothing was sent.</summary>
	NoPushName,
}
