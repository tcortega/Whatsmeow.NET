namespace Whatsmeow.Events;

/// <summary>Why WhatsApp sent this device no ciphertext for a message.</summary>
public enum UnavailableType
{
	/// <summary>WhatsApp named no reason, or one this version does not know. A copy that decrypts may still come.</summary>
	Unknown,

	/// <summary>
	/// A view-once message. whatsmeow asks the phone for it, but whether the phone answers with a copy that decrypts is
	/// not known yet (#36 checks it live).
	/// </summary>
	ViewOnce,
}
