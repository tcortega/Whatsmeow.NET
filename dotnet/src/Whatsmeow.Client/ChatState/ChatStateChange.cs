namespace Whatsmeow.ChatState;

/// <summary>What a <see cref="ChatStateChanged"/> changed. Each case is a nested sealed record.</summary>
/// <remarks>
/// The library defines every case; a type derived from it elsewhere is unsupported, and the cases may become a closed
/// set (ADR 0055).
/// </remarks>
public abstract record ChatStateChange
{
	private protected ChatStateChange()
	{
	}

	/// <summary>The chat was muted or unmuted.</summary>
	public sealed record Mute : ChatStateChange
	{
		/// <summary>True when the chat is muted.</summary>
		public required bool IsMuted { get; init; }

		/// <summary>When the mute ends, to the millisecond; null while unmuted, or when muted forever.</summary>
		public required DateTimeOffset? Until { get; init; }
	}

	/// <summary>The chat was pinned or unpinned.</summary>
	public sealed record Pin : ChatStateChange
	{
		/// <summary>True when the chat is pinned.</summary>
		public required bool IsPinned { get; init; }
	}

	/// <summary>The chat was archived or unarchived.</summary>
	public sealed record Archive : ChatStateChange
	{
		/// <summary>True when the chat is archived.</summary>
		public required bool IsArchived { get; init; }
	}

	/// <summary>The chat was marked read or unread.</summary>
	public sealed record Read : ChatStateChange
	{
		/// <summary>True when the chat is read; false when it was marked unread.</summary>
		public required bool IsRead { get; init; }
	}

	/// <summary>A message of the chat was starred or unstarred.</summary>
	public sealed record Star : ChatStateChange
	{
		/// <summary>The message.</summary>
		public required MessageId MessageId { get; init; }

		/// <summary>In a group, the user who sent the message, without a device; null for the account's own messages and in a direct chat.</summary>
		public required Jid? Sender { get; init; }

		/// <summary>True when the account sent the message.</summary>
		public required bool IsFromMe { get; init; }

		/// <summary>True when the message is starred.</summary>
		public required bool IsStarred { get; init; }
	}
}
