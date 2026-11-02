using System.Collections.Immutable;
using System.Text;
using E2E = Whatsmeow.Proto.WAWebProtobufsE2E;

namespace Whatsmeow.Events;

/// <summary>A message received, or sent from another of the account's devices.</summary>
public sealed record MessageReceived : WhatsAppEvent
{
	/// <summary>The WhatsApp message id; with <see cref="WhatsAppEvent.SessionId"/>, the dedupe key.</summary>
	public required MessageId Id { get; init; }

	/// <summary>Who and where.</summary>
	public required MessageSource Source { get; init; }

	/// <summary>The sender's push name; null when WhatsApp sent none.</summary>
	public required string? PushName { get; init; }

	/// <summary>The message's WhatsApp timestamp.</summary>
	public required DateTimeOffset Timestamp { get; init; }

	/// <summary>What it says.</summary>
	public required MessageContent Content { get; init; }

	/// <summary>The message it replies to; null when it is no reply.</summary>
	public QuotedMessage? ReplyTo { get; init; }

	/// <summary>The users it mentions; empty for none.</summary>
	public ImmutableArray<Jid> Mentions { get; init; } = [];

	/// <summary>True for a disappearing message, which the application should delete when the chat's timer says.</summary>
	public bool IsEphemeral { get; init; }

	/// <summary>
	/// For a message the account sent to a user from another of its devices, the chat it went to, as that device said;
	/// null for every other message.
	/// </summary>
	public Jid? DestinationChat { get; init; }

	/// <summary>
	/// The message as received, before whatsmeow removed its wrappers (device-sent, ephemeral, view-once, edit and
	/// similar); null when the event did not come from the sidecar. Its type is whatsmeow's, which changes with every
	/// whatsmeow version, so this member is experimental (<c>WMN1001</c>): it has no stability promise. It is not part
	/// of the record's equality, which compares the stable members only.
	/// </summary>
	[Experimental("WMN1001")]
	public E2E.Message? Raw { get; init; }

	/// <summary>
	/// Compares every stable member, the mentions by content: a message that comes again equals the first. The
	/// experimental <see cref="Raw"/> is left out, so the equality of this record never depends on whatsmeow's types.
	/// </summary>
	public bool Equals(MessageReceived? other) =>
		base.Equals(other)
		&& Id.Equals(other.Id)
		&& Source.Equals(other.Source)
		&& string.Equals(PushName, other.PushName, StringComparison.Ordinal)
		&& Timestamp.Equals(other.Timestamp)
		&& Content.Equals(other.Content)
		&& Equals(ReplyTo, other.ReplyTo)
		&& Mentions.AsSpan().SequenceEqual(other.Mentions.AsSpan())
		&& IsEphemeral == other.IsEphemeral
		&& Nullable.Equals(DestinationChat, other.DestinationChat);

	/// <summary>
	/// Prints every member but the experimental <see cref="Raw"/>: protobuf's text of a message carries what it holds,
	/// media keys included, which must not reach a log.
	/// </summary>
	protected override bool PrintMembers(StringBuilder builder)
	{
		ArgumentNullException.ThrowIfNull(builder);
		if (base.PrintMembers(builder))
			_ = builder.Append(", ");
		_ = builder.Append("Id = ").Append(Id);
		_ = builder.Append(", Source = ").Append(Source);
		_ = builder.Append(", PushName = ").Append(PushName);
		_ = builder.Append(", Timestamp = ").Append(Timestamp);
		_ = builder.Append(", Content = ").Append(Content);
		_ = builder.Append(", ReplyTo = ").Append(ReplyTo);
		_ = builder.Append(", Mentions = [").AppendJoin(", ", Mentions.AsSpan().ToArray()).Append(']');
		_ = builder.Append(", IsEphemeral = ").Append(IsEphemeral);
		_ = builder.Append(", DestinationChat = ").Append(DestinationChat);
		return true;
	}

	/// <inheritdoc/>
	public override int GetHashCode()
	{
		var hash = new HashCode();
		hash.Add(base.GetHashCode());
		hash.Add(Id);
		hash.Add(Source);
		hash.Add(Content);
		foreach (var mention in Mentions.AsSpan())
			hash.Add(mention);
		return hash.ToHashCode();
	}
}
