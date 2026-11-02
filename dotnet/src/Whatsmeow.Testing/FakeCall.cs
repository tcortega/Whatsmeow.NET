namespace Whatsmeow.Testing;

/// <summary>One call the code under test made on a <see cref="FakeWhatsAppClient"/>, in <see cref="FakeWhatsAppClient.Calls"/>.</summary>
public sealed record FakeCall
{
	/// <summary>The member called, as <c>nameof(IWhatsAppClient.SendTextAsync)</c> names it.</summary>
	public required string Member { get; init; }

	/// <summary>
	/// The arguments by parameter name, without the cancellation token. A collection is a copy, taken when it was called;
	/// a stream to upload is the bytes read from it, an <c>ImmutableArray&lt;byte&gt;</c>.
	/// </summary>
	public required IReadOnlyDictionary<string, object?> Arguments { get; init; }

	/// <summary>When it was called, on the fake's <see cref="TimeProvider"/>.</summary>
	public required DateTimeOffset Timestamp { get; init; }
}
