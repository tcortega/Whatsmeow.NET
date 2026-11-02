using Whatsmeow.Messaging;

namespace Whatsmeow.Events;

/// <summary>A poll. Its votes arrive as <see cref="PollVoteContent"/> messages, and the library tallies none.</summary>
public sealed record PollContent : MessageContent
{
	/// <summary>The question, the options and how many a voter may select.</summary>
	public required Poll Poll { get; init; }
}
