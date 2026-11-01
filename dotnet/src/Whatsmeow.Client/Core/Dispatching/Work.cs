namespace Whatsmeow.Core.Dispatching;

/// <summary>One received event for the <see cref="Dispatcher"/>: its seq, its lane, and the run of its handlers.</summary>
internal sealed class Work
{
	/// <summary>The event's seq; seqs arrive increasing.</summary>
	public required ulong Seq { get; init; }

	/// <summary>The lane: events of one lane run one at a time in seq order, different lanes concurrently.</summary>
	public required string Lane { get; init; }

	/// <summary>Runs every handler of the event; the token is cancelled when the dispatcher stops.</summary>
	public required Func<CancellationToken, ValueTask> RunAsync { get; init; }
}
