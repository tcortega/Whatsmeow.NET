using System.Collections.Immutable;

namespace Whatsmeow.Messaging;

/// <summary>
/// A poll: a question, its options, and how many of them a voter may select. The library keeps no polls, so an
/// application that tallies votes keeps the options it sent or received. Its options compare by content.
/// </summary>
public sealed record Poll
{
	/// <summary>The question; not empty.</summary>
	public required string Question { get; init; }

	/// <summary>
	/// The options, in the order shown; at least two, none empty, none repeated. A vote names an option by
	/// <see cref="Events.PollVoteContent.HashOption"/> of its name.
	/// </summary>
	public required ImmutableArray<string> Options { get; init; }

	/// <summary>
	/// How many options a voter may select, from 1 to the number of options. A received poll that sets no limit has the
	/// number of its options.
	/// </summary>
	public required int MaxSelectable { get; init; }

	/// <summary>Compares every field, the options by content.</summary>
	public bool Equals(Poll? other) =>
		other is not null
		&& string.Equals(Question, other.Question, StringComparison.Ordinal)
		&& Options.AsSpan().SequenceEqual(other.Options.AsSpan())
		&& MaxSelectable == other.MaxSelectable;

	/// <inheritdoc/>
	public override int GetHashCode()
	{
		var hash = new HashCode();
		hash.Add(Question, StringComparer.Ordinal);
		foreach (var option in Options.AsSpan())
			hash.Add(option, StringComparer.Ordinal);
		hash.Add(MaxSelectable);
		return hash.ToHashCode();
	}
}
