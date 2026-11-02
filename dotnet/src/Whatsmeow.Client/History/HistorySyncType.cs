namespace Whatsmeow.History;

/// <summary>Which history a history sync blob carries, as WhatsApp names it.</summary>
public enum HistorySyncType
{
	/// <summary>A type this client version does not know.</summary>
	Unknown,

	/// <summary>The first, recent history after pairing.</summary>
	InitialBootstrap,

	/// <summary>Status updates after pairing.</summary>
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
}
