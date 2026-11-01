namespace Whatsmeow.Pairing;

/// <summary>
/// One step of pairing a session (ARCHITECTURE §9): a code to show, then <see cref="Paired"/>, the last. Each case is a
/// nested sealed record.
/// </summary>
public abstract record PairingUpdate
{
	private protected PairingUpdate()
	{
	}

	/// <summary>A QR code to show; it replaces the previous one. WhatsApp rotates it until the phone scans one.</summary>
	public sealed record QrCode : PairingUpdate
	{
		/// <summary>The payload to render as a QR code.</summary>
		public required string Code { get; init; }
	}

	/// <summary>The code to enter on the phone, sent once. Pairing waits for it until it times out.</summary>
	public sealed record PairingCode : PairingUpdate
	{
		/// <summary>The code, formatted for display.</summary>
		public required string Code { get; init; }
	}

	/// <summary>The phone linked the device and the session is usable: it is <see cref="Sessions.SessionState.Connected"/>.</summary>
	public sealed record Paired : PairingUpdate;
}
