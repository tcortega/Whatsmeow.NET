namespace Whatsmeow.History;

/// <summary>
/// How much history a session asks its phone for when it is paired. These are process-wide: the sidecar sets them once
/// when it starts, and WhatsApp reads them only at pairing, so they apply to every session paired while the sidecar
/// runs with them, and never to one paired before. A limit left null keeps whatsmeow's default. What WhatsApp makes of
/// each is WhatsApp's, and not documented.
/// </summary>
public sealed class HistorySyncOptions
{
	/// <summary>Ask the phone for the full history, not only the recent.</summary>
	public bool RequestFullSync { get; set; }

	/// <summary>The full history's age limit in days; null for whatsmeow's default, none.</summary>
	public int? FullSyncDaysLimit { get; set; }

	/// <summary>The full history's size limit in megabytes; null for whatsmeow's default, none.</summary>
	public int? FullSyncSizeLimitMegabytes { get; set; }

	/// <summary>The device's storage quota for history in megabytes; null for whatsmeow's default, 10240.</summary>
	public int? StorageQuotaMegabytes { get; set; }

	/// <summary>A member-wise copy, so a property added later is copied too.</summary>
	internal HistorySyncOptions Copy() => (HistorySyncOptions)MemberwiseClone();
}
