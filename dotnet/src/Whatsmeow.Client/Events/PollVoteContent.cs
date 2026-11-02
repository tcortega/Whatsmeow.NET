using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using Whatsmeow.Messaging;

namespace Whatsmeow.Events;

/// <summary>
/// A vote on a poll, from the message's sender, decrypted with the poll's message secret. It replaces the voter's earlier
/// vote on the poll; each vote is delivered, and keeping the latest is the application's. WhatsApp names an option by
/// a hash of its name (<see cref="HashOption"/>), so the application, which holds the poll's options, tells which were
/// selected with <see cref="Selects"/>. Its hashes compare by content.
/// </summary>
public sealed record PollVoteContent : MessageContent
{
	/// <summary>The poll voted on; it may be one the application never saw.</summary>
	public required MessageKey Poll { get; init; }

	/// <summary>
	/// The selected options, each the <see cref="HashOption"/> of an option's name; empty when the voter cleared their
	/// vote, and when the vote is undecryptable.
	/// </summary>
	public ImmutableArray<ImmutableArray<byte>> SelectedOptionHashes { get; init; } = [];

	/// <summary>
	/// True when the vote could not be decrypted, so what it selected is unknown: this device has no secret for the poll,
	/// one that predates the device and that no history sync it processed carried, or the vote does not decrypt with it.
	/// </summary>
	public bool IsUndecryptable { get; init; }

	/// <summary>The hash WhatsApp names an option by: the SHA-256 of its name's UTF-8 bytes.</summary>
	/// <param name="option">The option's name, as the poll has it.</param>
	/// <exception cref="ArgumentNullException"><paramref name="option"/> is null.</exception>
	public static ImmutableArray<byte> HashOption(string option)
	{
		ArgumentNullException.ThrowIfNull(option);
		return [.. SHA256.HashData(Encoding.UTF8.GetBytes(option))];
	}

	/// <summary>True when the vote selected the option named <paramref name="option"/>.</summary>
	/// <param name="option">The option's name, as the poll has it.</param>
	/// <exception cref="ArgumentNullException"><paramref name="option"/> is null.</exception>
	public bool Selects(string option)
	{
		var hash = HashOption(option);
		foreach (var selected in SelectedOptionHashes.AsSpan())
		{
			if (selected.AsSpan().SequenceEqual(hash.AsSpan()))
				return true;
		}

		return false;
	}

	/// <summary>Compares every field, the hashes by content.</summary>
	public bool Equals(PollVoteContent? other)
	{
		if (other is null || !Equals(Poll, other.Poll) || IsUndecryptable != other.IsUndecryptable || SelectedOptionHashes.Length != other.SelectedOptionHashes.Length)
			return false;

		for (var i = 0; i < SelectedOptionHashes.Length; i++)
		{
			if (!SelectedOptionHashes[i].AsSpan().SequenceEqual(other.SelectedOptionHashes[i].AsSpan()))
				return false;
		}

		return true;
	}

	/// <inheritdoc/>
	public override int GetHashCode() => HashCode.Combine(Poll, IsUndecryptable, SelectedOptionHashes.Length);
}
