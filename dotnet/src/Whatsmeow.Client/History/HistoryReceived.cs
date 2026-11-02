using System.Collections.Immutable;
using Whatsmeow.Events;

namespace Whatsmeow.History;

/// <summary>
/// A batch of the account's history: old messages, which a bot must not answer as new ones, so they never arrive as
/// <see cref="MessageReceived"/>. WhatsApp sends history in blobs after pairing, and on
/// <see cref="IWhatsAppClient.RequestHistoryAsync"/>; the sidecar downloads them once the session caught up, always
/// after live traffic, and delivers each blob's messages in one or more batches, in order, then a
/// <see cref="HistorySyncProgressed"/>. History events run in one lane of their own, one at a time, in order, never in
/// a chat's lane, so a live message never waits for one. Like every event a batch can come again after a crash, with
/// the same messages: a batch with the same messages is the same batch, and equal to it.
/// </summary>
public sealed record HistoryReceived : WhatsAppEvent
{
	/// <summary>Which history the blob carries.</summary>
	public required HistorySyncType SyncType { get; init; }

	/// <summary>The messages, in the blob's order; at least one.</summary>
	public required ImmutableArray<HistoryMessage> Messages { get; init; }

	/// <summary>
	/// Compares every field, the messages by content: a batch that comes again equals the first
	/// (<see cref="ImmutableArray{T}"/> alone compares the arrays it wraps).
	/// </summary>
	public bool Equals(HistoryReceived? other) => base.Equals(other) && SyncType == other.SyncType && Messages.AsSpan().SequenceEqual(other.Messages.AsSpan());

	/// <inheritdoc/>
	public override int GetHashCode()
	{
		var hash = new HashCode();
		hash.Add(base.GetHashCode());
		hash.Add(SyncType);
		foreach (var message in Messages.AsSpan())
			hash.Add(message);
		return hash.ToHashCode();
	}
}
