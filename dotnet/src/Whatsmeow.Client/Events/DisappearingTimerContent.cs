namespace Whatsmeow.Events;

/// <summary>
/// A direct chat's disappearing messages timer changed, by the other side or on another of the account's devices
/// (<see cref="MessageReceived.Source"/> says which). A group's timer changes with a
/// <see cref="Groups.GroupChange.DisappearingTimerChanged"/> instead.
/// </summary>
public sealed record DisappearingTimerContent : MessageContent
{
	/// <summary>How long the chat's new messages last; <see cref="TimeSpan.Zero"/> when disappearing messages were turned off.</summary>
	public required TimeSpan Timer { get; init; }
}
