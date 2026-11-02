using Whatsmeow.Events;

namespace Whatsmeow.IntegrationTests.Events;

/// <summary>
/// What delivery across a crash must look like (ARCHITECTURE §8.4, invariants 2 and 3). The fake-mode kill tests and
/// <c>Whatsmeow.LiveTests</c> check a run with these same assertions (docs/guidelines/tests.md Live WhatsApp). A test
/// names each message it sent by its text, in the order it sent them, so a delivery under another id is seen, not
/// taken for another message. Received messages whose text the test did not send (a real account's other traffic) are
/// ignored. Each check compares (text, id, session) tuples in order, so a failure names the message, not a whole record.
/// </summary>
internal static class DeliveryChecks
{
	/// <summary>After a sidecar crash: every sent message reached the handlers exactly once, in order, with its id and the session.</summary>
	/// <param name="sent">The messages, by text and id, in the order they were sent.</param>
	public static void ValidateEachOnce(IReadOnlyList<(string Text, MessageId Id)> sent, IReadOnlyList<MessageReceived> received, SessionId session) =>
		Assert.Equal(Expected(sent, session), Ours(sent, received));

	/// <summary>
	/// After the .NET side was killed: nothing is lost, the events at or below the last ack (<paramref name="acked"/>)
	/// never come again, and each message after it comes exactly once more, in order, with the same id and session.
	/// </summary>
	/// <param name="sent">The messages, by text and id, in the order they were sent.</param>
	/// <param name="acked">The ids the killed process handled and acked.</param>
	/// <param name="receivedAfter">What the next process received.</param>
	public static void ValidateRepeatsFollowTheAck(
		IReadOnlyList<(string Text, MessageId Id)> sent,
		IReadOnlyCollection<MessageId> acked,
		IReadOnlyList<MessageReceived> receivedAfter,
		SessionId session
	)
	{
		var after = Ours(sent, receivedAfter);
		Assert.DoesNotContain(after, m => acked.Contains(m.Id));
		foreach (var (text, id) in sent.Where(m => !acked.Contains(m.Id)))
			Assert.True(after.Any(m => m.Id == id), $"message {id} (\"{text}\") was lost");
		Assert.Equal(Expected([.. sent.Where(m => !acked.Contains(m.Id))], session), after);
	}

	/// <summary>The text of a received text message, or null.</summary>
	public static string? TextOf(MessageReceived message) => message.Content is TextContent text ? text.Text : null;

	private static List<(string? Text, MessageId Id, SessionId Session)> Expected(IReadOnlyList<(string Text, MessageId Id)> sent, SessionId session) =>
		[.. sent.Select(m => ((string?)m.Text, m.Id, session))];

	private static List<(string? Text, MessageId Id, SessionId Session)> Ours(IReadOnlyList<(string Text, MessageId Id)> sent, IReadOnlyList<MessageReceived> received) =>
		[.. received.Where(m => TextOf(m) is { } text && sent.Any(s => string.Equals(s.Text, text, StringComparison.Ordinal))).Select(m => (TextOf(m), m.Id, m.SessionId))];
}
