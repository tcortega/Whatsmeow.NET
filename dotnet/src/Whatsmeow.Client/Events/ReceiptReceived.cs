using System.Collections.Immutable;

namespace Whatsmeow.Events;

/// <summary>
/// A receipt for messages of one chat: delivered, read, played and so on. It is recorded before WhatsApp is told it
/// arrived (ARCHITECTURE §13 invariant 2), and, like every event, can come again after a crash; a receipt for the same
/// messages, of the same type, at the same <see cref="Timestamp"/>, is the same receipt, and equal to it.
/// </summary>
public sealed record ReceiptReceived : WhatsAppEvent
{
	/// <summary>Who sent the receipt, and in which chat.</summary>
	public required MessageSource Source { get; init; }

	/// <summary>The messages it covers; at least one.</summary>
	public required ImmutableArray<MessageId> MessageIds { get; init; }

	/// <summary>The receipt's WhatsApp timestamp.</summary>
	public required DateTimeOffset Timestamp { get; init; }

	/// <summary>What happened to the messages.</summary>
	public required ReceiptType Type { get; init; }

	/// <summary>
	/// Compares every field, the message ids by content: a receipt that comes again equals the first
	/// (<see cref="ImmutableArray{T}"/> alone compares the arrays it wraps).
	/// </summary>
	public bool Equals(ReceiptReceived? other) =>
		base.Equals(other)
		&& Source.Equals(other.Source)
		&& MessageIds.AsSpan().SequenceEqual(other.MessageIds.AsSpan())
		&& Timestamp.Equals(other.Timestamp)
		&& Type == other.Type;

	/// <inheritdoc/>
	public override int GetHashCode()
	{
		var hash = new HashCode();
		hash.Add(base.GetHashCode());
		hash.Add(Source);
		foreach (var id in MessageIds.AsSpan())
			hash.Add(id);
		hash.Add(Timestamp);
		hash.Add(Type);
		return hash.ToHashCode();
	}
}
