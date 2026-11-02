using Whatsmeow.Events;

namespace Whatsmeow.IntegrationTests.Events;

/// <summary>
/// What delivery across a crash must look like (ARCHITECTURE §8.4, invariants 2 and 3). The fake-mode kill tests and
/// <c>Whatsmeow.LiveTests</c> check a run with these same assertions (docs/guidelines/tests.md Live WhatsApp). A test
/// names each message it sent by its text, so a delivery under another id is seen, not taken for another message.
/// Received messages whose text the test did not send (a real account's other traffic) are ignored.
/// </summary>
internal static class DeliveryChecks
{
	/// <summary>After a sidecar crash: every sent message reached the handlers exactly once, with its id and the session.</summary>
	public static void ValidateEachOnce(IReadOnlyDictionary<string, MessageId> sent, IReadOnlyList<MessageReceived> received, SessionId session)
	{
		var ours = Ours(sent, received);
		ValidateIdentity(sent, ours, session);
		Assert.Equal(sent.Keys.Order(StringComparer.Ordinal), ours.Select(m => TextOf(m)).Order(StringComparer.Ordinal));
	}

	/// <summary>
	/// After the .NET side was killed: nothing is lost, the events at or below the last ack (<paramref name="acked"/>)
	/// never come again, and each message after it comes at most once more, with the same id and session.
	/// </summary>
	/// <param name="acked">The ids the killed process handled and acked.</param>
	/// <param name="receivedAfter">What the next process received.</param>
	public static void ValidateRepeatsFollowTheAck(
		IReadOnlyDictionary<string, MessageId> sent,
		IReadOnlyCollection<MessageId> acked,
		IReadOnlyList<MessageReceived> receivedAfter,
		SessionId session
	)
	{
		var after = Ours(sent, receivedAfter);
		ValidateIdentity(sent, after, session);
		Assert.DoesNotContain(after, m => acked.Contains(m.Id));
		Assert.Distinct(after.Select(m => m.Id));
		Assert.All(sent.Values, id => Assert.True(acked.Contains(id) || after.Any(m => m.Id == id), $"message {id} was lost"));
	}

	/// <summary>The text of a received text message, or null.</summary>
	public static string? TextOf(MessageReceived message) => message.Content is TextContent text ? text.Text : null;

	private static List<MessageReceived> Ours(IReadOnlyDictionary<string, MessageId> sent, IReadOnlyList<MessageReceived> received) =>
		[.. received.Where(m => TextOf(m) is { } text && sent.ContainsKey(text))];

	private static void ValidateIdentity(IReadOnlyDictionary<string, MessageId> sent, List<MessageReceived> ours, SessionId session) =>
		Assert.All(
			ours,
			m =>
			{
				Assert.Equal(session, m.SessionId);
				Assert.Equal(sent[TextOf(m)!], m.Id);
			}
		);
}
