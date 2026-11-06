namespace Whatsmeow.Contacts;

/// <summary>What a <see cref="ContactUpdated"/> changed. Each case is a nested sealed record.</summary>
/// <remarks>
/// The library defines every case; a type derived from it elsewhere is unsupported, and the cases may become a closed
/// set (ADR 0055).
/// </remarks>
public abstract record ContactChange
{
	private protected ContactChange()
	{
	}

	/// <summary>The user changed their push name.</summary>
	public sealed record PushName : ContactChange
	{
		/// <summary>The new push name.</summary>
		public required string Name { get; init; }
	}

	/// <summary>The user's verified business name changed.</summary>
	public sealed record BusinessName : ContactChange
	{
		/// <summary>The new business name.</summary>
		public required string Name { get; init; }
	}

	/// <summary>The profile picture changed.</summary>
	public sealed record Picture : ContactChange
	{
		/// <summary>The new picture's id; null when the picture was removed.</summary>
		public required string? Id { get; init; }
	}

	/// <summary>The user changed their about text.</summary>
	public sealed record About : ContactChange
	{
		/// <summary>The new about text.</summary>
		public required string Text { get; init; }
	}
}
