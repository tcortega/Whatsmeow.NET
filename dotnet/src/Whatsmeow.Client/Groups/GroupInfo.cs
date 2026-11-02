using System.Collections.Immutable;

namespace Whatsmeow.Groups;

/// <summary>
/// One group as WhatsApp answered, from <see cref="IWhatsAppClient.GetGroupsAsync"/> and
/// <see cref="IWhatsAppClient.GetGroupInfoAsync"/>. Its participants compare by content.
/// </summary>
public sealed record GroupInfo
{
	/// <summary>The group's JID, <c>digits@g.us</c>.</summary>
	public required Jid Jid { get; init; }

	/// <summary>The name (subject).</summary>
	public required string Name { get; init; }

	/// <summary>The topic (description); null when there is none.</summary>
	public required string? Topic { get; init; }

	/// <summary>The user who created the group; null when WhatsApp did not say.</summary>
	public required Jid? Owner { get; init; }

	/// <summary>The owner's phone number when <see cref="Owner"/> is a LID and WhatsApp sent it.</summary>
	public required Jid? OwnerAlt { get; init; }

	/// <summary>When the group was created; null when WhatsApp did not say.</summary>
	public required DateTimeOffset? CreatedAt { get; init; }

	/// <summary>Only admins can send messages.</summary>
	public required bool IsAnnounce { get; init; }

	/// <summary>Only admins can change the group's info.</summary>
	public required bool IsLocked { get; init; }

	/// <summary>The participants, in WhatsApp's order.</summary>
	public required ImmutableArray<GroupParticipant> Participants { get; init; }

	/// <summary>Who can add participants; null when WhatsApp did not say.</summary>
	public GroupMemberAddMode? MemberAddMode { get; init; }

	/// <summary>
	/// Compares every field, the participants by content: a group read twice equals itself
	/// (<see cref="ImmutableArray{T}"/> alone compares the arrays it wraps).
	/// </summary>
	public bool Equals(GroupInfo? other) =>
		other is not null
		&& Jid.Equals(other.Jid)
		&& string.Equals(Name, other.Name, StringComparison.Ordinal)
		&& string.Equals(Topic, other.Topic, StringComparison.Ordinal)
		&& Nullable.Equals(Owner, other.Owner)
		&& Nullable.Equals(OwnerAlt, other.OwnerAlt)
		&& Nullable.Equals(CreatedAt, other.CreatedAt)
		&& IsAnnounce == other.IsAnnounce
		&& IsLocked == other.IsLocked
		&& Participants.AsSpan().SequenceEqual(other.Participants.AsSpan())
		&& MemberAddMode == other.MemberAddMode;

	/// <inheritdoc/>
	public override int GetHashCode()
	{
		var hash = new HashCode();
		hash.Add(Jid);
		hash.Add(Name, StringComparer.Ordinal);
		foreach (var participant in Participants.AsSpan())
			hash.Add(participant);
		return hash.ToHashCode();
	}
}
