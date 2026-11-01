namespace Whatsmeow.Presence;

/// <summary>What a user is doing in a chat: the account itself, or someone else in <see cref="Events.ChatPresenceChanged"/>.</summary>
public enum ChatPresenceState
{
	/// <summary>Typing a text message.</summary>
	Typing,

	/// <summary>Recording an audio message.</summary>
	Recording,

	/// <summary>Stopped typing or recording.</summary>
	Paused,
}
