namespace Whatsmeow.History;

/// <summary>Which history a history sync blob carries, as WhatsApp names it.</summary>
public enum HistorySyncType
{
	/// <summary>A type this client version does not know.</summary>
	Unknown,

	/// <summary>The first, recent history after pairing.</summary>
	InitialBootstrap,

	/// <summary>Status updates after pairing. Statuses are never delivered: they count in <see cref="HistorySyncProgressed.SkippedCount"/>.</summary>
	InitialStatus,

	/// <summary>Older history, as much as <see cref="WhatsmeowSidecarOptions.History"/> asks for.</summary>
	Full,

	/// <summary>Recent history.</summary>
	Recent,

	/// <summary>Contacts' push names; no messages.</summary>
	PushName,

	/// <summary>Other account data; no messages.</summary>
	NonBlockingData,

	/// <summary>The answer to <see cref="IWhatsAppClient.RequestHistoryAsync"/>.</summary>
	OnDemand,

	/// <summary>The phone says it sends no history; no messages. When it sends this is WhatsApp's, and not documented.</summary>
	NoHistory,

	/// <summary>The phone says how much of the account's messages this device may read; no messages.</summary>
	MessageAccessStatus,
}
