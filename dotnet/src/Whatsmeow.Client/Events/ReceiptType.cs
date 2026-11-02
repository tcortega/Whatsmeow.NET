namespace Whatsmeow.Events;

/// <summary>The kind of a <see cref="ReceiptReceived"/>.</summary>
public enum ReceiptType
{
	/// <summary>A kind this client does not know.</summary>
	Unknown = 0,

	/// <summary>Delivered to the recipient's device.</summary>
	Delivered,

	/// <summary>Delivered to another of the account's own devices.</summary>
	Sender,

	/// <summary>The recipient could not decrypt it; whatsmeow resends it on its own.</summary>
	Retry,

	/// <summary>Read by the recipient.</summary>
	Read,

	/// <summary>Read on another of the account's devices, with read receipts turned off.</summary>
	ReadSelf,

	/// <summary>A view-once message was opened.</summary>
	Played,

	/// <summary>A view-once message was opened on another of the account's devices.</summary>
	PlayedSelf,

	/// <summary>WhatsApp's server could not deliver it.</summary>
	ServerError,

	/// <summary>Delivered to a device that is not in use right now.</summary>
	Inactive,

	/// <summary>A message between the account's own devices.</summary>
	PeerMessage,

	/// <summary>A history sync was received.</summary>
	HistorySync,
}
