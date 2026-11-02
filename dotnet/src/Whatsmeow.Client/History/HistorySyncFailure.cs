namespace Whatsmeow.History;

/// <summary>Why a history sync blob was given up on.</summary>
public enum HistorySyncFailure
{
	/// <summary>A reason this client version does not know.</summary>
	Unknown,

	/// <summary>WhatsApp's media server no longer has the blob.</summary>
	Expired,

	/// <summary>The blob could not be read: it failed its checks, or did not decompress or parse.</summary>
	Invalid,

	/// <summary>Its download kept failing, three times a minute apart, while the session was online.</summary>
	Unavailable,
}
