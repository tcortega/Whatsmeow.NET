using System.Collections.Immutable;
using Whatsmeow.Events;

namespace Whatsmeow.Groups;

/// <summary>
/// A change to a group the session is in, by anyone, the session included. whatsmeow acknowledges it to WhatsApp only
/// after the sidecar recorded it, so it is at least once (ARCHITECTURE §1). It has no id: it can come again with the
/// same content, and equal to the first, so applying the changes again is harmless. When WhatsApp sends again one the
/// sidecar did not record is [unknown]: it can come after later changes to the group [inferred]. Order a group's
/// changes by <see cref="Timestamp"/>, not by their arrival.
/// </summary>
public sealed record GroupInfoChanged : WhatsAppEvent
{
	/// <summary>The group.</summary>
	public required Jid Group { get; init; }

	/// <summary>Who made the change; null when WhatsApp did not say.</summary>
	public required Jid? Sender { get; init; }

	/// <summary>The sender's phone number when <see cref="Sender"/> is a LID and WhatsApp sent it.</summary>
	public required Jid? SenderAlt { get; init; }

	/// <summary>When WhatsApp says it happened.</summary>
	public required DateTimeOffset Timestamp { get; init; }

	/// <summary>What changed, never empty, in the order of <see cref="GroupChange"/>'s cases.</summary>
	public required ImmutableArray<GroupChange> Changes { get; init; }

	/// <summary>
	/// Compares every field, the changes by content: a change WhatsApp sends again equals the first
	/// (<see cref="ImmutableArray{T}"/> alone compares the arrays it wraps).
	/// </summary>
	public bool Equals(GroupInfoChanged? other) =>
		base.Equals(other)
		&& Group.Equals(other.Group)
		&& Nullable.Equals(Sender, other.Sender)
		&& Nullable.Equals(SenderAlt, other.SenderAlt)
		&& Timestamp.Equals(other.Timestamp)
		&& Changes.AsSpan().SequenceEqual(other.Changes.AsSpan());

	/// <inheritdoc/>
	public override int GetHashCode()
	{
		var hash = new HashCode();
		hash.Add(base.GetHashCode());
		hash.Add(Group);
		hash.Add(Timestamp);
		foreach (var change in Changes.AsSpan())
			hash.Add(change);
		return hash.ToHashCode();
	}
}
