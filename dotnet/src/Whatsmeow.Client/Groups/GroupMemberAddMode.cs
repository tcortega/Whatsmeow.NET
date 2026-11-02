namespace Whatsmeow.Groups;

/// <summary>Who can add participants to a group.</summary>
public enum GroupMemberAddMode
{
	/// <summary>Only admins (WhatsApp's <c>admin_add</c>).</summary>
	AdminsOnly,

	/// <summary>Every member (WhatsApp's <c>all_member_add</c>).</summary>
	AllMembers,
}
