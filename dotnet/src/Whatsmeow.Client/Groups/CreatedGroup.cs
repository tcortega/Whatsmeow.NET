using System.Collections.Immutable;

namespace Whatsmeow.Groups;

/// <summary>
/// A group <see cref="IWhatsAppClient.CreateGroupAsync"/> created, and what WhatsApp answered for each participant. Its
/// results compare by content.
/// </summary>
public sealed record CreatedGroup
{
	/// <summary>The group, with the participants WhatsApp added.</summary>
	public required GroupInfo Group { get; init; }

	/// <summary>
	/// One result per participant asked for that WhatsApp answered for, in its order. WhatsApp may omit a participant it
	/// refused [unknown].
	/// </summary>
	public required ImmutableArray<ParticipantResult> Participants { get; init; }

	/// <summary>Compares the group and the results, by content (<see cref="ImmutableArray{T}"/> alone compares the arrays it wraps).</summary>
	public bool Equals(CreatedGroup? other) =>
		other is not null && Group.Equals(other.Group) && Participants.AsSpan().SequenceEqual(other.Participants.AsSpan());

	/// <inheritdoc/>
	public override int GetHashCode()
	{
		var hash = new HashCode();
		hash.Add(Group);
		foreach (var participant in Participants.AsSpan())
			hash.Add(participant);
		return hash.ToHashCode();
	}
}
