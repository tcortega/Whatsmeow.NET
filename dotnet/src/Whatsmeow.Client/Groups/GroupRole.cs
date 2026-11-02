namespace Whatsmeow.Groups;

/// <summary>A participant's role in a group.</summary>
public enum GroupRole
{
	/// <summary>A member.</summary>
	Member,

	/// <summary>An admin.</summary>
	Admin,

	/// <summary>The super admin: the group's creator, unless WhatsApp moved it.</summary>
	SuperAdmin,
}
