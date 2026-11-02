namespace Whatsmeow.LiveTests;

/// <summary>
/// Numbered messages a live test sends, each with a new id and a text unique to the run, so
/// <see cref="IntegrationTests.Events.DeliveryChecks"/> tells them from the account's other traffic and sees a delivery
/// under another id.
/// </summary>
public sealed class Burst
{
	private Burst(IReadOnlyList<(string Text, MessageId Id)> messages)
	{
		Messages = messages;
		ByText = messages.ToDictionary(m => m.Text, m => m.Id, StringComparer.Ordinal);
	}

	/// <summary>The messages, in the order they are sent.</summary>
	public IReadOnlyList<(string Text, MessageId Id)> Messages { get; }

	/// <summary>Each message's id by its text.</summary>
	public IReadOnlyDictionary<string, MessageId> ByText { get; }

	/// <summary>One message with this text and id.</summary>
	public static Burst One(string text, MessageId id) => new([(text, id)]);

	/// <summary><paramref name="count"/> messages "wmn &lt;test&gt; &lt;run&gt; #k".</summary>
	public static Burst Of(string test, int count)
	{
		var run = LiveClock.Time.GetUtcNow().ToUnixTimeSeconds();
		return new Burst([.. Enumerable.Range(1, count).Select(k => ($"wmn {test} {run} #{k}", MessageId.New()))]);
	}
}
