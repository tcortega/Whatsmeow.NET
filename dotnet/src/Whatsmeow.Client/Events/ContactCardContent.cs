namespace Whatsmeow.Events;

/// <summary>One contact card; a message with several arrives as <see cref="UnknownContent"/>.</summary>
public sealed record ContactCardContent : MessageContent
{
	/// <summary>The name the sender's app shows for the contact.</summary>
	public required string DisplayName { get; init; }

	/// <summary>The contact as a vCard, as the sender's app wrote it.</summary>
	public required string VCard { get; init; }
}
