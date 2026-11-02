using Whatsmeow.Events;

namespace Whatsmeow.History;

/// <summary>
/// One history sync blob done: after its <see cref="HistoryReceived"/> batches, or when it was given up on, with
/// <see cref="Failure"/> saying why. It runs in the history lane, after the blob's batches. The sidecar deletes the blob
/// from WhatsApp's server only once this and the batches are recorded.
/// </summary>
public sealed record HistorySyncProgressed : WhatsAppEvent
{
	/// <summary>Which history the blob carried.</summary>
	public required HistorySyncType SyncType { get; init; }

	/// <summary>The blob's place among the sync's blobs, as WhatsApp numbers them.</summary>
	public required long ChunkOrder { get; init; }

	/// <summary>WhatsApp's progress value for the sync, as it sent it; what it measures is WhatsApp's.</summary>
	public required long Progress { get; init; }

	/// <summary>How many messages the blob's batches delivered.</summary>
	public required long MessageCount { get; init; }

	/// <summary>How many of its messages were not delivered: one that did not parse, or one too large to deliver.</summary>
	public required long SkippedCount { get; init; }

	/// <summary>Why the blob was given up on; null when its messages were delivered.</summary>
	public required HistorySyncFailure? Failure { get; init; }
}
