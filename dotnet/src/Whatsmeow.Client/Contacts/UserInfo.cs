namespace Whatsmeow.Contacts;

/// <summary>One user's profile, from <see cref="IWhatsAppClient.GetUserInfoAsync"/>. A null field is one WhatsApp did not send.</summary>
public sealed record UserInfo
{
	/// <summary>The user, as WhatsApp answered.</summary>
	public required Jid Jid { get; init; }

	/// <summary>The about text.</summary>
	public required string? About { get; init; }

	/// <summary>The verified business name.</summary>
	public required string? VerifiedBusinessName { get; init; }

	/// <summary>The current profile picture's id, the one <see cref="ProfilePicture.Set.Id"/> carries.</summary>
	public required string? PictureId { get; init; }
}
