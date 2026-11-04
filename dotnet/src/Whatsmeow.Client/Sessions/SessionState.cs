namespace Whatsmeow.Sessions;

/// <summary>
/// A session's state (ARCHITECTURE §7.4). Each case is a nested sealed record; <see cref="Unknown"/> is a state this
/// client version does not know.
/// </summary>
/// <remarks>
/// The library defines every case; a type derived from it elsewhere is unsupported, and the cases may become a closed
/// set (ADR 0055).
/// </remarks>
[SuppressMessage(
	"Naming",
	"CA1724:Type names should not match namespaces",
	Justification = "The match is System.Web.SessionState, a .NET Framework namespace; this library targets net10.0 only, where it does not exist, and SessionState is the contract's name (session.proto)."
)]
public abstract record SessionState
{
	private protected SessionState()
	{
	}

	/// <summary>Never paired.</summary>
	public sealed record Created : SessionState;

	/// <summary>Being paired.</summary>
	public sealed record Pairing : SessionState;

	/// <summary>Connect was called; the connection is opening, or WhatsApp has not accepted the login yet.</summary>
	public sealed record Connecting : SessionState;

	/// <summary>Logged in to WhatsApp; the messages queued while offline may still be arriving.</summary>
	public sealed record Connected : SessionState;

	/// <summary>Connected, and the messages queued while offline have arrived.</summary>
	public sealed record CaughtUp : SessionState;

	/// <summary>
	/// The connection dropped, or the sidecar reset it so that WhatsApp delivers again a message it could not record;
	/// it is being opened again.
	/// </summary>
	public sealed record Reconnecting : SessionState;

	/// <summary>Paired, not connected: it was disconnected, the sidecar restarted, or pairing failed after the link.</summary>
	public sealed record Disconnected : SessionState;

	/// <summary>The device was unlinked and its keys deleted; the session needs to be paired again.</summary>
	public sealed record LoggedOut : SessionState
	{
		/// <summary>WhatsApp's logout code (401, 403 or 406), or 0 when the application logged out.</summary>
		public required int Code { get; init; }
	}

	/// <summary>Another client connected with the same keys, and WhatsApp closed this connection.</summary>
	public sealed record Replaced : SessionState;

	/// <summary>The account is temporarily banned.</summary>
	public sealed record TemporarilyBanned : SessionState
	{
		/// <summary>WhatsApp's ban code.</summary>
		public required int Code { get; init; }

		/// <summary>When the ban ends; null when WhatsApp sent no end.</summary>
		public required DateTimeOffset? Until { get; init; }
	}

	/// <summary>WhatsApp rejected this client version.</summary>
	public sealed record Outdated : SessionState;

	/// <summary>The connection ended for another reason and is not opened again on its own; connecting again retries.</summary>
	public sealed record Error : SessionState
	{
		/// <summary>WhatsApp's failure code, or 0 when there is none.</summary>
		public required int Code { get; init; }

		/// <summary>A description for logs; never match on it.</summary>
		public required string Message { get; init; }
	}

	/// <summary>A state this client version does not know.</summary>
	public sealed record Unknown : SessionState;
}
