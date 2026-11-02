namespace Whatsmeow.Contacts;

/// <summary>
/// A user's or a group's profile picture, from <see cref="IWhatsAppClient.GetProfilePictureAsync"/>: <see cref="Set"/>,
/// <see cref="None"/> or <see cref="Hidden"/>. Each case is a nested sealed record.
/// </summary>
public abstract record ProfilePicture
{
	private protected ProfilePicture()
	{
	}

	/// <summary>The picture is set. The application downloads it from <see cref="Url"/> itself.</summary>
	public sealed record Set : ProfilePicture
	{
		/// <summary>The full-size image's URL.</summary>
		public required Uri Url { get; init; }

		/// <summary>The picture's id.</summary>
		public required string Id { get; init; }
	}

	/// <summary>There is no picture.</summary>
	public sealed record None : ProfilePicture;

	/// <summary>The user hid the picture from this account.</summary>
	public sealed record Hidden : ProfilePicture;
}
