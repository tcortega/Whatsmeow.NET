using System.Collections.Immutable;

namespace Whatsmeow.Messaging;

/// <summary>What a text or media message says beyond its content: the message it replies to, and who it mentions.</summary>
public sealed record SendOptions
{
	/// <summary>The message this one replies to, which the recipient sees quoted; null for none.</summary>
	public MessageReply? ReplyTo { get; init; }

	/// <summary>
	/// The users mentioned, by phone-number JID or LID; empty for none. The text or caption names each as <c>@</c> and
	/// the JID's user part, which is how WhatsApp shows a mention; the library does not change it. Which address form a
	/// LID-addressed group needs is unknown.
	/// </summary>
	public ImmutableArray<Jid> Mentions { get; init; } = [];

	/// <summary>Compares every field, the mentions by content.</summary>
	public bool Equals(SendOptions? other) =>
		other is not null && Equals(ReplyTo, other.ReplyTo) && Mentions.AsSpan().SequenceEqual(other.Mentions.AsSpan());

	/// <inheritdoc/>
	public override int GetHashCode()
	{
		var hash = new HashCode();
		hash.Add(ReplyTo);
		foreach (var mention in Mentions.AsSpan())
			hash.Add(mention);
		return hash.ToHashCode();
	}
}
