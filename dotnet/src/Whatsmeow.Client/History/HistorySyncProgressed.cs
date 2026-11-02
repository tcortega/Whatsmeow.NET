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

	/// <summary>
	/// How many of the blob's messages the sidecar delivered in its <see cref="HistoryReceived"/> batches. A message the
	/// client then skipped as unreadable still counts; the votes that follow a poll do not.
	/// </summary>
	public required long MessageCount { get; init; }

	/// <summary>
	/// How many of its messages the sidecar did not deliver: one that did not parse, one too large to deliver, and every
	/// status update, which history never delivers.
	/// </summary>
	public required long SkippedCount { get; init; }

	/// <summary>Why the blob was given up on; null when its messages were delivered.</summary>
	public required HistorySyncFailure? Failure { get; init; }
}
