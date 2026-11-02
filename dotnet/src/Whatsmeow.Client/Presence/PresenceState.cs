namespace Whatsmeow.Presence;

/// <summary>Whether a user is online: the account's own presence, or a contact's in <see cref="Events.PresenceChanged"/>.</summary>
public enum PresenceState
{
	/// <summary>Online.</summary>
	Available,

	/// <summary>Offline.</summary>
	Unavailable,
}
