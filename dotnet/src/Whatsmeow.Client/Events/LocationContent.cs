namespace Whatsmeow.Events;

/// <summary>A location: a pinned place, not a live one, which arrives as <see cref="UnknownContent"/>.</summary>
public sealed record LocationContent : MessageContent
{
	/// <summary>The latitude in degrees.</summary>
	public required double Latitude { get; init; }

	/// <summary>The longitude in degrees.</summary>
	public required double Longitude { get; init; }

	/// <summary>The place's name, or null when the message names none.</summary>
	public string? Name { get; init; }

	/// <summary>The place's address, or null when the message gives none.</summary>
	public string? Address { get; init; }
}
