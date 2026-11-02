namespace Whatsmeow.Groups;

/// <summary>
/// One change in a <see cref="GroupInfoChanged"/>. Each case is a nested sealed record. A participant is addressed as
/// in <see cref="GroupInfo.Participants"/>; the session's store learned their other form with the change
/// (<see cref="IWhatsAppClient.ResolveJidsAsync"/>).
/// </summary>
public abstract record GroupChange
{
	private protected GroupChange()
	{
	}

	/// <summary>The group was renamed.</summary>
	public sealed record Renamed : GroupChange
	{
		/// <summary>The new name.</summary>
		public required string Name { get; init; }
	}

	/// <summary>The topic changed.</summary>
	public sealed record TopicChanged : GroupChange
	{
		/// <summary>The new topic; null when it was removed.</summary>
		public required string? Topic { get; init; }
	}

	/// <summary>Who can send messages changed.</summary>
	public sealed record AnnounceChanged : GroupChange
	{
		/// <summary>True when only admins can.</summary>
		public required bool IsAnnounce { get; init; }
	}

	/// <summary>Who can change the group's info changed.</summary>
	public sealed record LockedChanged : GroupChange
	{
		/// <summary>True when only admins can.</summary>
		public required bool IsLocked { get; init; }
	}

	/// <summary>Who can add participants changed.</summary>
	public sealed record MemberAddModeChanged : GroupChange
	{
		/// <summary>The new mode.</summary>
		public required GroupMemberAddMode Mode { get; init; }
	}

	/// <summary>
	/// The disappearing messages timer changed. The session's later messages do not carry it: the library sets no
	/// timer on a send, so a message that should disappear sets its own (<see cref="IWhatsAppClient.SendRawAsync"/>,
	/// <c>ContextInfo.Expiration</c>).
	/// </summary>
	public sealed record DisappearingTimerChanged : GroupChange
	{
		/// <summary>How long new messages last; <see cref="TimeSpan.Zero"/> when disappearing messages were turned off.</summary>
		public required TimeSpan Timer { get; init; }
	}

	/// <summary>A user joined or was added.</summary>
	public sealed record ParticipantJoined : GroupChange
	{
		/// <summary>The user.</summary>
		public required Jid Jid { get; init; }
	}

	/// <summary>A user left or was removed.</summary>
	public sealed record ParticipantLeft : GroupChange
	{
		/// <summary>The user.</summary>
		public required Jid Jid { get; init; }
	}

	/// <summary>A user became an admin.</summary>
	public sealed record ParticipantPromoted : GroupChange
	{
		/// <summary>The user.</summary>
		public required Jid Jid { get; init; }
	}

	/// <summary>A user stopped being an admin.</summary>
	public sealed record ParticipantDemoted : GroupChange
	{
		/// <summary>The user.</summary>
		public required Jid Jid { get; init; }
	}
}
