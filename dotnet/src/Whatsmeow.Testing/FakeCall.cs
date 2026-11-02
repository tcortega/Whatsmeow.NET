namespace Whatsmeow.Testing;

/// <summary>
/// One call the code under test made on a <see cref="FakeWhatsAppClient"/>, in <see cref="FakeWhatsAppClient.Calls"/>.
/// Two calls are equal when their member, their time and their arguments are: the same names, each with a value equal to
/// the other's by its own <see cref="object.Equals(object)"/>, so a test can compare a whole call with one it builds.
/// </summary>
public sealed record FakeCall
{
	/// <summary>The member called, as <c>nameof(IWhatsAppClient.SendTextAsync)</c> names it.</summary>
	public required string Member { get; init; }

	/// <summary>
	/// The arguments by parameter name, without the cancellation token. A collection is a copy, taken when it was called;
	/// a stream to upload is the bytes read from it, an <c>ImmutableArray&lt;byte&gt;</c>. A value compares as its own
	/// type does, so an <c>ImmutableArray</c> equals only itself: compare a collection argument on its own, by sequence.
	/// </summary>
	public required IReadOnlyDictionary<string, object?> Arguments { get; init; }

	/// <summary>When it was called, on the fake's <see cref="TimeProvider"/>.</summary>
	public required DateTimeOffset Timestamp { get; init; }

	/// <inheritdoc/>
	public bool Equals(FakeCall? other) =>
		other is not null
		&& string.Equals(Member, other.Member, StringComparison.Ordinal)
		&& Timestamp == other.Timestamp
		&& SameArguments(Arguments, other.Arguments);

	/// <inheritdoc/>
	public override int GetHashCode() => HashCode.Combine(StringComparer.Ordinal.GetHashCode(Member), Timestamp, Arguments.Count);

	private static bool SameArguments(IReadOnlyDictionary<string, object?> arguments, IReadOnlyDictionary<string, object?> other)
	{
		if (ReferenceEquals(arguments, other))
			return true;
		if (arguments.Count != other.Count)
			return false;

		// Copied, so the names compare by default equality whatever comparer either dictionary was made with.
		var byName = new Dictionary<string, object?>(other.Count, StringComparer.Ordinal);
		foreach (var (name, value) in other)
			byName[name] = value;

		foreach (var (name, value) in arguments)
		{
			if (!byName.TryGetValue(name, out var theirs) || !EqualityComparer<object?>.Default.Equals(value, theirs))
				return false;
		}

		return true;
	}
}
